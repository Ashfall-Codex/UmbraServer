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
    private Task? _verifyRecentTask;
    private bool _disposed;
    private readonly ConcurrentDictionary<string, byte> _pendingUploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _retryCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string FilePath, DateTime QueuedAt)> _recentUploads = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxRequeueRetries = 3;

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
        _verifyRecentTask = VerifyRecentUploadsLoopAsync(_cts.Token);
        _logger.LogInformation("Scaleway storage service started (sync scan at startup + every 5 minutes, recent uploads verification every 30s)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;

        _cts.Cancel();

        var tasks = new List<Task>();
        if (_processingTask != null) tasks.Add(_processingTask);
        if (_periodicScanTask != null) tasks.Add(_periodicScanTask);
        if (_verifyRecentTask != null) tasks.Add(_verifyRecentTask);

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
        _recentUploads[hash] = (filePath, DateTime.UtcNow);
        _logger.LogInformation("Queued file {Hash} for S3 upload, queue size: {Size}", hash, _uploadQueue.Count);
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
                _pendingUploads.TryRemove(hash, out _);
                _retryCounts.TryRemove(hash, out _);
                _recentUploads.TryRemove(hash, out _);
                return;
            }

            var localSize = new FileInfo(filePath).Length;
            if (await FileExistsAsync(hash, localSize, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("File {Hash} already exists on S3 with correct size, skipping upload", hash);
                _pendingUploads.TryRemove(hash, out _);
                _retryCounts.TryRemove(hash, out _);
                _recentUploads.TryRemove(hash, out _);
                return;
            }

            await UploadFileAsync(hash, filePath, ct).ConfigureAwait(false);
            _pendingUploads.TryRemove(hash, out _);
            _retryCounts.TryRemove(hash, out _);
            _recentUploads.TryRemove(hash, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryCount = _retryCounts.AddOrUpdate(hash, 1, (_, count) => count + 1);

            if (retryCount <= MaxRequeueRetries)
            {
                _logger.LogWarning(ex, "S3 upload failed for {Hash} (attempt {Attempt}/{Max}), re-queuing in 10s",
                    hash, retryCount, MaxRequeueRetries);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10 * retryCount), ct).ConfigureAwait(false);
                        _uploadQueue.Enqueue((hash, filePath));
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
            }
            else
            {
                _logger.LogError(ex, "S3 upload permanently failed for {Hash} after {Max} re-queues, giving up (verification loop will retry later)",
                    hash, MaxRequeueRetries);
                _pendingUploads.TryRemove(hash, out _);
                _retryCounts.TryRemove(hash, out _);
            }
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
                    var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
                    _logger.LogInformation("S3 upload success: {Hash} ({Size} bytes)", hash, fileSize);
                    try { File.SetLastWriteTimeUtc(filePath, metadata.LastModified.ToUniversalTime()); }
                    catch { /* fichier peut avoir été supprimé entre-temps */ }
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
                    throw new InvalidOperationException($"S3 upload for {hash} succeeded but HEAD verification failed after {maxRetries} attempts");
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
                    throw;
                }
            }
        }
    }


    private async Task VerifyRecentUploadsLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await VerifyRecentUploadsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during recent uploads verification");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task VerifyRecentUploadsAsync(CancellationToken ct)
    {
        if (_s3Client == null || _recentUploads.IsEmpty) return;

        var now = DateTime.UtcNow;
        int verified = 0;
        int requeued = 0;
        int expired = 0;

        foreach (var kvp in _recentUploads)
        {
            ct.ThrowIfCancellationRequested();

            var hash = kvp.Key;
            var (filePath, queuedAt) = kvp.Value;
            var age = now - queuedAt;

            // Laisser le temps au pipeline d'upload de finir
            if (age < TimeSpan.FromSeconds(20))
                continue;

            // Trop vieux : le scan périodique prendra le relais
            if (age > TimeSpan.FromMinutes(10))
            {
                _recentUploads.TryRemove(hash, out _);
                expired++;
                continue;
            }

            // Encore dans le pipeline d'upload, ne pas interférer
            if (_pendingUploads.ContainsKey(hash))
                continue;

            // Vérifier si le fichier existe toujours sur disque
            if (!File.Exists(filePath))
            {
                _recentUploads.TryRemove(hash, out _);
                continue;
            }

            var localSize = new FileInfo(filePath).Length;
            try
            {
                if (await FileExistsAsync(hash, localSize, ct).ConfigureAwait(false))
                {
                    _recentUploads.TryRemove(hash, out _);
                    verified++;
                }
                else
                {
                    _logger.LogWarning("Verify: file {Hash} not found on S3 ({Age}s after queue), re-queuing",
                        hash, (int)age.TotalSeconds);
                    _retryCounts.TryRemove(hash, out _);
                    QueueUpload(hash, filePath);
                    requeued++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Verify: HEAD check failed for {Hash}", hash);
            }
        }

        if (verified > 0 || requeued > 0)
        {
            _logger.LogInformation("Verify recent uploads: {Verified} confirmed on S3, {Requeued} re-queued, {Expired} expired, {Remaining} still tracking",
                verified, requeued, expired, _recentUploads.Count);
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
        bool listingSucceeded = false;
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
                    var slashIdx = obj.Key.IndexOf('/');
                    var hash = slashIdx >= 0 ? obj.Key[(slashIdx + 1)..] : obj.Key;
                    s3Objects[hash] = obj.Size;
                }

                continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
            } while (continuationToken != null);

            listingSucceeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list S3 objects during sync scan, falling back to individual HEAD checks");
        }

        if (listingSucceeded)
        {
            _logger.LogInformation("S3 sync scan: {S3Count} objects found on S3", s3Objects.Count);
        }

        int totalScanned = 0;
        int missing = 0;
        int sizeMismatch = 0;
        int inSync = 0;
        int skippedTemp = 0;
        int skippedPending = 0;
        int headChecked = 0;
        const int maxHeadChecksOnFallback = 50;

        foreach (var subDir in Directory.EnumerateDirectories(cacheDir))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var filePath in Directory.EnumerateFiles(subDir))
            {
                ct.ThrowIfCancellationRequested();

                var hash = Path.GetFileName(filePath);

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

                if (listingSucceeded)
                {
                    if (!s3Objects.TryGetValue(hash, out var s3Size))
                    {
                        missing++;
                        _retryCounts.TryRemove(hash, out _);
                        QueueUpload(hash, filePath);
                        continue;
                    }

                    var localSize = new FileInfo(filePath).Length;
                    if (s3Size != localSize)
                    {
                        sizeMismatch++;
                        _logger.LogWarning("S3 sync scan: size mismatch for {Hash} (local: {LocalSize} bytes, S3: {S3Size} bytes), re-queuing",
                            hash, localSize, s3Size);
                        _retryCounts.TryRemove(hash, out _);
                        QueueUpload(hash, filePath);
                        continue;
                    }

                    _retryCounts.TryRemove(hash, out _);
                    _recentUploads.TryRemove(hash, out _);
                    inSync++;
                }
                else
                {
                    if (headChecked >= maxHeadChecksOnFallback)
                        continue;

                    var localSize = new FileInfo(filePath).Length;
                    try
                    {
                        headChecked++;
                        if (!await FileExistsAsync(hash, localSize, ct).ConfigureAwait(false))
                        {
                            missing++;
                            _retryCounts.TryRemove(hash, out _);
                            QueueUpload(hash, filePath);
                        }
                        else
                        {
                            _retryCounts.TryRemove(hash, out _);
                            _recentUploads.TryRemove(hash, out _);
                            inSync++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "HEAD check failed for {Hash} during fallback scan", hash);
                    }
                }
            }
        }

        var queued = missing + sizeMismatch;
        if (!listingSucceeded)
        {
            _logger.LogInformation(
                "S3 sync scan complete (fallback HEAD mode): {Total} files scanned, {InSync} in sync, {Missing} missing, {HeadChecked}/{MaxHead} HEAD checks used, {SkippedTemp} temp skipped, {SkippedPending} pending skipped, {Queued} queued for upload",
                totalScanned, inSync, missing, headChecked, maxHeadChecksOnFallback, skippedTemp, skippedPending, queued);
        }
        else
        {
            _logger.LogInformation(
                "S3 sync scan complete: {Total} files scanned, {InSync} in sync, {Missing} missing, {SizeMismatch} size mismatch, {SkippedTemp} temp skipped, {SkippedPending} pending skipped, {Queued} queued for upload",
                totalScanned, inSync, missing, sizeMismatch, skippedTemp, skippedPending, queued);
        }
    }

    public async Task<int> DeleteS3ObjectsAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        if (_s3Client == null) return 0;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var deleted = 0;

        foreach (var batch in hashes.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();

            var request = new DeleteObjectsRequest
            {
                BucketName = bucketName,
                Objects = batch.Select(h => new KeyVersion { Key = GetS3Key(h) }).ToList()
            };

            try
            {
                var response = await _s3Client.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
                deleted += response.DeletedObjects.Count;

                foreach (var error in response.DeleteErrors)
                {
                    _logger.LogWarning("S3 delete error for key {Key}: {Code} - {Message}", error.Key, error.Code, error.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "S3 batch delete failed for {Count} objects", batch.Length);
            }
        }

        return deleted;
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
