using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Authenticated")]
    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto defaultPermissions)
    {
        _logger.LogCallInfo(MareHubLogger.Args(defaultPermissions));

        var prefs = await DbContext.UserDefaultPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == UserUID).ConfigureAwait(false);
        if (prefs == null)
        {
            prefs = new UserDefaultPreferredPermission { UserUID = UserUID };
            await DbContext.UserDefaultPreferredPermissions.AddAsync(prefs).ConfigureAwait(false);
        }

        prefs.DisableGroupAnimations = defaultPermissions.DisableGroupAnimations;
        prefs.DisableGroupSounds = defaultPermissions.DisableGroupSounds;
        prefs.DisableGroupVFX = defaultPermissions.DisableGroupVFX;
        prefs.DisableIndividualAnimations = defaultPermissions.DisableIndividualAnimations;
        prefs.DisableIndividualSounds = defaultPermissions.DisableIndividualSounds;
        prefs.DisableIndividualVFX = defaultPermissions.DisableIndividualVFX;
        prefs.IndividualIsSticky = defaultPermissions.IndividualIsSticky;

        DbContext.Update(prefs);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Caller.Client_UserUpdateDefaultPermissions(defaultPermissions).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(
            "Individual", dto.AffectedUsers.Count,
            "Group", dto.AffectedGroups.Count));

        // On retire l'auto-référence si le client l'a envoyée par erreur
        dto.AffectedUsers.Remove(UserUID);
        if (dto.AffectedUsers.Count == 0 && dto.AffectedGroups.Count == 0) return;

        var ownDefaultPerms = await DbContext.UserDefaultPreferredPermissions.AsNoTracking().SingleOrDefaultAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        // ===== Permissions individuelles =====
        foreach (var entry in dto.AffectedUsers)
        {
            var newPerm = entry.Value;

            // Vérifie qu'on a bien une pair directe avec cet user (sinon on ne touche à rien)
            var pair = await DbContext.ClientPairs.AsNoTracking()
                .SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == entry.Key).ConfigureAwait(false);
            if (pair == null) continue;

            var prevPerms = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == entry.Key).ConfigureAwait(false);
            if (prevPerms == null)
            {
                prevPerms = new UserPermissionSet { UserUID = UserUID, OtherUserUID = entry.Key };
                await DbContext.Permissions.AddAsync(prevPerms).ConfigureAwait(false);
            }

            bool setSticky = false;
            if (!prevPerms.Sticky && !newPerm.IsSticky())
            {
                setSticky = ownDefaultPerms?.IndividualIsSticky ?? false;
            }

            var pauseChange = prevPerms.IsPaused != newPerm.IsPaused();

            prevPerms.IsPaused = newPerm.IsPaused();
            prevPerms.DisableAnimations = newPerm.IsDisableAnimations();
            prevPerms.DisableSounds = newPerm.IsDisableSounds();
            prevPerms.DisableVFX = newPerm.IsDisableVFX();
            prevPerms.Sticky = newPerm.IsSticky() || setSticky;
            DbContext.Update(prevPerms);

            // Notifications
            var permCopy = newPerm;
            permCopy.SetSticky(prevPerms.Sticky);
            var permToOther = newPerm;
            permToOther.SetSticky(false);

            await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(new UserPermissionsDto(new UserData(entry.Key), permCopy)).ConfigureAwait(false);

            // L'autre côté est-il appairé avec nous ? Si oui, lui pousser l'update
            var otherPair = await DbContext.ClientPairs.AsNoTracking()
                .SingleOrDefaultAsync(p => p.UserUID == entry.Key && p.OtherUserUID == UserUID).ConfigureAwait(false);
            if (otherPair == null) continue;

            await Clients.User(entry.Key).Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID), permToOther)).ConfigureAwait(false);

            if (pauseChange)
            {
                var otherPermsRow = await DbContext.Permissions.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.UserUID == entry.Key && p.OtherUserUID == UserUID).ConfigureAwait(false);
                if (otherPermsRow == null || otherPermsRow.IsPaused) continue;

                var otherCharaIdent = await GetUserIdent(entry.Key).ConfigureAwait(false);
                if (UserCharaIdent == null || otherCharaIdent == null) continue;

                if (newPerm.IsPaused())
                {
                    await Clients.User(UserUID).Client_UserSendOffline(new UserDto(new UserData(entry.Key))).ConfigureAwait(false);
                    await Clients.User(entry.Key).Client_UserSendOffline(new UserDto(new UserData(UserUID))).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(new UserData(entry.Key), otherCharaIdent)).ConfigureAwait(false);
                    await Clients.User(entry.Key).Client_UserSendOnline(new(new UserData(UserUID), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }

        // ===== Permissions par groupe =====
        foreach (var entry in dto.AffectedGroups)
        {
            // Vérifie qu'on est bien membre du groupe
            var membership = await DbContext.GroupPairs.AsNoTracking()
                .SingleOrDefaultAsync(g => g.GroupGID == entry.Key && g.GroupUserUID == UserUID).ConfigureAwait(false);
            if (membership == null) continue;

            var groupPrefs = await DbContext.GroupPairPreferredPermissions
                .SingleOrDefaultAsync(g => g.GroupGID == entry.Key && g.UserUID == UserUID).ConfigureAwait(false);
            if (groupPrefs == null)
            {
                groupPrefs = new GroupPairPreferredPermission { GroupGID = entry.Key, UserUID = UserUID };
                await DbContext.GroupPairPreferredPermissions.AddAsync(groupPrefs).ConfigureAwait(false);
            }

            groupPrefs.IsPaused = entry.Value.IsPaused();
            groupPrefs.DisableAnimations = entry.Value.IsDisableAnimations();
            groupPrefs.DisableSounds = entry.Value.IsDisableSounds();
            groupPrefs.DisableVFX = entry.Value.IsDisableVFX();
            DbContext.Update(groupPrefs);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Une pause (individuelle ou par syncshell) retire des destinataires : le cache doit être recalculé des deux côtés.
        var affectedGroupGids = dto.AffectedGroups.Keys.ToList();
        var affectedGroupMembers = affectedGroupGids.Count == 0
            ? new List<string>()
            : await DbContext.GroupPairs.AsNoTracking()
                .Where(g => affectedGroupGids.Contains(g.GroupGID))
                .Select(g => g.GroupUserUID)
                .ToListAsync().ConfigureAwait(false);
        _pairCacheService.InvalidateUsers(dto.AffectedUsers.Keys.Concat(affectedGroupMembers).Append(UserUID));
    }
}
