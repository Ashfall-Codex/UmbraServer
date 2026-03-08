using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using MareSynchronosShared.Data;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

public sealed class ScalewayStorageService : IHostedService, IDisposable
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _config;
    private readonly ILogger<ScalewayStorageService> _logger;
    private readonly IServiceProvider _services;
    private readonly CancellationTokenSource _cts = new();
    private IAmazonS3? _s3Client;
    private Task? _syncWorkerTask;
    private Task? _integrityScanTask;
    private bool _disposed;

    // Garde contre les uploads simultanés du même hash (inline + worker)
    private readonly ConcurrentDictionary<string, byte> _uploadsInFlight = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled => _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ScalewayEnabled), false);

    private string CacheDirectory => _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)
        ? _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory))
        : _config.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));

    public ScalewayStorageService(
        IConfigurationService<StaticFilesServerConfiguration> config,
        ILogger<ScalewayStorageService> logger,
        IServiceProvider services)
    {
        _config = config;
        _logger = logger;
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("Scaleway storage is disabled");
            return Task.CompletedTask;
        }

        InitializeS3Client();
        _syncWorkerTask = SyncWorkerLoopAsync(_cts.Token);
        _integrityScanTask = IntegrityScanLoopAsync(_cts.Token);
        _logger.LogInformation("Scaleway storage service started (DB-driven sync worker + integrity scan)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;

        _cts.Cancel();

        var tasks = new List<Task>();
        if (_syncWorkerTask != null) tasks.Add(_syncWorkerTask);
        if (_integrityScanTask != null) tasks.Add(_integrityScanTask);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("Scaleway storage service stopped");
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

    //  Sync Worker : poll DB for S3Confirmed=false, upload + confirm

    private async Task SyncWorkerLoopAsync(CancellationToken ct)
    {
        // Laisser le serveur démarrer
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await SyncBatchAsync(ct).ConfigureAwait(false);

                // Si on a traité des fichiers, enchaîner immédiatement ; sinon attendre
                if (processed == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in S3 sync worker");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> SyncBatchAsync(CancellationToken ct)
    {
        if (_s3Client == null) return 0;

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var unsyncedFiles = await db.Files
            .Where(f => f.Uploaded && !f.S3Confirmed)
            .OrderBy(f => f.UploadDate)
            .Take(50)
            .Select(f => new { f.Hash, f.Size })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (unsyncedFiles.Count == 0) return 0;

        _logger.LogInformation("S3 sync: {Count} files to sync", unsyncedFiles.Count);

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var cacheDir = CacheDirectory;
        int synced = 0;
        int failed = 0;

        // Paralléliser avec un sémaphore pour limiter les uploads concurrents
        var semaphore = new SemaphoreSlim(10);
        var tasks = unsyncedFiles.Select(async file =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Skip si déjà en cours d'upload inline
                if (!_uploadsInFlight.TryAdd(file.Hash, 0))
                {
                    Interlocked.Increment(ref synced); // Compté comme traité
                    return;
                }

                var filePath = FilePathUtil.GetFilePath(cacheDir, file.Hash);
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("S3 sync: file {Hash} not found on disk, skipping", file.Hash);
                    Interlocked.Increment(ref failed);
                    return;
                }

                var localSize = new FileInfo(filePath).Length;
                var key = GetS3Key(file.Hash);

                // Vérifier si déjà présent avec la bonne taille
                bool alreadyOnS3 = false;
                try
                {
                    var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
                    alreadyOnS3 = metadata.ContentLength == localSize;
                    if (!alreadyOnS3)
                    {
                        _logger.LogWarning("S3 sync: size mismatch for {Hash} (local={Local}, S3={S3}), re-uploading",
                            file.Hash, localSize, metadata.ContentLength);
                    }
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Pas sur S3, on upload
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "S3 sync: HEAD check failed for {Hash}", file.Hash);
                }

                if (!alreadyOnS3)
                {
                    await UploadToS3Async(file.Hash, filePath, bucketName, ct).ConfigureAwait(false);
                }

                // Confirmer en DB
                await ConfirmS3Async(file.Hash, ct).ConfigureAwait(false);
                Interlocked.Increment(ref synced);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "S3 sync: failed for {Hash}", file.Hash);
                Interlocked.Increment(ref failed);
            }
            finally
            {
                _uploadsInFlight.TryRemove(file.Hash, out _);
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (synced > 0 || failed > 0)
        {
            _logger.LogInformation("S3 sync batch: {Synced} confirmed, {Failed} failed", synced, failed);
        }

        return unsyncedFiles.Count;
    }


    // Upload immédiat vers S3 + confirmation en DB.Retourne true si le fichier est confirmé sur S3, false sinon (le worker rattrapera).
    public async Task<bool> UploadAndConfirmAsync(string hash, string filePath, CancellationToken ct)
    {
        if (!IsEnabled || _s3Client == null) return false;
        if (!_uploadsInFlight.TryAdd(hash, 0)) return false; // Déjà en cours

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        try
        {
            await UploadToS3Async(hash, filePath, bucketName, ct).ConfigureAwait(false);
            await ConfirmS3Async(hash, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Inline S3 upload failed for {Hash}, worker will retry", hash);
            return false;
        }
        finally
        {
            _uploadsInFlight.TryRemove(hash, out _);
        }
    }

    private async Task UploadToS3Async(string hash, string filePath, string bucketName, CancellationToken ct)
    {
        var key = GetS3Key(hash);
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(filePath))
                    throw new FileNotFoundException($"File {filePath} not found");

                var fileSize = new FileInfo(filePath).Length;
                _logger.LogDebug("S3 upload: {Hash} ({Size} bytes, attempt {A}/{Max})",
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

                // Vérification post-upload
                var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);
                if (metadata.ContentLength != fileSize)
                {
                    throw new InvalidOperationException(
                        $"S3 upload size mismatch for {hash}: uploaded {fileSize}, S3 reports {metadata.ContentLength}");
                }

                _logger.LogInformation("S3 upload success: {Hash} ({Size} bytes)", hash, fileSize);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxRetries) throw;

                var delay = TimeSpan.FromSeconds(3 * attempt);
                _logger.LogWarning(ex, "S3 upload failed: {Hash} (attempt {A}/{Max}), retrying in {D}s",
                    hash, attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ConfirmS3Async(string hash, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        await db.Files
            .Where(f => f.Hash == hash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.S3Confirmed, true)
                .SetProperty(f => f.S3ConfirmedAt, DateTime.UtcNow), ct)
            .ConfigureAwait(false);
    }

    //  Scan D'intégrité : re-verify old confirmed files

    private async Task IntegrityScanLoopAsync(CancellationToken ct)
    {
        // Premier scan après 2 minutes
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunIntegrityScanAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during S3 integrity scan");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunIntegrityScanAsync(CancellationToken ct)
    {
        if (_s3Client == null) return;

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);

        // Vérifier un batch de fichiers confirmés il y a >24h
        var filesToCheck = await db.Files
            .Where(f => f.S3Confirmed && f.S3ConfirmedAt != null && f.S3ConfirmedAt < cutoff)
            .OrderBy(f => f.S3ConfirmedAt)
            .Take(100)
            .Select(f => new { f.Hash, f.Size })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (filesToCheck.Count == 0) return;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        int verified = 0;
        int invalidated = 0;

        foreach (var file in filesToCheck)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var key = GetS3Key(file.Hash);
                var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);

                if (metadata.ContentLength == file.Size)
                {
                    // Toujours valide — rafraîchir le timestamp de confirmation
                    await db.Files
                        .Where(f => f.Hash == file.Hash)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.S3ConfirmedAt, DateTime.UtcNow), ct)
                        .ConfigureAwait(false);
                    verified++;
                }
                else
                {
                    _logger.LogWarning("Integrity scan: size mismatch for {Hash} (DB={DbSize}, S3={S3Size}), invalidating",
                        file.Hash, file.Size, metadata.ContentLength);
                    await InvalidateS3ConfirmationAsync(db, file.Hash, ct).ConfigureAwait(false);
                    invalidated++;
                }
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Integrity scan: {Hash} not found on S3, invalidating", file.Hash);
                await InvalidateS3ConfirmationAsync(db, file.Hash, ct).ConfigureAwait(false);
                invalidated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Integrity scan: HEAD check failed for {Hash}", file.Hash);
            }
        }

        _logger.LogInformation("Integrity scan: {Checked} checked, {Verified} OK, {Invalidated} invalidated",
            filesToCheck.Count, verified, invalidated);
    }

    private static async Task InvalidateS3ConfirmationAsync(MareDbContext db, string hash, CancellationToken ct)
    {
        await db.Files
            .Where(f => f.Hash == hash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.S3Confirmed, false)
                .SetProperty(f => f.S3ConfirmedAt, (DateTime?)null), ct)
            .ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  CDN Miss : vérification S3 + invalidation si absent
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vérifie sur S3 que les hashes existent réellement.
    /// Si un hash est absent (404) ou a une taille incohérente, on invalide S3Confirmed en DB.
    /// Appelé depuis ReportCdnMiss (fire-and-forget).
    /// </summary>
    public async Task VerifyAndInvalidateAsync(List<string> hashes, CancellationToken ct)
    {
        if (!IsEnabled || _s3Client == null || hashes.Count == 0) return;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        // Charger les tailles attendues depuis la DB
        var dbFiles = await db.Files
            .Where(f => hashes.Contains(f.Hash) && f.S3Confirmed)
            .Select(f => new { f.Hash, f.Size })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (dbFiles.Count == 0) return;

        int invalidated = 0;

        foreach (var file in dbFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var key = GetS3Key(file.Hash);
                var metadata = await _s3Client.GetObjectMetadataAsync(bucketName, key, ct).ConfigureAwait(false);

                if (metadata.ContentLength != file.Size)
                {
                    _logger.LogWarning("CDN miss verify: size mismatch for {Hash} (DB={DbSize}, S3={S3Size}), invalidating",
                        file.Hash, file.Size, metadata.ContentLength);
                    await InvalidateS3ConfirmationAsync(db, file.Hash, ct).ConfigureAwait(false);
                    invalidated++;
                }
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("CDN miss verify: {Hash} not found on S3, invalidating", file.Hash);
                await InvalidateS3ConfirmationAsync(db, file.Hash, ct).ConfigureAwait(false);
                invalidated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CDN miss verify: HEAD check failed for {Hash}", file.Hash);
            }
        }

        if (invalidated > 0)
        {
            _logger.LogInformation("CDN miss report: {Invalidated}/{Total} hashes invalidated", invalidated, dbFiles.Count);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Méthodes publiques pour FileCleanupService
    // ──────────────────────────────────────────────────────────────────────

    public async Task<HashSet<string>> GetS3HashSetAsync(CancellationToken ct)
    {
        if (_s3Client == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    public async Task<int> DeleteS3ObjectsAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        if (_s3Client == null) return 0;

        var bucketName = _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ScalewayBucketName));
        var deleted = 0;

        foreach (var batch in hashes.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var deleteRequest = new DeleteObjectsRequest
                {
                    BucketName = bucketName,
                    Objects = batch.Select(h => new KeyVersion { Key = GetS3Key(h) }).ToList()
                };

                var response = await _s3Client.DeleteObjectsAsync(deleteRequest, ct).ConfigureAwait(false);
                deleted += response.DeletedObjects.Count;

                if (response.DeleteErrors.Count > 0)
                {
                    foreach (var err in response.DeleteErrors)
                        _logger.LogWarning("S3 delete error: {Key} — {Code} {Message}", err.Key, err.Code, err.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "S3 batch delete failed for {Count} objects", batch.Length);
            }
        }

        return deleted;
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
        _s3Client?.Dispose();
    }
}