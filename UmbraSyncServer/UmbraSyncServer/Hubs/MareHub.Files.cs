using MareSynchronosServer.Utils;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using UmbraSync.API.Dto.User;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const int FilesReportMissingMaxPerMinute = 10;
    private const int FilesReportMissingMaxHashes = 200;
    private static readonly TimeSpan FilesReUploadDedupTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Un récepteur signale des fichiers absents du serveur dans les données d'un pair : on demande à
    /// l'émetteur (forcément en ligne puisqu'il vient d'envoyer ses données) de les ré-uploader.
    /// Refus silencieux côté appelant ; l'identité du demandeur n'est jamais transmise à l'émetteur.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task FilesReportMissing(UserDto owner, List<string> hashes)
    {
        if (owner?.User == null || string.IsNullOrEmpty(owner.User.UID) || hashes == null || hashes.Count == 0)
            return;

        var ownerUid = owner.User.UID;
        if (string.Equals(ownerUid, UserUID, StringComparison.Ordinal))
            return;

        _logger.LogCallInfo(MareHubLogger.Args(ownerUid, hashes.Count));

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.Database, "filesmissing", UserUID, FilesReportMissingMaxPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
        {
            _logger.LogCallInfo(MareHubLogger.Args(ownerUid, "RateLimited"));
            return;
        }

        var candidates = hashes
            .Where(h => !string.IsNullOrEmpty(h) && HashRegex().IsMatch(h))
            .Select(h => h.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(FilesReportMissingMaxHashes)
            .ToList();
        if (candidates.Count == 0) return;

        var refusal = await GetReportMissingRefusalAsync(ownerUid).ConfigureAwait(false);
        if (refusal != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(ownerUid, refusal));
            return;
        }

        // Le hub ne voit pas les disques du serveur de fichiers : est « servable » une ligne Uploaded=true.
        // Les lignes fantômes (blob perdu) sont déjà repassées à Uploaded=false par le serveur de fichiers
        // quand le récepteur a demandé leur taille, juste avant ce signalement.
        var servable = await DbContext.Files.AsNoTracking()
            .Where(f => candidates.Contains(f.Hash) && f.Uploaded)
            .Select(f => f.Hash)
            .ToListAsync().ConfigureAwait(false);
        var servableSet = servable.ToHashSet(StringComparer.Ordinal);

        var db = _redis.Database;
        List<string> toRequest = [];
        foreach (var hash in candidates)
        {
            if (servableSet.Contains(hash)) continue;
            // Un même fichier n'est redemandé à l'émetteur qu'une fois toutes les 5 minutes, quel que soit le demandeur
            if (await db.StringSetAsync($"reupload:{ownerUid}:{hash}", "1", FilesReUploadDedupTtl, When.NotExists).ConfigureAwait(false))
                toRequest.Add(hash);
        }

        if (toRequest.Count == 0)
        {
            _logger.LogCallInfo(MareHubLogger.Args(ownerUid, "NothingToRequest", candidates.Count));
            return;
        }

        _logger.LogCallInfo(MareHubLogger.Args(ownerUid, "ReUploadRequested", toRequest.Count));
        await Clients.User(ownerUid).Client_FilesReUploadRequested(toRequest).ConfigureAwait(false);
    }

    private async Task<string> GetReportMissingRefusalAsync(string ownerUid)
    {
        // Seul un pair qui reçoit effectivement les données de l'émetteur (paire directe ou syncshell, non en pause) peut signaler
        var pairedUnpaused = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (!pairedUnpaused.Contains(ownerUid, StringComparer.Ordinal))
            return "NotPaired";

        if (await UserBlockQueries.IsBlockedEitherWayAsync(DbContext, UserUID, ownerUid).ConfigureAwait(false))
            return "Blocked";

        if (string.IsNullOrEmpty(await GetUserIdent(ownerUid).ConfigureAwait(false)))
            return "OwnerOffline";

        return null;
    }
}
