using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace MareSynchronosServer.Services;

public class UserCleanupService : IHostedService
{
    private readonly MareMetrics metrics;
    private readonly ILogger<UserCleanupService> _logger;
    private readonly IDbContextFactory<MareDbContext> _mareDbContextFactory;
    private readonly IConfigurationService<ServerConfiguration> _configuration;
    private readonly McdfShareStorage _mcdfStorage;
    private CancellationTokenSource _cleanupCts;

    public UserCleanupService(MareMetrics metrics, ILogger<UserCleanupService> logger, IDbContextFactory<MareDbContext> mareDbContextFactory, IConfigurationService<ServerConfiguration> configuration, McdfShareStorage mcdfStorage)
    {
        this.metrics = metrics;
        _logger = logger;
        _mareDbContextFactory = mareDbContextFactory;
        _configuration = configuration;
        _mcdfStorage = mcdfStorage;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");
        _cleanupCts = new();

        _ = CleanUp(_cleanupCts.Token);

        return Task.CompletedTask;
    }

    private async Task CleanUp(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var dbContext = await _mareDbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {

                CleanUpOutdatedLodestoneAuths(dbContext);

                await PurgeTempInvites(dbContext).ConfigureAwait(false);
                await PurgeExpiredTemporaryGroups(dbContext).ConfigureAwait(false);
                await PurgeExpiredWildRpAnnouncements(dbContext, ct).ConfigureAwait(false);
                await PurgeExpiredMcdfShares(dbContext, ct).ConfigureAwait(false);

                dbContext.SaveChanges();
            }

            await PurgeUnusedAccounts(ct).ConfigureAwait(false);

            var span = TimeSpan.FromMinutes(1);
            var nextRun = DateTime.Now.Add(span);

            _logger.LogInformation("User Cleanup Complete, next run at {date}", nextRun);
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }

    private async Task PurgeExpiredTemporaryGroups(MareDbContext dbContext)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredGroups = await dbContext.Groups
                .Where(g => g.IsTemporary && g.ExpiresAt != null && g.ExpiresAt <= now)
                .ToListAsync()
                .ConfigureAwait(false);

            if (expiredGroups.Count == 0) return;

            _logger.LogInformation("Cleaning up {count} expired temporary syncshells", expiredGroups.Count);

            dbContext.Groups.RemoveRange(expiredGroups);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during temporary syncshell purge");
        }
    }

    private async Task PurgeTempInvites(MareDbContext dbContext)
    {
        try
        {
            var tempInvites = await dbContext.GroupTempInvites.ToListAsync().ConfigureAwait(false);
            dbContext.RemoveRange(tempInvites.Where(i => i.ExpirationDate < DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Temp Invite purge");
        }
    }

    private async Task PurgeUnusedAccounts(CancellationToken ct)
    {
        try
        {
            if (!_configuration.GetValueOrDefault(nameof(ServerConfiguration.PurgeUnusedAccounts), false)) return;

            var usersOlderThanDays = _configuration.GetValueOrDefault(nameof(ServerConfiguration.PurgeUnusedAccountsPeriodInDays), 365);
            var maxGroupsByUser = _configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(usersOlderThanDays);

            _logger.LogInformation("Cleaning up users older than {usersOlderThanDays} days", usersOlderThanDays);

            List<string> outdatedUids;
            using (var dbContext = await _mareDbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                // Un compte principal n'est purgé que si tous ses comptes secondaires sont eux aussi inactifs,
                // puisque sa purge emporte ses secondaires.
                outdatedUids = await dbContext.Users.AsNoTracking()
                    .Where(u => u.LastLoggedIn < cutoff)
                    .Where(u => !dbContext.Auth.Any(a => a.PrimaryUserUID == u.UID && a.User.LastLoggedIn >= cutoff))
                    .Select(u => u.UID)
                    .ToListAsync(ct).ConfigureAwait(false);
            }

            foreach (var uid in outdatedUids)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // Un contexte par compte : un échec ne laisse aucun état en attente pour les suivants.
                    using var dbContext = await _mareDbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    var user = await dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid, ct).ConfigureAwait(false);
                    if (user == null || user.LastLoggedIn >= cutoff) continue;

                    await SharedDbFunctions.PurgeUser(_logger, user, dbContext, maxGroupsByUser).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during purge of user {uid}", uid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during user purge");
        }
    }

    private async Task PurgeExpiredWildRpAnnouncements(MareDbContext dbContext, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var removed = await dbContext.WildRpAnnouncements
                .Where(a => a.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            if (removed > 0)
                _logger.LogInformation("Removed {count} expired wild RP announcements", removed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during wild RP announcement purge");
        }
    }

    private async Task PurgeExpiredMcdfShares(MareDbContext dbContext, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            await dbContext.McdfShareAllowedUsers
                .Where(a => a.Share.ExpiresAtUtc != null && a.Share.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await dbContext.McdfShareAllowedGroups
                .Where(a => a.Share.ExpiresAtUtc != null && a.Share.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var removed = await dbContext.McdfShares
                .Where(s => s.ExpiresAtUtc != null && s.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            if (removed > 0)
                _logger.LogInformation("Removed {count} expired MCDF shares", removed);

            // Les suppressions SQL (expiration, cascade à la suppression d'un compte) ne touchent pas au disque :
            // on balaie les fichiers sans partage en base (délai de grâce pour les uploads en cours).
            var knownIds = (await dbContext.McdfShares.AsNoTracking()
                .Where(s => s.IsFileBacked)
                .Select(s => s.Id)
                .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
            var swept = _mcdfStorage.SweepOrphans(knownIds, TimeSpan.FromHours(1));
            if (swept > 0)
                _logger.LogInformation("Removed {count} orphaned MCDF storage files", swept);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during MCDF share purge");
        }
    }

    private void CleanUpOutdatedLodestoneAuths(MareDbContext dbContext)
    {
        try
        {
            _logger.LogInformation($"Cleaning up expired lodestone authentications");
            var lodestoneAuths = dbContext.LodeStoneAuth.Include(u => u.User).Where(a => a.StartedAt != null).ToList();
            List<LodeStoneAuth> expiredAuths = new List<LodeStoneAuth>();
            foreach (var auth in lodestoneAuths)
            {
                if (auth.StartedAt < DateTime.UtcNow - TimeSpan.FromMinutes(15))
                {
                    expiredAuths.Add(auth);
                }
            }

            dbContext.Users.RemoveRange(expiredAuths.Where(u => u.User != null).Select(a => a.User));
            dbContext.RemoveRange(expiredAuths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during expired auths cleanup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }
}
