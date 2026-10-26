using MareSynchronosServer.Utils;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosServer.Services;

public sealed class ProfileImageMetadataCleanupService : IHostedService
{
    private const string DoneKey = "cleanup:profile-image-metadata:v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(200);

    private readonly ILogger<ProfileImageMetadataCleanupService> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly IRedisDatabase _redis;
    private readonly CancellationTokenSource _cts = new();

    public ProfileImageMetadataCleanupService(ILogger<ProfileImageMetadataCleanupService> logger,
        IDbContextFactory<MareDbContext> dbContextFactory, IRedisDatabase redis)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _redis = redis;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _cts.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (await _redis.ExistsAsync(DoneKey).ConfigureAwait(false)) return;
            await Task.Delay(StartDelay, ct).ConfigureAwait(false);
            _logger.LogInformation("Profile image metadata cleanup started");

            var cleaned = 0;
            cleaned += await CleanTableAsync<UserProfileData, string>(
                db => db.UserProfileData.Where(p => p.Base64ProfileImage != null), nameof(UserProfileData.UserUID),
                p => StripColumn(p.Base64ProfileImage, v => p.Base64ProfileImage = v), ct).ConfigureAwait(false);
            cleaned += await CleanTableAsync<CharacterRpProfileData, int>(
                db => db.CharacterRpProfiles.Where(p => p.RpProfilePictureBase64 != null || p.RpBannerBase64 != null), nameof(CharacterRpProfileData.Id),
                p => StripColumn(p.RpProfilePictureBase64, v => p.RpProfilePictureBase64 = v) | StripColumn(p.RpBannerBase64, v => p.RpBannerBase64 = v), ct).ConfigureAwait(false);
            cleaned += await CleanTableAsync<GroupProfile, string>(
                db => db.GroupProfiles.Where(p => p.Base64ProfileImage != null || p.Base64BannerImage != null), nameof(GroupProfile.GroupGID),
                p => StripColumn(p.Base64ProfileImage, v => p.Base64ProfileImage = v) | StripColumn(p.Base64BannerImage, v => p.Base64BannerImage = v), ct).ConfigureAwait(false);
            cleaned += await CleanTableAsync<Establishment, Guid>(
                db => db.Establishments.Where(e => e.LogoImageBase64 != null || e.BannerImageBase64 != null), nameof(Establishment.Id),
                e => StripColumn(e.LogoImageBase64, v => e.LogoImageBase64 = v) | StripColumn(e.BannerImageBase64, v => e.BannerImageBase64 = v), ct).ConfigureAwait(false);

            await _redis.AddAsync(DoneKey, DateTime.UtcNow.ToString("O")).ConfigureAwait(false);
            _logger.LogInformation("Profile image metadata cleanup finished, {count} rows rewritten", cleaned);
        }
        catch (OperationCanceledException)
        {
            // Arrêt du serveur : le nettoyage reprendra au prochain démarrage, les images déjà traitées ne changent plus.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile image metadata cleanup failed, it will run again at next start");
        }
    }

    private async Task<int> CleanTableAsync<TEntity, TKey>(Func<MareDbContext, IQueryable<TEntity>> source, string keyName,
        Func<TEntity, bool> stripImages, CancellationToken ct) where TEntity : class
    {
        List<TKey> keys;
        using (var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            keys = await source(db).Select(e => EF.Property<TKey>(e, keyName)).ToListAsync(ct).ConfigureAwait(false);
        }

        var cleaned = 0;
        foreach (var batch in keys.Chunk(BatchSize))
        {
            using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entities = await source(db).Where(e => batch.Contains(EF.Property<TKey>(e, keyName))).ToListAsync(ct).ConfigureAwait(false);
            foreach (var entity in entities)
            {
                if (stripImages(entity)) cleaned++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await Task.Delay(BatchPause, ct).ConfigureAwait(false);
        }

        return cleaned;
    }

    private static bool StripColumn(string base64, Action<string> update)
    {
        if (string.IsNullOrEmpty(base64)) return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        var stripped = ProfileImageSanitizer.StripMetadata(bytes);
        if (ReferenceEquals(stripped, bytes)) return false;

        update(Convert.ToBase64String(stripped));
        return true;
    }
}
