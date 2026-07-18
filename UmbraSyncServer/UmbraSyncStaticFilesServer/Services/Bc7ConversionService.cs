using System.Security.Cryptography;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public sealed class Bc7ConversionService : IHostedService, IDisposable
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _config;
    private readonly ILogger<Bc7ConversionService> _logger;
    private readonly IServiceProvider _services;
    private readonly ScalewayStorageService _scaleway;
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private bool _disposed;

    public bool IsEnabled => _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionEnabled), false);

    // Répertoire d'écriture des blobs convertis : identique à ScalewayStorageService.CacheDirectory
    // pour que le worker S3 les récupère sans plomberie supplémentaire.
    private string WriteDirectory => _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)
        ? _config.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory))
        : _config.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));

    private string HotDirectory => _config.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));

    public Bc7ConversionService(
        IConfigurationService<StaticFilesServerConfiguration> config,
        ILogger<Bc7ConversionService> logger,
        IServiceProvider services,
        ScalewayStorageService scaleway)
    {
        _config = config;
        _logger = logger;
        _services = services;
        _scaleway = scaleway;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("BC7 conversion service is disabled");
            return Task.CompletedTask;
        }

        _workerTask = WorkerLoopAsync(_cts.Token);
        _logger.LogInformation("BC7 conversion service started (DB-driven worker)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;

        _cts.Cancel();
        if (_workerTask != null)
        {
            try { await _workerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _logger.LogInformation("BC7 conversion service stopped");
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        // Laisser le serveur démarrer
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await ConvertBatchAsync(ct).ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BC7 conversion worker");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> ConvertBatchAsync(CancellationToken ct)
    {
        var batchSize = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionBatchSize), 25);
        int ioConcurrency = Math.Max(1, _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionIoConcurrency), 16));
        int pollSize = Math.Max(batchSize, ioConcurrency * 4);

        List<(string SourceHash, Bc7TextureRole Role)> pending;
        using (var pollScope = _services.CreateScope())
        using (var pollDb = pollScope.ServiceProvider.GetRequiredService<MareDbContext>())
        {
            pending = (await pollDb.FileBc7Conversions
                .Where(c => c.State == Bc7ConversionState.Pending)
                .OrderBy(c => c.UpdatedAt)
                .Take(pollSize)
                .Select(c => new { c.SourceHash, c.Role })
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .Select(c => (c.SourceHash, c.Role))
                .ToList();
        }

        if (pending.Count == 0) return 0;

        _logger.LogInformation("BC7: {Count} textures to process", pending.Count);

        var writeDir = WriteDirectory;
        var hotDir = HotDirectory;
        int encodeConcurrency = Math.Max(1, _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionEncodeConcurrency), 2));
        var configThreads = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionMaxThreads), 0);
        int maxThreads = configThreads > 0 ? configThreads : Math.Max(1, (Environment.ProcessorCount - 2) / encodeConcurrency);
        int converted = 0, skipped = 0, failed = 0;
        using var ioGate = new SemaphoreSlim(ioConcurrency);
        using var encodeGate = new SemaphoreSlim(encodeConcurrency);

        var tasks = pending.Select(async item =>
        {
            var outcome = await ConvertOneAsync(item.SourceHash, item.Role, writeDir, hotDir, maxThreads, ioGate, encodeGate, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case Bc7ConversionState.Converted: Interlocked.Increment(ref converted); break;
                case Bc7ConversionState.Skipped: Interlocked.Increment(ref skipped); break;
                default: Interlocked.Increment(ref failed); break;
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation("BC7 batch: {Converted} converted, {Skipped} skipped, {Failed} failed", converted, skipped, failed);
        return pending.Count;
    }

    private async Task<Bc7ConversionState> ConvertOneAsync(string sourceHash, Bc7TextureRole role, string writeDir, string hotDir, int maxThreads, SemaphoreSlim ioGate, SemaphoreSlim encodeGate, CancellationToken ct)
    {
        // Chaque item a son propre scope DB (traitement parallèle : DbContext non thread-safe).
        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        try
        {
            // Règle Yukiara : ne jamais compresser une normal map en BC7 (artefacts sur la peau).
            if (role == Bc7TextureRole.Normal)
            {
                await SetStateAsync(db, sourceHash, Bc7ConversionState.Skipped, null, ct).ConfigureAwait(false);
                return Bc7ConversionState.Skipped;
            }

            byte[] rawTex;
            await ioGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var fi = FilePathUtil.GetFileInfoForHash(writeDir, sourceHash)
                         ?? FilePathUtil.GetFileInfoForHash(hotDir, sourceHash);
                if (fi != null)
                {
                    rawTex = DecompressLz4(fi.FullName);
                }
                else
                {
                    var fetchFromS3 = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionFetchFromS3), true);
                    if (!fetchFromS3)
                    {
                        await SetStateAsync(db, sourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false);
                        return Bc7ConversionState.Failed;
                    }

                    // Download complet (protégé par timeout). Le skip des non-textures se fait juste après via
                    // PeekFormat(rawTex), hors gate d'encodage — pas de range-GET fragile ici.
                    var lz4 = await _scaleway.TryDownloadObjectAsync(sourceHash, ct).ConfigureAwait(false);
                    if (lz4 == null)
                    {
                        _logger.LogWarning("BC7: source blob {Hash} not found (disk+S3)", sourceHash);
                        await SetStateAsync(db, sourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false);
                        return Bc7ConversionState.Failed;
                    }
                    rawTex = DecompressLz4Bytes(lz4);
                }
            }
            finally
            {
                ioGate.Release();
            }

            if (TexTranscoder.PeekFormat(rawTex) == TexTranscoder.TexPeekResult.NotConvertible)
            {
                await SetStateAsync(db, sourceHash, Bc7ConversionState.Skipped, null, ct).ConfigureAwait(false);
                return Bc7ConversionState.Skipped;
            }

            byte[] bc7Tex;
            TexTranscodeResult result;
            await encodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                result = TexTranscoder.TryTranscodeToBc7(rawTex, out bc7Tex, maxThreads);
            }
            finally
            {
                encodeGate.Release();
            }

            if (result != TexTranscodeResult.Converted)
            {
                await SetStateAsync(db, sourceHash, Bc7ConversionState.Skipped, null, ct).ConfigureAwait(false);
                return Bc7ConversionState.Skipped;
            }

            // Le hash content-addressed = SHA1 du contenu décompressé (comme tout le reste du système).
            string altHash = Convert.ToHexString(SHA1.HashData(bc7Tex));

            bool alreadyStored = await db.Files.AnyAsync(f => f.Hash == altHash, ct).ConfigureAwait(false);
            if (!alreadyStored)
            {
                // Attribution : on réutilise l'uploader de la source (UID existant → pas de violation de FK).
                var uploaderUid = await db.Files
                    .Where(f => f.Hash == sourceHash)
                    .Select(f => f.UploaderUID)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(uploaderUid))
                {
                    _logger.LogWarning("BC7: no FileCache row for source {Hash}, cannot attribute alternate", sourceHash);
                    await SetStateAsync(db, sourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false);
                    return Bc7ConversionState.Failed;
                }

                byte[] compressed = CompressLz4(bc7Tex);
                var outPath = FilePathUtil.GetFilePath(writeDir, altHash);
                await File.WriteAllBytesAsync(outPath, compressed, ct).ConfigureAwait(false);

                try
                {
                    db.Files.Add(new FileCache
                    {
                        Hash = altHash,
                        UploadDate = DateTime.UtcNow,
                        UploaderUID = uploaderUid,
                        Size = compressed.Length,
                        Uploaded = true,
                        S3Confirmed = false,
                        S3ConfirmedAt = null,
                    });
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // Course : un autre thread a inséré le même altHash (textures identiques). Sans gravité.
                }
            }

            await SetStateAsync(db, sourceHash, Bc7ConversionState.Converted, altHash, ct).ConfigureAwait(false);
            _logger.LogInformation("BC7: {Source} -> {Alt}", sourceHash, altHash);
            return Bc7ConversionState.Converted;
        }
        catch (OperationCanceledException)
        {
            return Bc7ConversionState.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BC7: conversion failed for {Hash}", sourceHash);
            try { await SetStateAsync(db, sourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false); } catch { }
            return Bc7ConversionState.Failed;
        }
    }

    private static async Task SetStateAsync(MareDbContext db, string sourceHash, Bc7ConversionState state, string? altHash, CancellationToken ct)
    {
        await db.FileBc7Conversions
            .Where(c => c.SourceHash == sourceHash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.State, state)
                .SetProperty(c => c.AlternateHash, altHash)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct)
            .ConfigureAwait(false);
    }

    private static byte[] DecompressLz4(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dec = LZ4Stream.Decode(fs, extraMemory: 0, leaveOpen: false);
        using var ms = new MemoryStream();
        dec.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] DecompressLz4Bytes(byte[] compressed)
    {
        using var src = new MemoryStream(compressed);
        using var dec = LZ4Stream.Decode(src, extraMemory: 0, leaveOpen: false);
        using var ms = new MemoryStream();
        dec.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] CompressLz4(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var enc = LZ4Stream.Encode(ms, LZ4Level.L09_HC, extraMemory: 0, leaveOpen: true))
        {
            enc.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
