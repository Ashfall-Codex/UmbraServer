using K4os.Compression.LZ4.Streams;
using UmbraSync.API.Dto.Files;
using UmbraSync.API.Routes;
using UmbraSync.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text.Json;
using System.Text.RegularExpressions;

#nullable enable

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.ServerFiles)]
public class ServerFilesController : ControllerBase
{
    private static readonly Dictionary<string, UploadLock> _fileUploadLocks = new(StringComparer.Ordinal);
    private static readonly Lock _fileUploadLocksGuard = new();
    private static readonly ConcurrentDictionary<string, DateTime> _cdnMissRateLimit = new(StringComparer.Ordinal);
    private readonly string _basePath;
    private readonly string _hotPath;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly IHubContext<MareHub> _hubContext;
    private readonly MareDbContext _mareDbContext;
    private readonly MareMetrics _metricsClient;
    private readonly ScalewayStorageService _scalewayStorage;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        IHubContext<MareHub> hubContext,
        MareDbContext mareDbContext, MareMetrics metricsClient,
        ScalewayStorageService scalewayStorage) : base(logger)
    {
        _configuration = configuration;
        _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _hotPath = _basePath;
        if (_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false))
            _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory));
        _cachedFileProvider = cachedFileProvider;
        _hubContext = hubContext;
        _mareDbContext = mareDbContext;
        _metricsClient = metricsClient;
        _scalewayStorage = scalewayStorage;
    }

    [HttpPost(MareFiles.ServerFiles_DeleteAll)]
    public IActionResult FilesDeleteAll() => Ok();

    [HttpPost(MareFiles.ServerFiles_GetSizes)]
    [Produces("application/json")]
    public async Task<IActionResult> FilesGetSizes([FromBody] List<string> hashes)
    {
        var requested = (hashes ?? new List<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length == 40 && h.All(c => char.IsAsciiHexDigit(c)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

        if (requested.Count == 0)
            return Ok(new List<DownloadFileDto>());

        var forbiddenFiles = await _mareDbContext.ForbiddenUploadEntries
            .Where(f => requested.Contains(f.Hash))
            .ToListAsync().ConfigureAwait(false);

        var cacheFiles = await _mareDbContext.Files
            .AsNoTracking()
            .Where(f => requested.Contains(f.Hash))
            .Select(k => new { k.Hash, k.Size, k.S3Confirmed, k.Uploaded })
            .ToListAsync().ConfigureAwait(false);

        var ghosts = await HealGhostRowsAsync(cacheFiles.Select(k => (k.Hash, k.Uploaded, k.S3Confirmed)), HttpContext.RequestAborted).ConfigureAwait(false);
        var cacheDict = cacheFiles
            .Where(k => k.Uploaded && !ghosts.Contains(k.Hash))
            .ToDictionary(k => k.Hash, k => k.Size, StringComparer.OrdinalIgnoreCase);
        var s3ConfirmedDict = cacheFiles.ToDictionary(k => k.Hash, k => k.S3Confirmed, StringComparer.OrdinalIgnoreCase);
        var forbiddenDict = forbiddenFiles.ToDictionary(f => f.Hash, f => f, StringComparer.OrdinalIgnoreCase);

        // BC7 : alternates compressés disponibles pour les hashes demandés.
        var bc7Conversions = await _mareDbContext.FileBc7Conversions
            .AsNoTracking()
            .Where(c => requested.Contains(c.SourceHash))
            .Select(c => new { c.SourceHash, c.State, c.AlternateHash, c.Role })
            .ToListAsync().ConfigureAwait(false);
        var convDict = bc7Conversions.ToDictionary(c => c.SourceHash, c => c, StringComparer.OrdinalIgnoreCase);

        var altHashes = bc7Conversions
            .Where(c => c.State == Bc7ConversionState.Converted && c.AlternateHash != null)
            .Select(c => c.AlternateHash!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var altRows = altHashes.Count == 0
            ? new List<(string Hash, long Size, bool S3Confirmed, bool Uploaded)>()
            : (await _mareDbContext.Files.AsNoTracking()
                .Where(f => altHashes.Contains(f.Hash))
                .Select(k => new { k.Hash, k.Size, k.S3Confirmed, k.Uploaded })
                .ToListAsync().ConfigureAwait(false))
                .Select(k => (k.Hash, k.Size, k.S3Confirmed, k.Uploaded)).ToList();
        var altGhosts = await HealGhostRowsAsync(altRows.Select(k => (k.Hash, k.Uploaded, k.S3Confirmed)), HttpContext.RequestAborted).ConfigureAwait(false);
        var altFiles = altRows
            .Where(k => k.Uploaded && !altGhosts.Contains(k.Hash))
            .Select(k => (k.Hash, k.Size, k.S3Confirmed)).ToList();
        var altDict = altFiles.ToDictionary(k => k.Hash, k => (k.Size, k.S3Confirmed), StringComparer.OrdinalIgnoreCase);
        var shardsConfig = _configuration.GetValueOrDefault<ICollection<CdnShardConfiguration>>(
            nameof(StaticFilesServerConfiguration.CdnShardConfiguration),
            Array.Empty<CdnShardConfiguration>());
        var allFileShards = new List<CdnShardConfiguration>(shardsConfig ?? Array.Empty<CdnShardConfiguration>());

        Uri? DefaultCdnUrlSafely()
        {
            try { return _configuration.GetValue<Uri>(nameof(StaticFilesServerConfiguration.CdnFullUrl)); }
            catch { return null; }
        }

        bool TryMatches(string pattern, string hash)
        {
            try { return new Regex(pattern).IsMatch(hash); }
            catch { return false; }
        }

        string BuildDirectDownloadUrl(string hash)
        {
            if (!s3ConfirmedDict.TryGetValue(hash, out var confirmed)) return string.Empty;
            return BuildDirectFor(hash, confirmed);
        }

        string BuildDirectFor(string hash, bool s3Confirmed)
        {
            if (!_scalewayStorage.IsEnabled) return string.Empty;
            if (!s3Confirmed) return string.Empty;
            var cdnUrl = DefaultCdnUrlSafely()?.ToString().TrimEnd('/');
            if (string.IsNullOrEmpty(cdnUrl)) return string.Empty;
            return $"{cdnUrl}/{hash[0]}/{hash}";
        }

        // Build a response for every requested hash (unknowns => FileExists=false, not 500)
        List<DownloadFileDto> response = new(requested.Count);
        foreach (var hash in requested)
        {
            forbiddenDict.TryGetValue(hash, out var forbiddenFile);

            Uri? baseUrl = null;
            if (forbiddenFile == null)
            {
                // Select shard by matching regex; tolerate bad patterns
                var matchingShards = allFileShards.Where(s => TryMatches(s.FileMatch, hash)).ToList();
                List<CdnShardConfiguration> selectedShards;

                if (string.Equals(Continent, "*", StringComparison.Ordinal))
                {
                    selectedShards = matchingShards;
                }
                else
                {
                    selectedShards = matchingShards
                        .Where(c => c.Continents.Contains(Continent, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (!selectedShards.Any()) selectedShards = matchingShards;
                }

                var shard = selectedShards
                    .OrderBy(s => !s.Continents.Any() ? 0 : 1)
                    .ThenBy(s => s.Continents.Contains("*", StringComparer.Ordinal) ? 0 : 1)
                    .ThenBy(_ => Guid.NewGuid())
                    .FirstOrDefault();

                baseUrl = shard?.CdnFullUrl ?? DefaultCdnUrlSafely();
            }

            var exists = cacheDict.TryGetValue(hash, out var size) && size > 0;

            DownloadFileDto? altDto = null;
            bool willNotBeCompressed = false;
            if (forbiddenFile == null && convDict.TryGetValue(hash, out var conv))
            {
                // Garde-fou Yukiara : ne jamais servir d'alternate BC7 pour une normal map, même si un blob a été
                // généré en aveugle lors du backfill historique (rôle inconnu à l'époque). La sécurité vit ici.
                if (conv.Role == Bc7TextureRole.Normal)
                {
                    willNotBeCompressed = true;
                }
                else if (conv.State == Bc7ConversionState.Converted && conv.AlternateHash != null
                    && altDict.TryGetValue(conv.AlternateHash, out var altInfo) && altInfo.Size > 0)
                {
                    altDto = new DownloadFileDto
                    {
                        FileExists = true,
                        Hash = conv.AlternateHash,
                        Size = altInfo.Size,
                        Url = baseUrl?.ToString() ?? string.Empty,
                        DirectDownloadUrl = BuildDirectFor(conv.AlternateHash, altInfo.S3Confirmed),
                    };
                }
                else if (conv.State == Bc7ConversionState.Skipped || conv.State == Bc7ConversionState.Failed)
                {
                    willNotBeCompressed = true;
                }
            }

            response.Add(new DownloadFileDto
            {
                FileExists = exists,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = hash,
                Size = exists ? size : 0,
                Url = exists ? (baseUrl?.ToString() ?? string.Empty) : string.Empty,
                DirectDownloadUrl = exists && forbiddenFile == null ? BuildDirectDownloadUrl(hash) : string.Empty,
                CompressedAlternateFileDownload = altDto,
                WillNotBeCompressed = willNotBeCompressed,
            });
        }

        return Ok(response);
    }

    [HttpGet(MareFiles.ServerFiles_GetSizes)]
    [Produces("application/json")]
    public async Task<IActionResult> FilesGetSizesGet([FromQuery] string? hashes = null)
    {
        List<string> parsed = new();
        if (!string.IsNullOrWhiteSpace(hashes))
        {
            try
            {
                var direct = JsonSerializer.Deserialize<List<string>>(hashes);
                if (direct != null) parsed = direct;
            }
            catch
            {
                try
                {
                    var inner = JsonSerializer.Deserialize<string>(hashes);
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        var innerList = JsonSerializer.Deserialize<List<string>>(inner);
                        if (innerList != null) parsed = innerList;
                    }
                }
                catch
                {
                    parsed = hashes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
        }

        return await FilesGetSizes(parsed).ConfigureAwait(false);
    }

    [HttpPost(MareFiles.ServerFiles_FilesSend)]
    public async Task<IActionResult> FilesSend([FromBody] FilesSendDto filesSendDto)
    {
        var userSentHashes = new HashSet<string>(filesSendDto.FileHashes.Distinct(StringComparer.Ordinal).Select(s => string.Concat(s.Where(c => char.IsLetterOrDigit(c)))), StringComparer.Ordinal);
        var notCoveredFiles = new Dictionary<string, UploadFileDto>(StringComparer.Ordinal);
        var forbiddenFiles = await _mareDbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var existingFiles = await _mareDbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        // Une ligne « Uploaded » dont le fichier n'existe plus nulle part doit être renvoyée par l'émetteur.
        var ghosts = await HealGhostRowsAsync(existingFiles.Values.Select(f => (f.Hash, f.Uploaded, f.S3Confirmed)), HttpContext.RequestAborted).ConfigureAwait(false);

        List<FileCache> fileCachesToUpload = new();
        foreach (var hash in userSentHashes)
        {
            // Skip empty file hashes, duplicate file hashes, forbidden file hashes and existing file hashes
            if (string.IsNullOrEmpty(hash)) { continue; }
            if (notCoveredFiles.ContainsKey(hash)) { continue; }
            if (forbiddenFiles.ContainsKey(hash))
            {
                notCoveredFiles[hash] = new UploadFileDto()
                {
                    ForbiddenBy = forbiddenFiles[hash].ForbiddenBy,
                    Hash = hash,
                    IsForbidden = true,
                };

                continue;
            }
            if (existingFiles.TryGetValue(hash, out var file) && file.Uploaded && !ghosts.Contains(hash)) { continue; }

            notCoveredFiles[hash] = new UploadFileDto()
            {
                Hash = hash,
            };
        }

        if (notCoveredFiles.Any(p => !p.Value.IsForbidden))
        {
            await _hubContext.Clients.Users(filesSendDto.UIDs).SendAsync(nameof(IMareHub.Client_UserReceiveUploadStatus), new UmbraSync.API.Dto.User.UserDto(new(MareUser)))
                .ConfigureAwait(false);
        }

        return Ok(JsonSerializer.Serialize(notCoveredFiles.Values.ToList()));
    }

    [HttpPost(MareFiles.ServerFiles_Upload + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile(string hash, CancellationToken requestAborted)
    {
        _logger.LogInformation("{user} uploading file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        if (hash.Length != 40 || !hash.All(char.IsAsciiHexDigit)) return BadRequest();

        if (await IsServableAsync(hash, requestAborted).ConfigureAwait(false)) return Ok();

        UploadLock uploadLock = AcquireUploadLockReference(hash);
        bool lockTaken = false;
        try
        {
            await uploadLock.Semaphore.WaitAsync(requestAborted).ConfigureAwait(false);
            lockTaken = true;

            // Un upload concurrent du même hash a pu aboutir pendant l'attente
            if (await IsServableAsync(hash, requestAborted).ConfigureAwait(false)) return Ok();

            var path = FilePathUtil.GetFilePath(_basePath, hash);
            // Temporaire unique : deux uploads du même hash ne doivent jamais écrire dans le même fichier
            var tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            long compressedSize = -1;

            try
            {
                // Write incoming file to a temporary file while also hashing the decompressed content

                // Stream flow diagram:
                // Request.Body ==> (Tee) ==> FileStream
                //                        ==> CountedStream ==> LZ4DecoderStream ==> HashingStream ==> Stream.Null

                // Reading via TeeStream causes the request body to be copied to tmpPath
                using (var tmpFileStream = new FileStream(tmpPath, FileMode.CreateNew))
                {
                    using var teeStream = new TeeStream(Request.Body, tmpFileStream);
                    teeStream.DisposeUnderlying = false;
                    // Read via CountedStream to count the number of compressed bytes
                    using var countStream = new CountedStream(teeStream);
                    countStream.DisposeUnderlying = false;

                    // The decompressed file content is read through LZ4DecoderStream, and written out to HashingStream
                    using var decStream = LZ4Stream.Decode(countStream, extraMemory: 0, leaveOpen: true);
                    // HashingStream simply hashes the decompressed bytes without writing them anywhere
                    using var hashStream = new HashingStream(Stream.Null, SHA1.Create());
                    hashStream.DisposeUnderlying = false;

                    await decStream.CopyToAsync(hashStream, requestAborted).ConfigureAwait(false);
                    decStream.Close();

                    var hashString = BitConverter.ToString(hashStream.Finish())
                        .Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
                    if (!string.Equals(hashString, hash, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Hash does not match file, computed: {hashString}, expected: {hash}");

                    compressedSize = countStream.BytesRead;
                    await tmpFileStream.FlushAsync(requestAborted).ConfigureAwait(false);
                }

                if (compressedSize <= 0)
                    throw new InvalidOperationException($"Empty upload for {hash}");

                // Contenu vérifié : même contenu que tout fichier déjà présent à cet emplacement, l'écrasement est sans risque
                System.IO.File.Move(tmpPath, path, true);
            }
            catch
            {
                // Ne supprimer que notre propre temporaire, jamais le fichier final qu'un autre upload a pu publier
                try { System.IO.File.Delete(tmpPath); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "Could not delete temp file for {hash}", hash); }
                throw;
            }

            await SaveUploadedFileRowAsync(hash, compressedSize, requestAborted).ConfigureAwait(false);

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, compressedSize);

            // Upload immédiat vers S3 — si ça échoue, le worker rattrapera
            _ = _scalewayStorage.UploadAndConfirmAsync(hash, path, CancellationToken.None);

            return Ok();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Upload of {hash} aborted by client", hash);
            return BadRequest();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            if (lockTaken) uploadLock.Semaphore.Release();
            ReleaseUploadLockReference(hash, uploadLock);
        }
    }

    // Crée la ligne, ou réactive une ligne existante (ligne fantôme réparée, compte d'origine supprimé).
    // Le fichier final n'est jamais supprimé en cas d'échec : il est valide et peut être référencé ailleurs.
    private async Task SaveUploadedFileRowAsync(string hash, long compressedSize, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var uploader = MareUser;
        var updated = await _mareDbContext.Files
            .Where(f => f.Hash == hash)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Uploaded, true)
                .SetProperty(f => f.Size, compressedSize)
                .SetProperty(f => f.UploadDate, now)
                .SetProperty(f => f.S3Confirmed, false)
                .SetProperty(f => f.S3ConfirmedAt, (DateTime?)null)
                .SetProperty(f => f.UploaderUID, f => f.UploaderUID ?? uploader), ct)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            _logger.LogInformation("Re-upload of {hash} restored an existing file entry", hash);
            return;
        }

        // update on db — S3Confirmed=false, le worker S3 le sync automatiquement
        await _mareDbContext.Files.AddAsync(new FileCache()
        {
            Hash = hash,
            UploadDate = now,
            UploaderUID = uploader,
            Size = compressedSize,
            Uploaded = true,
            S3Confirmed = false,
            S3ConfirmedAt = null
        }, ct).ConfigureAwait(false);

        try
        {
            await _mareDbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Ligne insérée entre-temps (conversion BC7 du même contenu) : le fichier est bien enregistré
            if (!await _mareDbContext.Files.AsNoTracking().AnyAsync(f => f.Hash == hash, CancellationToken.None).ConfigureAwait(false))
                throw;
            _logger.LogDebug(ex, "File entry for {hash} already created concurrently", hash);
        }
    }

    // Servable = enregistré comme envoyé ET (confirmé sur S3 OU présent sur le disque de ce serveur)
    private async Task<bool> IsServableAsync(string hash, CancellationToken ct)
    {
        var row = await _mareDbContext.Files.AsNoTracking()
            .Where(f => f.Hash == hash)
            .Select(f => new { f.Hash, f.Uploaded, f.S3Confirmed })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row == null || !row.Uploaded) return false;

        var ghosts = await HealGhostRowsAsync([(row.Hash, row.Uploaded, row.S3Confirmed)], ct).ConfigureAwait(false);
        return ghosts.Count == 0;
    }

    private bool IsLocallyPresent(string hash)
    {
        try
        {
            var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
            if (fi != null && fi.Length > 0) return true;
            if (string.Equals(_basePath, _hotPath, StringComparison.Ordinal)) return false;
            fi = FilePathUtil.GetFileInfoForHash(_hotPath, hash);
            return fi != null && fi.Length > 0;
        }
        catch (Exception ex)
        {
            // Dans le doute on considère le fichier présent : une fausse réparation forcerait un ré-upload inutile
            _logger.LogWarning(ex, "Could not check local presence of {hash}", hash);
            return true;
        }
    }

    // Une ligne « Uploaded » non confirmée sur S3 dont le blob local a disparu ne peut plus être servie :
    // on la repasse à Uploaded=false pour que FilesSend redemande le fichier à l'émetteur.
    private async Task<HashSet<string>> HealGhostRowsAsync(IEnumerable<(string Hash, bool Uploaded, bool S3Confirmed)> rows, CancellationToken ct)
    {
        var ghosts = rows
            .Where(r => r.Uploaded && !r.S3Confirmed && !IsLocallyPresent(r.Hash))
            .Select(r => r.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ghosts.Count == 0) return ghosts;

        try
        {
            var ghostList = ghosts.ToList();
            var healed = await _mareDbContext.Files
                .Where(f => ghostList.Contains(f.Hash) && f.Uploaded && !f.S3Confirmed)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.Uploaded, false), ct)
                .ConfigureAwait(false);
            _logger.LogWarning("Self-heal: {count} file entr(ies) marked as not uploaded (blob missing locally and not on S3): {hashes}",
                healed, string.Join(",", ghostList.Take(10)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Self-heal of {count} file entries failed", ghosts.Count);
        }

        return ghosts;
    }

    private static UploadLock AcquireUploadLockReference(string hash)
    {
        lock (_fileUploadLocksGuard)
        {
            if (!_fileUploadLocks.TryGetValue(hash, out var uploadLock))
            {
                uploadLock = new UploadLock();
                _fileUploadLocks[hash] = uploadLock;
            }

            uploadLock.References++;
            return uploadLock;
        }
    }

    // Le sémaphore n'est retiré qu'une fois plus personne ne l'attend : il n'est jamais disposé en cours d'usage
    private static void ReleaseUploadLockReference(string hash, UploadLock uploadLock)
    {
        lock (_fileUploadLocksGuard)
        {
            uploadLock.References--;
            if (uploadLock.References <= 0 && _fileUploadLocks.TryGetValue(hash, out var current) && ReferenceEquals(current, uploadLock))
            {
                _fileUploadLocks.Remove(hash);
                uploadLock.Semaphore.Dispose();
            }
        }
    }

    private sealed class UploadLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    [HttpPost(MareFiles.ServerFiles_ReportCdnMiss)]
    public async Task<IActionResult> ReportCdnMiss([FromBody] List<string> hashes)
    {
        // Rate limiting par utilisateur : max 1 appel toutes les 30 secondes
        var now = DateTime.UtcNow;
        if (_cdnMissRateLimit.TryGetValue(MareUser, out var lastCall) && (now - lastCall).TotalSeconds < 30)
            return Ok();
        _cdnMissRateLimit[MareUser] = now;

        var requested = (hashes ?? new List<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length == 40 && h.All(c => char.IsAsciiHexDigit(c)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        if (requested.Count == 0) return Ok();

        // Vérifier sur S3 avant d'invalider — on ne fait pas confiance au client aveuglément
        _ = _scalewayStorage.VerifyAndInvalidateAsync(requested, CancellationToken.None);

        return Ok();
    }
}
