using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Utils;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Group;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections.Features;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    public string UserCharaIdent => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.CharaIdent, StringComparison.Ordinal))?.Value ?? throw new Exception("No Chara Ident in Claims");

    public string UserUID => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? throw new Exception("No UID in Claims");

    public string Continent => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Continent, StringComparison.Ordinal))?.Value ?? "UNK";

    private string GetTransportType()
    {
        try
        {
            return Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private string GetUserAgent() => _contextAccessor.HttpContext?.Request.Headers["User-Agent"].ToString() ?? "null";

    private bool IsFallbackConnection() =>
        string.Equals(_contextAccessor.HttpContext?.Request.Headers["X-Umbra-Fallback"].ToString(), "1", StringComparison.Ordinal);

    private async Task SafeLifecycleStep(string stepName, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args(stepName, "failed", ex.GetType().Name, ex.Message));
        }
    }
    
    private void SafeLifecycleStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args(stepName, "failed", ex.GetType().Name, ex.Message));
        }
    }

    private async Task DeleteUser(User user)
    {
        var ownPairData = await DbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToListAsync().ConfigureAwait(false);
        var auth = await DbContext.Auth.SingleAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var lodestone = await DbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == user.UID).ConfigureAwait(false);
        var groupPairs = await DbContext.GroupPairs.Where(g => g.GroupUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var userProfileData = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var bannedEntries = await DbContext.GroupBans.Where(u => u.BannedUserUID == user.UID).ToListAsync().ConfigureAwait(false);

        // RGPD: cascade delete RP profiles
        var rpProfiles = await DbContext.CharacterRpProfiles.Where(r => r.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.CharacterRpProfiles.RemoveRange(rpProfiles);

        // RGPD: cascade delete CharaData (files, poses, swaps, originals, allowances are cascade-deleted by EF)
        var charaData = await DbContext.CharaData
            .Include(c => c.Files)
            .Include(c => c.Poses)
            .Include(c => c.FileSwaps)
            .Include(c => c.OriginalFiles)
            .Include(c => c.AllowedIndividiuals)
            .Where(c => c.UploaderUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.CharaData.RemoveRange(charaData);

        // RGPD: cascade delete MCDF shares (allowed users/groups are cascade-deleted by EF)
        var mcdfShares = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.McdfShares.RemoveRange(mcdfShares);

        // RGPD: cascade delete housing shares
        var housingShares = await DbContext.HousingShares.Where(h => h.OwnerUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.HousingShares.RemoveRange(housingShares);

        // RGPD: cascade delete uploaded files
        var uploadedFiles = await DbContext.Files.Where(f => f.UploaderUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.Files.RemoveRange(uploadedFiles);

        // RGPD: anonymize profile reports (keep for audit but remove UID reference)
        var reportsAboutUser = await DbContext.UserProfileReports.Where(r => r.ReportedUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        foreach (var report in reportsAboutUser) report.ReportedUserUID = "[deleted]";
        var reportsByUser = await DbContext.UserProfileReports.Where(r => r.ReportingUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        foreach (var report in reportsByUser) report.ReportingUserUID = "[deleted]";

        // RGPD: remove allowances where user is the allowed party
        var allowancesForUser = await DbContext.CharaDataAllowances.Where(a => a.AllowedUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.CharaDataAllowances.RemoveRange(allowancesForUser);

        // RGPD: remove MCDF share allowed entries where user is the allowed party
        var mcdfAllowedForUser = await DbContext.McdfShareAllowedUsers.Where(a => a.AllowedIndividualUid == user.UID).ToListAsync().ConfigureAwait(false);
        DbContext.McdfShareAllowedUsers.RemoveRange(mcdfAllowedForUser);

        if (lodestone != null)
        {
            DbContext.Remove(lodestone);
        }

        if (userProfileData != null)
        {
            DbContext.Remove(userProfileData);
        }

        DbContext.ClientPairs.RemoveRange(ownPairData);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        var otherPairData = await DbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == user.UID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        foreach (var pair in otherPairData)
        {
            await Clients.User(pair.UserUID).Client_UserRemoveClientPair(new(user.ToUserData())).ConfigureAwait(false);
        }

        foreach (var pair in groupPairs)
        {
            await UserLeaveGroup(new GroupDto(new GroupData(pair.GroupGID)), user.UID).ConfigureAwait(false);
        }

        _mareMetrics.IncCounter(MetricsAPI.CounterUsersRegisteredDeleted, 1);

        DbContext.GroupBans.RemoveRange(bannedEntries);
        DbContext.ClientPairs.RemoveRange(otherPairData);
        DbContext.Users.Remove(user);
        DbContext.Auth.Remove(auth);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<List<PausedEntry>> GetAllPairedClientsWithPauseState(string? uid = null)
    {
        uid ??= UserUID;

        // Pairs directes : LEFT JOIN sur user_permission_sets pour récupérer l'état de pause de chaque côté
        var directQuery = await (from userPair in DbContext.ClientPairs.AsNoTracking()
                                 join otherUserPair in DbContext.ClientPairs.AsNoTracking() on userPair.OtherUserUID equals otherUserPair.UserUID
                                 join ownPerm in DbContext.Permissions.AsNoTracking()
                                     on new { U = userPair.UserUID, O = userPair.OtherUserUID }
                                     equals new { U = ownPerm.UserUID, O = ownPerm.OtherUserUID } into ownLeft
                                 from ownPerm in ownLeft.DefaultIfEmpty()
                                 join otherPerm in DbContext.Permissions.AsNoTracking()
                                     on new { U = otherUserPair.UserUID, O = otherUserPair.OtherUserUID }
                                     equals new { U = otherPerm.UserUID, O = otherPerm.OtherUserUID } into otherLeft
                                 from otherPerm in otherLeft.DefaultIfEmpty()
                                 where otherUserPair.OtherUserUID == uid && userPair.UserUID == uid
                                 select new
                                 {
                                     UID = Convert.ToString(userPair.OtherUserUID),
                                     GID = "DIRECT",
                                     PauseStateSelf = ownPerm != null && ownPerm.IsPaused,
                                     PauseStateOther = otherPerm != null && otherPerm.IsPaused,
                                 }).ToListAsync().ConfigureAwait(false);

        // Pairs via syncshell : LEFT JOIN sur group_pair_preferred_permissions pour chaque côté + état pause group
        var groupQuery = await (from userGroupPair in DbContext.GroupPairs.AsNoTracking()
                                join otherGroupPair in DbContext.GroupPairs.AsNoTracking() on userGroupPair.GroupGID equals otherGroupPair.GroupGID
                                join g in DbContext.Groups.AsNoTracking() on userGroupPair.GroupGID equals g.GID
                                join ownGroupPrefs in DbContext.GroupPairPreferredPermissions.AsNoTracking()
                                    on new { U = userGroupPair.GroupUserUID, G = userGroupPair.GroupGID }
                                    equals new { U = ownGroupPrefs.UserUID, G = ownGroupPrefs.GroupGID } into ownGroupLeft
                                from ownGroupPrefs in ownGroupLeft.DefaultIfEmpty()
                                join otherGroupPrefs in DbContext.GroupPairPreferredPermissions.AsNoTracking()
                                    on new { U = otherGroupPair.GroupUserUID, G = otherGroupPair.GroupGID }
                                    equals new { U = otherGroupPrefs.UserUID, G = otherGroupPrefs.GroupGID } into otherGroupLeft
                                from otherGroupPrefs in otherGroupLeft.DefaultIfEmpty()
                                where userGroupPair.GroupUserUID == uid && otherGroupPair.GroupUserUID != uid
                                select new
                                {
                                    UID = Convert.ToString(otherGroupPair.GroupUserUID),
                                    GID = Convert.ToString(otherGroupPair.GroupGID),
                                    PauseStateSelf = (ownGroupPrefs != null && ownGroupPrefs.IsPaused) || g.IsPaused,
                                    PauseStateOther = (otherGroupPrefs != null && otherGroupPrefs.IsPaused) || g.IsPaused,
                                }).ToListAsync().ConfigureAwait(false);

        var query = directQuery.Concat(groupQuery).ToList();

        return query.GroupBy(g => g.UID, g => (g.GID, g.PauseStateSelf, g.PauseStateOther),
            (key, g) => new PausedEntry
            {
                UID = key,
                PauseStates = g.Select(p => new PauseState() { GID = string.Equals(p.GID, "DIRECT", StringComparison.Ordinal) ? null : p.GID, IsSelfPaused = p.PauseStateSelf, IsOtherPaused = p.PauseStateOther })
                .ToList(),
            }, StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> GetAllPairedUnpausedUsers(string? uid = null)
    {
        uid ??= UserUID;
        var ret = await GetAllPairedClientsWithPauseState(uid).ConfigureAwait(false);
        return ret.Where(k => !k.IsPaused).Select(k => k.UID).ToList();
    }

    private async Task<List<string>> GetDirectPairedUnpausedUsers(string? uid = null)
    {
        uid ??= UserUID;

        var query = await (from userPair in DbContext.ClientPairs.AsNoTracking()
                           join otherUserPair in DbContext.ClientPairs.AsNoTracking() on userPair.OtherUserUID equals otherUserPair.UserUID
                           join ownPerm in DbContext.Permissions.AsNoTracking()
                               on new { U = userPair.UserUID, O = userPair.OtherUserUID }
                               equals new { U = ownPerm.UserUID, O = ownPerm.OtherUserUID } into ownLeft
                           from ownPerm in ownLeft.DefaultIfEmpty()
                           join otherPerm in DbContext.Permissions.AsNoTracking()
                               on new { U = otherUserPair.UserUID, O = otherUserPair.OtherUserUID }
                               equals new { U = otherPerm.UserUID, O = otherPerm.OtherUserUID } into otherLeft
                           from otherPerm in otherLeft.DefaultIfEmpty()
                           where otherUserPair.OtherUserUID == uid && userPair.UserUID == uid
                                 && !(ownPerm != null && ownPerm.IsPaused)
                                 && !(otherPerm != null && otherPerm.IsPaused)
                           select Convert.ToString(userPair.OtherUserUID)).ToListAsync().ConfigureAwait(false);

        return query;
    }

    private async Task<Dictionary<string, string>> GetOnlineUsers(List<string> uids)
    {
        var result = await _redis.GetAllAsync<string>(uids.Select(u => "UID:" + u).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
        return uids.Where(u => result.TryGetValue("UID:" + u, out var ident) && !string.IsNullOrEmpty(ident)).ToDictionary(u => u, u => result["UID:" + u], StringComparer.Ordinal);
    }

    private async Task<string> GetUserIdent(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
        return await _redis.GetAsync<string>("UID:" + uid).ConfigureAwait(false);
    }

    private async Task RemoveUserFromRedis()
    {
        await _redis.RemoveAsync("UID:" + UserUID, StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task SendGroupDeletedToAll(List<GroupPair> groupUsers)
    {
        foreach (var pair in groupUsers)
        {
            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var pairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupUsers.Where(g => !string.Equals(g.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, pairs, pairIdent, pair.GroupUserUID).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<string>> SendOfflineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);

        // Also notify one-way pair partners (not covered by GetAllPairedUnpausedUsers which requires bidirectional pairs)
        await SendOfflineToOneWayPairPartners(self, usersToSendDataTo).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task SendOfflineToOneWayPairPartners(User self, List<string> alreadyNotified)
    {
        var alreadyNotifiedSet = new HashSet<string>(alreadyNotified, StringComparer.Ordinal);

        // Users who have paired with us (they have an entry where OtherUserUID == our UID)
        // but we haven't paired them back (no reverse entry)
        var incomingOnlyUids = await (from cp in DbContext.ClientPairs
                                      where cp.OtherUserUID == self.UID
                                      && !DbContext.ClientPairs.Any(r => r.UserUID == self.UID && r.OtherUserUID == cp.UserUID)
                                      select cp.UserUID)
                                      .AsNoTracking().ToListAsync().ConfigureAwait(false);

        // Users we have paired (we have an entry where OtherUserUID == their UID)
        // but they haven't paired us back (no reverse entry)
        var outgoingOnlyUids = await (from cp in DbContext.ClientPairs
                                      where cp.UserUID == self.UID
                                      && !DbContext.ClientPairs.Any(r => r.UserUID == cp.OtherUserUID && r.OtherUserUID == self.UID)
                                      select cp.OtherUserUID)
                                      .AsNoTracking().ToListAsync().ConfigureAwait(false);

        var oneWayUids = incomingOnlyUids.Concat(outgoingOnlyUids)
            .Where(uid => !alreadyNotifiedSet.Contains(uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (oneWayUids.Count > 0)
        {
            await Clients.Users(oneWayUids).Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> SendOnlineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task<(bool IsValid, Group ReferredGroup)> TryValidateGroupModeratorOrOwner(string gid)
    {
        var isOwnerResult = await TryValidateOwner(gid).ConfigureAwait(false);
        if (isOwnerResult.isValid) return (true, isOwnerResult.ReferredGroup);

        if (isOwnerResult.ReferredGroup == null) return (false, null);

        var groupPairSelf = await DbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        if (groupPairSelf == null || !groupPairSelf.IsModerator) return (false, null);

        return (true, isOwnerResult.ReferredGroup);
    }

    private async Task<(bool isValid, Group ReferredGroup)> TryValidateOwner(string gid)
    {
        var group = await DbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null) return (false, null);

        return (string.Equals(group.OwnerUID, UserUID, StringComparison.Ordinal), group);
    }

    private async Task<(bool IsValid, GroupPair ReferredPair)> TryValidateUserInGroup(string gid, string? uid = null)
    {
        uid ??= UserUID;

        var groupPair = await DbContext.GroupPairs.Include(c => c.GroupUser)
            .SingleOrDefaultAsync(g => g.GroupGID == gid && (g.GroupUserUID == uid || g.GroupUser.Alias == uid)).ConfigureAwait(false);
        if (groupPair == null) return (false, null);

        return (true, groupPair);
    }

    private async Task UpdateUserOnRedis()
    {
        await _redis.AddAsync("UID:" + UserUID, UserCharaIdent, TimeSpan.FromSeconds(60), StackExchange.Redis.When.Always, StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task UserGroupLeave(GroupPair groupUserPair, List<PausedEntry> allUserPairs, string userIdent, string? uid = null)
    {
        uid ??= UserUID;
        var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
        if (userPair != null)
        {
            if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) return;
            if (userPair.IsPausedPerGroup is PauseInfo.Unpaused) return;
        }

        var groupUserIdent = await GetUserIdent(groupUserPair.GroupUserUID).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(groupUserIdent))
        {
            await Clients.User(uid).Client_UserSendOffline(new(new(groupUserPair.GroupUserUID))).ConfigureAwait(false);
            await Clients.User(groupUserPair.GroupUserUID).Client_UserSendOffline(new(new(uid))).ConfigureAwait(false);
        }
    }

    private async Task UserLeaveGroup(GroupDto dto, string userUid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (exists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, userUid).ConfigureAwait(false);
        if (!exists) return;

        var group = await DbContext.Groups.SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
        var groupPairsWithoutSelf = groupPairs.Where(p => !string.Equals(p.GroupUserUID, userUid, StringComparison.Ordinal)).ToList();

        DbContext.GroupPairs.Remove(groupPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(userUid).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, userUid, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairsWithoutSelf.Any())
            {
                _logger.LogCallInfo(MareHubLogger.Args(dto, "Deleted"));

                DbContext.Groups.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(DbContext, group, groupPairsWithoutSelf, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    _logger.LogCallInfo(MareHubLogger.Args(dto, "Migrated", groupHasMigrated.Item2));

                    var user = await DbContext.Users.SingleAsync(u => u.UID == groupHasMigrated.Item2).ConfigureAwait(false);

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(),
                        user.ToUserData(), group.GetGroupPermissions())
                    {
                        IsTemporary = group.IsTemporary,
                        ExpiresAt = group.ExpiresAt,
                        AutoDetectVisible = group.AutoDetectVisible,
                        PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
                        MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
                    }).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogCallInfo(MareHubLogger.Args(dto, "Deleted"));

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupDelete(dto).ConfigureAwait(false);

                    await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);

                    return;
                }
            }
        }

        var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == userUid).ToListAsync().ConfigureAwait(false);
        DbContext.CharaDataAllowances.RemoveRange(sharedData);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupPairLeft(new GroupPairDto(dto.Group, groupPair.GroupUser.ToUserData())).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        var ident = await GetUserIdent(userUid).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairsWithoutSelf)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, ident, userUid).ConfigureAwait(false);
        }
    }
    
    private const string IndividualPairKey = "Individual";
    private async Task<Dictionary<string, UserInfo>> GetAllPairInfo(string uid)
    {
        // Pairs directs ClientPairs avec leur état miroir (synced si l'autre nous a aussi appairé)
        var clientPairs = from cp in DbContext.ClientPairs.AsNoTracking().Where(u => u.UserUID == uid)
                          join cp2 in DbContext.ClientPairs.AsNoTracking().Where(u => u.OtherUserUID == uid)
                              on new { UserUID = cp.UserUID, OtherUserUID = cp.OtherUserUID }
                              equals new { UserUID = cp2.OtherUserUID, OtherUserUID = cp2.UserUID }
                              into joined
                          from c in joined.DefaultIfEmpty()
                          where cp.UserUID == uid
                          select new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID,
                              Gid = string.Empty,
                              Synced = c != null
                          };

        // Pairs implicites via syncshells partagées (toujours bidirectionnels)
        var groupPairs = from gp in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID == uid)
                         join gp2 in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID != uid)
                             on new { GID = gp.GroupGID } equals new { GID = gp2.GroupGID }
                         select new
                         {
                             UserUID = gp.GroupUserUID,
                             OtherUserUID = gp2.GroupUserUID,
                             Gid = Convert.ToString(gp2.GroupGID),
                             Synced = true
                         };

        var allPairs = clientPairs.Concat(groupPairs);

        var result = from user in allPairs
                     join u in DbContext.Users.AsNoTracking() on user.OtherUserUID equals u.UID
                     join o in DbContext.Permissions.AsNoTracking().Where(u => u.UserUID == uid)
                        on new { UserUID = user.UserUID, OtherUserUID = user.OtherUserUID }
                        equals new { UserUID = o.UserUID, OtherUserUID = o.OtherUserUID }
                        into ownperms
                     from ownperm in ownperms.DefaultIfEmpty()
                     join p in DbContext.Permissions.AsNoTracking().Where(u => u.OtherUserUID == uid)
                        on new { UserUID = user.OtherUserUID, OtherUserUID = user.UserUID }
                        equals new { UserUID = p.UserUID, OtherUserUID = p.OtherUserUID }
                        into otherperms
                     from otherperm in otherperms.DefaultIfEmpty()
                     where user.UserUID == uid
                        && u.UID == user.OtherUserUID
                        && (ownperm == null || (ownperm.UserUID == user.UserUID && ownperm.OtherUserUID == user.OtherUserUID))
                        && (otherperm == null || (otherperm.OtherUserUID == user.UserUID && otherperm.UserUID == user.OtherUserUID))
                     select new
                     {
                         UserUID = user.UserUID,
                         OtherUserUID = user.OtherUserUID,
                         OtherUserAlias = u.Alias,
                         GID = user.Gid,
                         Synced = user.Synced,
                         OwnPermissions = ownperm,
                         OtherPermissions = otherperm,
                     };

        var resultList = await result.AsNoTracking().ToListAsync().ConfigureAwait(false);

        return resultList.GroupBy(g => g.OtherUserUID, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            return new UserInfo(
                g.First().OtherUserAlias,
                // IndividuallyPaired : il existe une ligne ClientPairs (GID empty) et celle-ci est syncée
                g.SingleOrDefault(p => string.IsNullOrEmpty(p.GID))?.Synced ?? false,
                // IsSynced : au moins un chemin (direct ou syncshell) est bidirectionnel
                g.Max(p => p.Synced),
                g.Select(p => string.IsNullOrEmpty(p.GID) ? IndividualPairKey : p.GID).Distinct(StringComparer.Ordinal).ToList(),
                g.First().OwnPermissions,
                g.First().OtherPermissions);
        }, StringComparer.Ordinal);
    }

    /// <summary>
    /// Vue minimale d'une paire (directe ou via syncshell) côté serveur.
    /// Utilisée par <see cref="GetAllPairInfo"/> pour matérialiser la sortie unifiée
    /// avant projection vers les DTOs réseau.
    /// </summary>
    public record UserInfo(
        string? Alias,
        bool IndividuallyPaired,
        bool IsSynced,
        List<string> GIDs,
        UserPermissionSet? OwnPermissions,
        UserPermissionSet? OtherPermissions);
}
