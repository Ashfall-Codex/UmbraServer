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
        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var batchSize = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionBatchSize), 25);

        var pending = await db.FileBc7Conversions
            .Where(c => c.State == Bc7ConversionState.Pending)
            .OrderBy(c => c.UpdatedAt)
            .Take(batchSize)
            .Select(c => new { c.SourceHash, c.Role })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pending.Count == 0) return 0;

        _logger.LogInformation("BC7: {Count} textures to convert", pending.Count);

        var writeDir = WriteDirectory;
        var hotDir = HotDirectory;
        // 0 = moitié des cœurs (laisse du CPU au service de fichiers). BCnEncoder parallélise en interne.
        var configThreads = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionMaxThreads), 0);
        int maxThreads = configThreads > 0 ? configThreads : Math.Max(1, Environment.ProcessorCount / 2);
        int converted = 0, skipped = 0, failed = 0;

        // Séquentiel : l'encodage BC7 est CPU-lourd et le DbContext n'est pas thread-safe.
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ConvertOneAsync(db, item.SourceHash, item.Role, writeDir, hotDir, maxThreads, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case Bc7ConversionState.Converted: converted++; break;
                    case Bc7ConversionState.Skipped: skipped++; break;
                    default: failed++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "BC7: conversion failed for {Hash}", item.SourceHash);
                await SetStateAsync(db, item.SourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false);
                failed++;
            }
        }

        _logger.LogInformation("BC7 batch: {Converted} converted, {Skipped} skipped, {Failed} failed", converted, skipped, failed);
        return pending.Count;
    }

    private async Task<Bc7ConversionState> ConvertOneAsync(MareDbContext db, string sourceHash, Bc7TextureRole role, string writeDir, string hotDir, int maxThreads, CancellationToken ct)
    {
        // Règle Yukiara : ne jamais compresser une normal map en BC7 (artefacts sur la peau).
        if (role == Bc7TextureRole.Normal)
        {
            await SetStateAsync(db, sourceHash, Bc7ConversionState.Skipped, null, ct).ConfigureAwait(false);
            return Bc7ConversionState.Skipped;
        }

        // Les blobs (disque comme S3) sont en LZ4 : on récupère le blob compressé puis on le décompresse.
        var fi = FilePathUtil.GetFileInfoForHash(writeDir, sourceHash)
                 ?? FilePathUtil.GetFileInfoForHash(hotDir, sourceHash);
        byte[] rawTex;
        if (fi != null)
        {
            rawTex = DecompressLz4(fi.FullName);
        }
        else
        {
            // Blob évincé du disque local (nœud edge / cache chaud) : fallback S3 si autorisé.
            var fetchFromS3 = _config.GetValueOrDefault(nameof(StaticFilesServerConfiguration.Bc7ConversionFetchFromS3), true);
            var lz4 = fetchFromS3 ? await _scaleway.TryDownloadObjectAsync(sourceHash, ct).ConfigureAwait(false) : null;
            if (lz4 == null)
            {
                _logger.LogWarning("BC7: source blob {Hash} not found (disk{S3})", sourceHash, fetchFromS3 ? "+S3" : "");
                await SetStateAsync(db, sourceHash, Bc7ConversionState.Failed, null, ct).ConfigureAwait(false);
                return Bc7ConversionState.Failed;
            }
            rawTex = DecompressLz4Bytes(lz4);
        }

        var result = TexTranscoder.TryTranscodeToBc7(rawTex, out var bc7Tex, maxThreads);
        if (result != TexTranscodeResult.Converted)
        {
            _logger.LogDebug("BC7: {Hash} not convertible ({Reason}), marking skipped", sourceHash, result);
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

        await SetStateAsync(db, sourceHash, Bc7ConversionState.Converted, altHash, ct).ConfigureAwait(false);
        _logger.LogInformation("BC7: {Source} -> {Alt}", sourceHash, altHash);
        return Bc7ConversionState.Converted;
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
