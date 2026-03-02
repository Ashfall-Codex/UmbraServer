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
        _logger.LogInformation("Scaleway storage service started (sync scan at startup + every 5 minutes)");
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

    public async Task<bool> FileExistsAsync(string hash, long expectedSize, CancellationToken ct = default)
    {
        if (!IsEnabled || _s3Client == null) return false;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var key = GetS3Key(hash);

        try
        {
            var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
            if (metadata.ContentLength != expectedSize)
            {
                _logger.LogWarning("S3 size mismatch for {Hash}: expected {Expected} bytes, got {Actual} bytes on S3",
                    hash, expectedSize, metadata.ContentLength);
                return false;
            }
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 HEAD request failed for {Hash} (HTTP {StatusCode}), assuming file does not exist", hash, ex.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "S3 HEAD request error for {Hash}, assuming file does not exist", hash);
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
                    var capturedItem = item;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await UploadFileWithCheckAsync(capturedItem.Hash, capturedItem.FilePath, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _uploadSemaphore.Release();
                        }
                    }, ct);
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

            var localSize = new FileInfo(filePath).Length;
            if (await FileExistsAsync(hash, localSize, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("File {Hash} already exists on S3 with correct size, skipping upload", hash);
                return;
            }

            await UploadFileAsync(hash, filePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 upload check/upload failed for {Hash}, re-queuing for next sync scan", hash);
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

                // Vérification post-upload : HEAD request pour confirmer la persistance sur Scaleway
                try
                {
                    await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
                    _logger.LogInformation("S3 upload success: {Hash} ({Size} bytes)", hash, fileSize);
                    return;
                }
                catch (AmazonS3Exception verifyEx) when (verifyEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("S3 upload for {Hash} returned success but HEAD verification failed (not found), attempt {Attempt}/{MaxRetries}",
                        hash, attempt, maxRetries);
                }
                catch (Exception verifyEx) when (verifyEx is not OperationCanceledException)
                {
                    _logger.LogWarning(verifyEx, "S3 upload for {Hash} returned success but HEAD verification errored, attempt {Attempt}/{MaxRetries}",
                        hash, attempt, maxRetries);
                }

                if (attempt < maxRetries)
                {
                    var verifyDelay = TimeSpan.FromSeconds(3 * attempt);
                    await Task.Delay(verifyDelay, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError("S3 upload abandoned: {Hash} after {MaxRetries} attempts (upload succeeds but verification fails)", hash, maxRetries);
                }
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
        // Scan immédiat au démarrage (attendre 5s pour l'init du serveur)
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
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

            // Scan toutes les 5 minutes
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
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

        // Récupérer tous les objets présents sur S3 avec leurs tailles
        var s3Objects = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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
                    s3Objects[hash] = obj.Size;
                }

                continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
            } while (continuationToken != null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list S3 objects during sync scan");
            return;
        }

        _logger.LogInformation("S3 sync scan: {S3Count} objects found on S3", s3Objects.Count);

        // Scanner les fichiers locaux et comparer existence + taille
        int totalScanned = 0;
        int missing = 0;
        int sizeMismatch = 0;
        int inSync = 0;
        int skippedTemp = 0;
        int skippedPending = 0;

        foreach (var subDir in Directory.EnumerateDirectories(cacheDir))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var filePath in Directory.EnumerateFiles(subDir))
            {
                ct.ThrowIfCancellationRequested();

                var hash = Path.GetFileName(filePath);

                // Ignorer les fichiers temporaires (uploads en cours)
                if (hash.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || hash.EndsWith(".dl", StringComparison.OrdinalIgnoreCase))
                {
                    skippedTemp++;
                    continue;
                }

                totalScanned++;

                if (_pendingUploads.ContainsKey(hash))
                {
                    skippedPending++;
                    continue;
                }

                if (!s3Objects.TryGetValue(hash, out var s3Size))
                {
                    missing++;
                    QueueUpload(hash, filePath);
                    continue;
                }

                var localSize = new FileInfo(filePath).Length;
                if (s3Size != localSize)
                {
                    sizeMismatch++;
                    _logger.LogWarning("S3 sync scan: size mismatch for {Hash} (local: {LocalSize} bytes, S3: {S3Size} bytes), re-queuing",
                        hash, localSize, s3Size);
                    QueueUpload(hash, filePath);
                    continue;
                }

                inSync++;
            }
        }

        var queued = missing + sizeMismatch;
        _logger.LogInformation(
            "S3 sync scan complete: {Total} files scanned, {InSync} in sync, {Missing} missing, {SizeMismatch} size mismatch, {SkippedTemp} temp skipped, {SkippedPending} pending skipped, {Queued} queued for upload",
            totalScanned, inSync, missing, sizeMismatch, skippedTemp, skippedPending, queued);
    }

    public async Task<HashSet<string>> GetS3HashSetAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_s3Client == null) return result;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
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
                var slashIdx = obj.Key.IndexOf('/');
                var hash = slashIdx >= 0 ? obj.Key[(slashIdx + 1)..] : obj.Key;
                result.Add(hash);
            }

            continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        return result;
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
