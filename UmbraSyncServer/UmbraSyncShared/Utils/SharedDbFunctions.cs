using System.Collections.Generic;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Utils;

public static class SharedDbFunctions
{
    public static async Task<(bool, string)> MigrateOrDeleteGroup(MareDbContext context, Group group, IEnumerable<GroupPair> groupPairs, int maxGroupsByUser)
    {
        bool groupHasMigrated = false;
        string newOwner = string.Empty;
        foreach (var potentialNewOwner in groupPairs.OrderByDescending(p => p.IsModerator).ThenByDescending(p => p.IsPinned).ToList())
        {
            groupHasMigrated = await TryMigrateGroup(context, group, potentialNewOwner.GroupUserUID, maxGroupsByUser).ConfigureAwait(false);

            if (groupHasMigrated)
            {
                newOwner = potentialNewOwner.GroupUserUID;
                potentialNewOwner.IsPinned = true;
                potentialNewOwner.IsModerator = false;

                await context.SaveChangesAsync().ConfigureAwait(false);
                break;
            }
        }

        if (!groupHasMigrated)
        {
            context.GroupPairs.RemoveRange(groupPairs);
            context.Groups.Remove(group);

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        return (groupHasMigrated, newOwner);
    }

    public static async Task PurgeUser(ILogger logger, User user, MareDbContext dbContext, int maxGroupsByUser)
    {
        logger.LogInformation("Purging user: {uid}", user.UID);

        await PurgeSecondaryUsers(logger, user, dbContext, maxGroupsByUser).ConfigureAwait(false);
        await EraseUserAsync(logger, dbContext, user, maxGroupsByUser).ConfigureAwait(false);

        logger.LogInformation("User purged: {uid}", user.UID);
    }

    /// Effacement en base de toutes les données d'un compte, commun à la suppression demandée
    /// par l'utilisateur (hub) et à la purge automatique. Le hub fournit <paramref name="leaveGroup"/>
    /// pour quitter les syncshells en notifiant les membres ; sans lui, la sortie est silencieuse.
    /// Les comptes secondaires doivent avoir été effacés avant l'appel.
    public static async Task EraseUserAsync(ILogger logger, MareDbContext dbContext, User user, int maxGroupsByUser, Func<GroupPair, Task> leaveGroup = null)
    {
        if (dbContext.Database.CurrentTransaction != null)
        {
            await EraseUserInternalAsync(logger, dbContext, user, maxGroupsByUser, leaveGroup).ConfigureAwait(false);
            return;
        }

        var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await EraseUserInternalAsync(logger, dbContext, user, maxGroupsByUser, leaveGroup).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
    }

    private static async Task EraseUserInternalAsync(ILogger logger, MareDbContext dbContext, User user, int maxGroupsByUser, Func<GroupPair, Task> leaveGroup)
    {
        await RemoveUserContentAsync(dbContext, user.UID).ConfigureAwait(false);
        await RemoveUserAccessAndRelationsAsync(dbContext, user).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        await RemoveUserFromGroupsAsync(logger, dbContext, user, maxGroupsByUser, leaveGroup).ConfigureAwait(false);
        await RemoveOrReassignGroupBansAsync(dbContext, user.UID).ConfigureAwait(false);

        var auth = await dbContext.Auth.Where(a => a.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        dbContext.Auth.RemoveRange(auth);
        dbContext.Users.Remove(user);

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<bool> TryMigrateGroup(MareDbContext context, Group group, string potentialNewOwnerUid, int maxGroupsByUser)
    {
        var newOwnerOwnedGroups = await context.Groups.CountAsync(g => g.OwnerUID == potentialNewOwnerUid).ConfigureAwait(false);
        if (newOwnerOwnedGroups >= maxGroupsByUser)
        {
            return false;
        }
        group.OwnerUID = potentialNewOwnerUid;
        group.Alias = null;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static async Task PurgeSecondaryUsers(ILogger logger, User user, MareDbContext dbContext, int maxGroupsByUser)
    {
        var secondaryUsers = await dbContext.Auth.Include(u => u.User)
            .Where(u => u.PrimaryUserUID == user.UID)
            .Select(c => c.User)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var secondaryUser in secondaryUsers)
        {
            await PurgeUser(logger, secondaryUser, dbContext, maxGroupsByUser).ConfigureAwait(false);
        }
    }

    private static async Task RemoveUserContentAsync(MareDbContext dbContext, string uid)
    {
        dbContext.CharacterRpProfiles.RemoveRange(await dbContext.CharacterRpProfiles.Where(r => r.UserUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.CharaData.RemoveRange(await dbContext.CharaData
            .Include(c => c.Files)
            .Include(c => c.Poses)
            .Include(c => c.FileSwaps)
            .Include(c => c.OriginalFiles)
            .Include(c => c.AllowedIndividiuals)
            .Where(c => c.UploaderUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.McdfShares.RemoveRange(await dbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.HousingShares.RemoveRange(await dbContext.HousingShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.HousingScenarios.RemoveRange(await dbContext.HousingScenarios
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Include(s => s.AllowedEditors)
            .Where(s => s.OwnerUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.Establishments.RemoveRange(await dbContext.Establishments
            .Include(e => e.Events)
            .Where(e => e.OwnerUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.WildRpAnnouncements.RemoveRange(await dbContext.WildRpAnnouncements.Where(a => a.UserUID == uid).ToListAsync().ConfigureAwait(false));

        // Les fichiers sont adressés par leur contenu et servent à tous les joueurs qui utilisent le même mod :
        // on retire seulement l'attribution au compte supprimé, sans effacer le fichier.
        await dbContext.Files.Where(f => f.UploaderUID == uid)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.UploaderUID, (string)null)).ConfigureAwait(false);

        // Les signalements ne peuvent pas être anonymisés (clé étrangère vers users) : ils sont supprimés.
        dbContext.UserProfileReports.RemoveRange(await dbContext.UserProfileReports
            .Where(r => r.ReportedUserUID == uid || r.ReportingUserUID == uid).ToListAsync().ConfigureAwait(false));
    }

    private static async Task RemoveUserAccessAndRelationsAsync(MareDbContext dbContext, User user)
    {
        var uid = user.UID;

        // Les listes d'accès des partages stockent des identifiants saisis, normalisés en majuscules.
        List<string> accessIdentifiers = [uid, uid.ToUpperInvariant()];
        if (!string.IsNullOrWhiteSpace(user.Alias))
            accessIdentifiers.Add(user.Alias.Trim().ToUpperInvariant());
        accessIdentifiers = accessIdentifiers.Distinct(StringComparer.Ordinal).ToList();

        // Accès accordés à l'utilisateur sur les partages d'autrui.
        dbContext.CharaDataAllowances.RemoveRange(await dbContext.CharaDataAllowances.Where(a => a.AllowedUserUID == uid).ToListAsync().ConfigureAwait(false));
        dbContext.McdfShareAllowedUsers.RemoveRange(await dbContext.McdfShareAllowedUsers.Where(a => accessIdentifiers.Contains(a.AllowedIndividualUid)).ToListAsync().ConfigureAwait(false));
        dbContext.HousingShareAllowedUsers.RemoveRange(await dbContext.HousingShareAllowedUsers.Where(a => accessIdentifiers.Contains(a.AllowedIndividualUid)).ToListAsync().ConfigureAwait(false));
        dbContext.HousingScenarioAllowedUsers.RemoveRange(await dbContext.HousingScenarioAllowedUsers.Where(a => accessIdentifiers.Contains(a.AllowedIndividualUid)).ToListAsync().ConfigureAwait(false));
        dbContext.HousingScenarioAllowedEditors.RemoveRange(await dbContext.HousingScenarioAllowedEditors.Where(a => accessIdentifiers.Contains(a.EditorUid)).ToListAsync().ConfigureAwait(false));

        dbContext.Permissions.RemoveRange(await dbContext.Permissions.Where(p => p.UserUID == uid || p.OtherUserUID == uid).ToListAsync().ConfigureAwait(false));
        dbContext.UserDefaultPreferredPermissions.RemoveRange(await dbContext.UserDefaultPreferredPermissions.Where(p => p.UserUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.LodeStoneAuth.RemoveRange(await dbContext.LodeStoneAuth.Where(a => a.User.UID == uid).ToListAsync().ConfigureAwait(false));
        dbContext.UserProfileData.RemoveRange(await dbContext.UserProfileData.Where(u => u.UserUID == uid).ToListAsync().ConfigureAwait(false));

        dbContext.ClientPairs.RemoveRange(await dbContext.ClientPairs.Where(p => p.UserUID == uid || p.OtherUserUID == uid).ToListAsync().ConfigureAwait(false));
    }

    private static async Task RemoveUserFromGroupsAsync(ILogger logger, MareDbContext dbContext, User user, int maxGroupsByUser, Func<GroupPair, Task> leaveGroup)
    {
        var userJoinedGroups = await dbContext.GroupPairs.Include(g => g.Group)
            .Where(u => u.GroupUserUID == user.UID)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var userGroupPair in userJoinedGroups)
        {
            if (leaveGroup != null)
            {
                await leaveGroup(userGroupPair).ConfigureAwait(false);
                continue;
            }

            bool ownerHasLeft = string.Equals(userGroupPair.Group.OwnerUID, user.UID, StringComparison.Ordinal);

            if (ownerHasLeft)
            {
                var groupPairs = await dbContext.GroupPairs
                    .Where(g => g.GroupGID == userGroupPair.GroupGID && g.GroupUserUID != user.UID)
                    .ToListAsync()
                    .ConfigureAwait(false);

                if (!groupPairs.Any())
                {
                    logger.LogInformation("Group {gid} has no new owner, deleting", userGroupPair.GroupGID);
                    dbContext.Groups.Remove(userGroupPair.Group);
                }
                else
                {
                    _ = await MigrateOrDeleteGroup(dbContext, userGroupPair.Group, groupPairs, maxGroupsByUser).ConfigureAwait(false);
                }
            }

            dbContext.GroupPairs.Remove(userGroupPair);

            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        dbContext.GroupPairPreferredPermissions.RemoveRange(await dbContext.GroupPairPreferredPermissions.Where(p => p.UserUID == user.UID).ToListAsync().ConfigureAwait(false));

        // Syncshells dont l'utilisateur est resté propriétaire sans en être membre.
        var orphanedGroups = await dbContext.Groups.Where(g => g.OwnerUID == user.UID).ToListAsync().ConfigureAwait(false);
        foreach (var group in orphanedGroups)
        {
            var groupPairs = await dbContext.GroupPairs.Where(g => g.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
            if (groupPairs.Count == 0)
            {
                logger.LogInformation("Group {gid} has no new owner, deleting", group.GID);
                dbContext.Groups.Remove(group);
            }
            else
            {
                _ = await MigrateOrDeleteGroup(dbContext, group, groupPairs, maxGroupsByUser).ConfigureAwait(false);
            }
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    // À appeler une fois les syncshells quittées et la propriété transférée : les bannissements
    // émis dans les groupes qui survivent sont réattribués au propriétaire actuel.
    private static async Task RemoveOrReassignGroupBansAsync(MareDbContext dbContext, string uid)
    {
        var bans = await dbContext.GroupBans.Include(b => b.Group)
            .Where(b => b.BannedUserUID == uid || b.BannedByUID == uid)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var ban in bans)
        {
            bool isBannedUser = string.Equals(ban.BannedUserUID, uid, StringComparison.Ordinal);
            bool hasNewOwner = ban.Group != null && !string.Equals(ban.Group.OwnerUID, uid, StringComparison.Ordinal);

            if (isBannedUser || !hasNewOwner)
                dbContext.GroupBans.Remove(ban);
            else
                ban.BannedByUID = ban.Group.OwnerUID;
        }
    }
}
