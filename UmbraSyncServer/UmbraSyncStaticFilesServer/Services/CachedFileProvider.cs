using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using MareSynchronosShared.Utils;
using UmbraSync.API.Routes;
using MareSynchronosShared.Utils.Configuration;
using K4os.Compression.LZ4.Streams;
using System.Security.Cryptography;

#nullable enable

namespace MareSynchronosStaticFilesServer.Services;

public sealed class CachedFileProvider : IDisposable
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ILogger<CachedFileProvider> _logger;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly MareMetrics _metrics;
    private readonly ServerTokenGenerator _generator;
    private readonly ITouchHashService _touchService;
    private readonly Uri? _remoteCacheSourceUri;
    private readonly bool _useColdStorage;
    private readonly string _hotStoragePath;
    private readonly string _coldStoragePath;
    private readonly ConcurrentDictionary<string, Task> _currentTransfers = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private readonly ScalewayStorageService _scaleway;
    // Hashes introuvables sur S3 récemment : évite de relancer un GET S3 à chaque requête du même fichier
    private readonly ConcurrentDictionary<string, DateTime> _s3Misses = new(StringComparer.Ordinal);
    private static readonly TimeSpan S3MissTtl = TimeSpan.FromMinutes(1);
    private const int MaxS3MissEntries = 10_000;
    private bool _disposed;

    private bool IsMainServer => _remoteCacheSourceUri == null && _isDistributionServer;
    private bool _isDistributionServer;

    public CachedFileProvider(IConfigurationService<StaticFilesServerConfiguration> configuration, ILogger<CachedFileProvider> logger,
        FileStatisticsService fileStatisticsService, MareMetrics metrics, ServerTokenGenerator generator, ITouchHashService touchService,
        ScalewayStorageService scaleway)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        _configuration = configuration;
        _logger = logger;
        _fileStatisticsService = fileStatisticsService;
        _metrics = metrics;
        _generator = generator;
        _touchService = touchService;
        _scaleway = scaleway;
        _remoteCacheSourceUri = configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.DistributionFileServerAddress), null);
        _isDistributionServer = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.IsDistributionNode), false);
        _useColdStorage = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);
        _hotStoragePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _coldStoragePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory));
        _httpClient = new();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MareSynchronosServer", "1.0.0.0"));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient?.Dispose();
    }

    // Écrit un fichier LZ4 dans un temporaire unique en vérifiant le hash de son contenu décompressé (SHA1, comme
    // à l'upload), puis le publie par un Move atomique. Un contenu dont le hash ne correspond pas n'est jamais publié,
    // et deux copies concurrentes du même hash n'écrivent jamais dans le même fichier.
    private async Task<bool> WriteVerifiedAsync(Stream source, string hash, string destinationFilePath, string origin, CancellationToken ct)
    {
        var tmpPath = destinationFilePath + "." + Guid.NewGuid().ToString("N") + ".dl";
        try
        {
            long compressedSize;
            string computedHash;
            using (var tmpFileStream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var teeStream = new TeeStream(source, tmpFileStream) { DisposeUnderlying = false };
                using var countStream = new CountedStream(teeStream) { DisposeUnderlying = false };
                using (var sha1 = SHA1.Create())
                {
                    using var decStream = LZ4Stream.Decode(countStream, extraMemory: 0, leaveOpen: true);
                    using var hashStream = new HashingStream(Stream.Null, sha1) { DisposeUnderlying = false };
                    await decStream.CopyToAsync(hashStream, ct).ConfigureAwait(false);
                    computedHash = Convert.ToHexString(hashStream.Finish());
                }

                // Le décodeur peut s'arrêter à la fin de la trame LZ4 : on recopie le reste pour une copie à l'identique
                await countStream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                compressedSize = countStream.BytesRead;
                await tmpFileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (compressedSize <= 0 || !string.Equals(computedHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Rejected {hash} from {origin}: computed hash {computed}, {size} bytes", hash, origin, computedHash, compressedSize);
                return false;
            }

            File.Move(tmpPath, destinationFilePath, true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verified copy of {hash} from {origin} failed", hash, origin);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete temp file for {hash}", hash);
            }
        }
    }

    private async Task<bool> DownloadTask(string hash, CancellationToken ct)
    {
        var destinationFilePath = FilePathUtil.GetFilePath(_useColdStorage ? _coldStoragePath : _hotStoragePath, hash);

        // if cold storage is not configured or file not found or error is present try to download file from remote
        var downloadUrl = MareFiles.DistributionGetFullPath(_remoteCacheSourceUri, hash);
        _logger.LogInformation("Did not find {hash}, downloading from {server}", hash, downloadUrl);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _generator.Token);
        if (_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DistributionFileServerForceHTTP2), false))
        {
            requestMessage.Version = new Version(2, 0);
            requestMessage.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to download {url}", downloadUrl);
            return false;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download {url}: {status}", downloadUrl, (int)response.StatusCode);
                return false;
            }

            bool published;
            await using (var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                published = await WriteVerifiedAsync(content, hash, destinationFilePath, "distribution", ct).ConfigureAwait(false);
            }

            if (!published) return false;
        }

        _metrics.IncGauge(_useColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(_useColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, new FileInfo(destinationFilePath).Length);
        return true;
    }

    // Serveur principal, dernier recours (SRV-7) : le fichier n'est ni sur le disque chaud ni dans le stockage froid,
    // on le récupère sur S3 avec la même vérification que les autres copies.
    private async Task<bool> DownloadFromS3Task(string hash, CancellationToken ct)
    {
        var destinationFilePath = FilePathUtil.GetFilePath(_useColdStorage ? _coldStoragePath : _hotStoragePath, hash);
        _logger.LogInformation("Did not find {hash} locally, fetching from S3", hash);

        // Délai borné dans ScalewayStorageService (120 s) ; null si absent, en erreur ou expiré
        var data = await _scaleway.TryDownloadObjectAsync(hash, ct).ConfigureAwait(false);
        if (data == null || data.Length == 0)
        {
            RememberS3Miss(hash);
            return false;
        }

        bool published;
        using (var memoryStream = new MemoryStream(data, writable: false))
        {
            published = await WriteVerifiedAsync(memoryStream, hash, destinationFilePath, "S3", ct).ConfigureAwait(false);
        }

        if (!published)
        {
            RememberS3Miss(hash);
            return false;
        }

        _metrics.IncGauge(_useColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(_useColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, data.Length);
        return true;
    }

    private bool IsRecentS3Miss(string hash)
    {
        return _s3Misses.TryGetValue(hash, out var until) && until > DateTime.UtcNow;
    }

    private void RememberS3Miss(string hash)
    {
        var now = DateTime.UtcNow;
        if (_s3Misses.Count > MaxS3MissEntries)
        {
            foreach (var entry in _s3Misses)
            {
                if (entry.Value <= now) _s3Misses.TryRemove(entry.Key, out _);
            }
        }

        _s3Misses[hash] = now + S3MissTtl;
    }

    private async Task<bool> TryCopyFromColdStorageAsync(string hash, string destinationFilePath, CancellationToken ct)
    {
        if (!_useColdStorage) return false;

        if (string.IsNullOrEmpty(_coldStoragePath)) return false;

        var coldStorageFilePath = FilePathUtil.GetFilePath(_coldStoragePath, hash);
        if (!File.Exists(coldStorageFilePath)) return false;

        _logger.LogDebug("Copying {hash} from cold storage: {path}", hash, coldStorageFilePath);
        bool copied;
        try
        {
            await using var source = new FileStream(coldStorageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            copied = await WriteVerifiedAsync(source, hash, destinationFilePath, "cold storage", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not copy {coldStoragePath} from cold storage", coldStorageFilePath);
            copied = false;
        }

        // Une copie concurrente du même hash a pu publier le fichier entre-temps
        if (!copied) return File.Exists(destinationFilePath);

        _metrics.IncGauge(MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(MetricsAPI.GaugeFilesTotalSize, new FileInfo(destinationFilePath).Length);
        return true;
    }

    private async Task StartTransferAsync(string hash, Func<CancellationToken, Task<bool>> download)
    {
        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentTransfers.TryGetValue(hash, out var downloadTask) && !(downloadTask?.IsCompleted ?? true))
                return;

            // Indépendant de la requête qui l'a déclenché : d'autres requêtes attendent le même fichier
            _currentTransfers[hash] = Task.Run(async () =>
            {
                try
                {
                    _metrics.IncGauge(MetricsAPI.GaugeFilesDownloadingFromCache);
                    if (await download(CancellationToken.None).ConfigureAwait(false))
                        await TryCopyFromColdStorageAsync(hash, FilePathUtil.GetFilePath(_hotStoragePath, hash), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during Download Task for {hash}", hash);
                }
                finally
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesDownloadingFromCache);
                    _currentTransfers.Remove(hash, out _);
                }
            });
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    // Returns FileInfo ONLY if the hot file was immediately available without downloading
    // Since the intended use is for pre-fetching files from hot storage, this is exactly what we need anyway
    public async Task<FileInfo?> DownloadFileWhenRequired(string hash, CancellationToken ct = default)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_hotStoragePath, hash);

        if (fi != null && fi.Length != 0)
            return fi;

        // first check cold storage
        if (await TryCopyFromColdStorageAsync(hash, FilePathUtil.GetFilePath(_hotStoragePath, hash), ct).ConfigureAwait(false))
            return null;

        if (_remoteCacheSourceUri != null)
        {
            await StartTransferAsync(hash, token => DownloadTask(hash, token)).ConfigureAwait(false);
            return null;
        }

        // Serveur principal : S3 en dernier recours. Sans S3 configuré, comportement inchangé.
        if (_scaleway.IsEnabled && !IsRecentS3Miss(hash))
            await StartTransferAsync(hash, token => DownloadFromS3Task(hash, token)).ConfigureAwait(false);

        return null;
    }

    public async Task<FileInfo?> GetAndDownloadFile(string hash, CancellationToken ct = default)
    {
        var fi = await DownloadFileWhenRequired(hash, ct).ConfigureAwait(false);

        if (fi == null && _currentTransfers.TryGetValue(hash, out var downloadTask))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));
                _metrics.IncGauge(MetricsAPI.GaugeFilesTasksWaitingForDownloadFromCache);
                await downloadTask.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed while waiting for download task for {hash}", hash);
                return null;
            }
            finally
            {
                _metrics.DecGauge(MetricsAPI.GaugeFilesTasksWaitingForDownloadFromCache);
            }
        }

        fi ??= FilePathUtil.GetFileInfoForHash(_hotStoragePath, hash);

        if (fi == null)
            return null;

        fi.LastAccessTimeUtc = DateTime.UtcNow;
        _touchService.TouchColdHash(hash);

        _fileStatisticsService.LogFile(hash, fi.Length);

        return fi;
    }

    public async Task<FileStream?> GetAndDownloadFileStream(string hash, CancellationToken ct = default)
    {
        var fi = await GetAndDownloadFile(hash, ct).ConfigureAwait(false);
        if (fi == null)
        {
            return null;
        }
        return new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Inheritable | FileShare.Read);
    }

    public void TouchColdHash(string hash)
    {
        _touchService.TouchColdHash(hash);
    }

    public bool AnyFilesDownloading(List<string> hashes)
    {
        return hashes.Exists(_currentTransfers.Keys.Contains);
    }
}
