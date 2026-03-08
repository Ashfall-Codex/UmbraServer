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
    private static readonly SemaphoreSlim _fileLockDictLock = new(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileUploadLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> _cdnMissRateLimit = new(StringComparer.Ordinal);
    private readonly string _basePath;
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
            .Select(k => new { k.Hash, k.Size, k.S3Confirmed })
            .ToListAsync().ConfigureAwait(false);

        var cacheDict = cacheFiles.ToDictionary(k => k.Hash, k => k.Size, StringComparer.OrdinalIgnoreCase);
        var s3ConfirmedDict = cacheFiles.ToDictionary(k => k.Hash, k => k.S3Confirmed, StringComparer.OrdinalIgnoreCase);
        var forbiddenDict = forbiddenFiles.ToDictionary(f => f.Hash, f => f, StringComparer.OrdinalIgnoreCase);
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
            if (!_scalewayStorage.IsEnabled) return string.Empty;
            if (!s3ConfirmedDict.TryGetValue(hash, out var confirmed) || !confirmed) return string.Empty;
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

            response.Add(new DownloadFileDto
            {
                FileExists = exists,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = hash,
                Size = exists ? size : 0,
                Url = exists ? (baseUrl?.ToString() ?? string.Empty) : string.Empty,
                DirectDownloadUrl = exists && forbiddenFile == null ? BuildDirectDownloadUrl(hash) : string.Empty,
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
            if (existingFiles.TryGetValue(hash, out var file) && file.Uploaded) { continue; }

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
        var existingFile = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim? fileLock = null;
        bool successfullyWaited = false;
        while (!successfullyWaited && !requestAborted.IsCancellationRequested)
        {
            lock (_fileUploadLocks)
            {
                if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                    _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
            }

            try
            {
                await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);
                successfullyWaited = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("Semaphore disposed for {hash}, recreating", hash);
            }
        }

        try
        {
            var existingFileCheck2 = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            var path = FilePathUtil.GetFilePath(_basePath, hash);
            var tmpPath = path + ".tmp";
            long compressedSize = -1;

            try
            {
                // Write incoming file to a temporary file while also hashing the decompressed content

                // Stream flow diagram:
                // Request.Body ==> (Tee) ==> FileStream
                //                        ==> CountedStream ==> LZ4DecoderStream ==> HashingStream ==> Stream.Null

                // Reading via TeeStream causes the request body to be copied to tmpPath
                using var tmpFileStream = new FileStream(tmpPath, FileMode.Create);
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

                // File content is verified -- move it to its final location
                System.IO.File.Move(tmpPath, path, true);
            }
            catch
            {
                try { System.IO.File.Delete(tmpPath); } catch { }
                // If the file was already moved to its final path, clean it up too
                try { System.IO.File.Delete(path); } catch { }
                throw;
            }

            try
            {
                // update on db — S3Confirmed=false, le worker S3 le sync automatiquement
                await _mareDbContext.Files.AddAsync(new FileCache()
                {
                    Hash = hash,
                    UploadDate = DateTime.UtcNow,
                    UploaderUID = MareUser,
                    Size = compressedSize,
                    Uploaded = true,
                    S3Confirmed = false,
                    S3ConfirmedAt = null
                }).ConfigureAwait(false);
                await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

                _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
                _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, compressedSize);

                // Upload immédiat vers S3 — si ça échoue, le worker rattrapera
                _ = _scalewayStorage.UploadAndConfirmAsync(hash, path, CancellationToken.None);

                _fileUploadLocks.TryRemove(hash, out _);

                return Ok();
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "DB save failed for {hash}, cleaning up file", hash);
                try { System.IO.File.Delete(path); } catch { }
                throw;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            try
            {
                fileLock?.Release();
                fileLock?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
        }
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
