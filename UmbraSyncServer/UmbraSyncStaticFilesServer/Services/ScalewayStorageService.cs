using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

public sealed class ScalewayStorageService : IHostedService, IDisposable
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _config;
    private readonly ILogger<ScalewayStorageService> _logger;
    private readonly ConcurrentQueue<(string Hash, string FilePath)> _uploadQueue = new();
    private readonly SemaphoreSlim _uploadSemaphore = new(3);
    private readonly CancellationTokenSource _cts = new();
    private IAmazonS3? _s3Client;
    private Task? _processingTask;
    private Task? _periodicScanTask;
    private bool _disposed;
    private readonly ConcurrentDictionary<string, byte> _pendingUploads = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled => _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ScalewayEnabled), false);

    public ScalewayStorageService(
        IConfigurationService<StaticFilesServerConfiguration> config,
        ILogger<ScalewayStorageService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("Scaleway storage is disabled");
            return Task.CompletedTask;
        }

        InitializeS3Client();
        _processingTask = ProcessUploadQueueAsync(_cts.Token);
        _periodicScanTask = PeriodicSyncScanAsync(_cts.Token);
        _logger.LogInformation("Scaleway storage service started (sync scan at startup + every 10 minutes)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;

        _cts.Cancel();

        var tasks = new List<Task>();
        if (_processingTask != null) tasks.Add(_processingTask);
        if (_periodicScanTask != null) tasks.Add(_periodicScanTask);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("Scaleway storage service stopped with {Count} items remaining in queue", _uploadQueue.Count);
    }

    private void InitializeS3Client()
    {
        var accessKey = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayAccessKey));
        var secretKey = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewaySecretKey));
        var endpoint = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayEndpoint));
        var region = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayRegion));

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = region
        };

        _s3Client = new AmazonS3Client(accessKey, secretKey, s3Config);
    }

    public void QueueUpload(string hash, string filePath)
    {
        if (!IsEnabled || _s3Client == null) return;

        _uploadQueue.Enqueue((hash, filePath));
        _pendingUploads.TryAdd(hash, 0);
        _logger.LogDebug("Queued file {Hash} for Scaleway upload, queue size: {Size}", hash, _uploadQueue.Count);
    }

    public async Task<bool> FileExistsAsync(string hash, CancellationToken ct = default)
    {
        if (!IsEnabled || _s3Client == null) return false;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var key = GetS3Key(hash);

        try
        {
            await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // --- Traitement de la queue d'upload ---

    private async Task ProcessUploadQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_uploadQueue.TryDequeue(out var item))
                {
                    await _uploadSemaphore.WaitAsync(ct).ConfigureAwait(false);
                    _ = UploadFileWithCheckAsync(item.Hash, item.FilePath, ct)
                        .ContinueWith(_ => _uploadSemaphore.Release(), TaskScheduler.Default);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in upload queue processing");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }
    
    private async Task UploadFileWithCheckAsync(string hash, string filePath, CancellationToken ct)
    {
        try
        {
            if (_s3Client == null) return;

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File {FilePath} no longer exists, skipping upload", filePath);
                return;
            }

            // Vérifier si le fichier existe déjà sur S3
            if (await FileExistsAsync(hash, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("File {Hash} already exists on S3, skipping upload", hash);
                return;
            }

            await UploadFileAsync(hash, filePath, ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingUploads.TryRemove(hash, out _);
        }
    }

    private async Task UploadFileAsync(string hash, string filePath, CancellationToken ct)
    {
        if (_s3Client == null) return;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var key = GetS3Key(hash);

        const int maxRetries = 5;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File {FilePath} no longer exists, skipping upload", filePath);
                    return;
                }

                var fileSize = new FileInfo(filePath).Length;
                _logger.LogInformation("S3 upload starting: {Hash} ({Size} bytes, attempt {Attempt}/{MaxRetries})",
                    hash, fileSize, attempt, maxRetries);

                using var transferUtility = new TransferUtility(_s3Client);
                var uploadRequest = new TransferUtilityUploadRequest
                {
                    FilePath = filePath,
                    BucketName = bucketName,
                    Key = key,
                    ContentType = "application/octet-stream",
                    StorageClass = S3StorageClass.Standard,
                    CannedACL = S3CannedACL.PublicRead
                };

                await transferUtility.UploadAsync(uploadRequest, ct).ConfigureAwait(false);
                _logger.LogInformation("S3 upload success: {Hash} ({Size} bytes)", hash, fileSize);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(5 * attempt);
                    _logger.LogWarning(ex, "S3 upload failed: {Hash} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}s",
                        hash, attempt, maxRetries, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError(ex, "S3 upload abandoned: {Hash} after {MaxRetries} attempts", hash, maxRetries);
                }
            }
        }
    }

    //Scan périodique de rattrapage

    private async Task PeriodicSyncScanAsync(CancellationToken ct)
    {
        // Scan immédiat au démarrage (attendre 10s pour l'init du serveur)
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSyncScanAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic S3 sync scan");
            }

            // Scan toutes les 10 minutes
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSyncScanAsync(CancellationToken ct)
    {
        if (_s3Client == null) return;

        var cacheDir = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
        {
            _logger.LogWarning("Cache directory {Dir} does not exist, skipping sync scan", cacheDir);
            return;
        }

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));

        _logger.LogInformation("Starting periodic S3 sync scan of {Dir}", cacheDir);

        // Récupérer tous les objets présents sur S3
        var s3Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? continuationToken = null;
            do
            {
                var listRequest = new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(listRequest, ct).ConfigureAwait(false);
                foreach (var obj in response.S3Objects)
                {
                    // La clé est au format "{char}/{hash}", extraire le hash
                    var slashIdx = obj.Key.IndexOf('/');
                    var hash = slashIdx >= 0 ? obj.Key[(slashIdx + 1)..] : obj.Key;
                    s3Keys.Add(hash);
                }

                continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
            } while (continuationToken != null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list S3 objects during sync scan");
            return;
        }

        _logger.LogInformation("S3 sync scan: {S3Count} objects found on S3", s3Keys.Count);

        // Scanner les fichiers locaux et trouver ceux manquants sur S3
        int queued = 0;
        foreach (var subDir in Directory.EnumerateDirectories(cacheDir))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var filePath in Directory.EnumerateFiles(subDir))
            {
                ct.ThrowIfCancellationRequested();

                var hash = Path.GetFileName(filePath);

                // Ignorer si déjà sur S3 ou déjà dans la queue
                if (s3Keys.Contains(hash)) continue;
                if (_pendingUploads.ContainsKey(hash)) continue;

                QueueUpload(hash, filePath);
                queued++;
            }
        }

        if (queued > 0)
            _logger.LogWarning("S3 sync scan: queued {Count} missing files for upload", queued);
        else
            _logger.LogInformation("S3 sync scan: all local files are in sync with S3");
    }

    private static string GetS3Key(string hash)
    {
        return $"{hash[0]}/{hash}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _uploadSemaphore.Dispose();
        _s3Client?.Dispose();
    }
}
