using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using MareSynchronosServer.Services.AutoDetect;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, reason));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;

        var alias = string.IsNullOrEmpty(groupPair.GroupUser.Alias) ? "-" : groupPair.GroupUser.Alias;
        var ban = new GroupBan()
        {
            BannedByUID = UserUID,
            BannedReason = $"{reason} (Alias at time of ban: {alias})",
            BannedOn = DateTime.UtcNow,
            BannedUserUID = dto.User.UID,
            GroupGID = dto.Group.GID,
        };

        DbContext.Add(ban);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await GroupRemoveUser(dto).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        group.InvitesEnabled = !dto.Permissions.HasFlag(GroupPermissions.DisableInvites);
        group.PreferDisableSounds = dto.Permissions.HasFlag(GroupPermissions.DisableSounds);
        group.PreferDisableAnimations = dto.Permissions.HasFlag(GroupPermissions.DisableAnimations);
        group.PreferDisableVFX = dto.Permissions.HasFlag(GroupPermissions.DisableVFX);
        group.IsPaused = dto.Permissions.HasFlag(GroupPermissions.Paused);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).Client_GroupChangePermissions(new GroupPermissionDto(dto.Group, dto.Permissions)).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (inGroup, groupPair) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!inGroup) return;

        var groupPrefs = await DbContext.GroupPairPreferredPermissions
            .SingleOrDefaultAsync(p => p.GroupGID == dto.Group.GID && p.UserUID == UserUID).ConfigureAwait(false);
        if (groupPrefs == null)
        {
            groupPrefs = new GroupPairPreferredPermission { GroupGID = dto.Group.GID, UserUID = UserUID };
            await DbContext.GroupPairPreferredPermissions.AddAsync(groupPrefs).ConfigureAwait(false);
        }

        var wasPaused = groupPrefs.IsPaused;
        groupPrefs.DisableSounds = dto.GroupPairPermissions.IsDisableSounds();
        groupPrefs.DisableAnimations = dto.GroupPairPermissions.IsDisableAnimations();
        groupPrefs.IsPaused = dto.GroupPairPermissions.IsPaused();
        groupPrefs.DisableVFX = dto.GroupPairPermissions.IsDisableVFX();

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = DbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == dto.Group.GID).ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupPairChangePermissions(dto).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
        var self = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        if (wasPaused == groupPrefs.IsPaused) return;

        foreach (var groupUserPair in groupPairs.Where(u => !string.Equals(u.GroupUserUID, UserUID, StringComparison.Ordinal)).ToList())
        {
            var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair != null)
            {
                if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
                if (userPair.IsPausedExcludingGroup(dto.Group.GID) is PauseInfo.Unpaused) continue;
                if (userPair.IsOtherPausedForSpecificGroup(dto.Group.GID) is PauseInfo.Paused) continue;
            }

            var groupUserIdent = await GetUserIdent(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                if (!groupPrefs.IsPaused)
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(groupUserPair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                    await Clients.User(groupUserPair.GroupUserUID)
                        .Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(UserUID).Client_UserSendOffline(new(groupUserPair.ToUserData())).ConfigureAwait(false);
                    await Clients.User(groupUserPair.GroupUserUID)
                        .Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);
                }
            }
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetMaxUserCount(GroupDto dto, int max)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, max));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return false;

        // Clamp requested max between 1 and absolute server cap
        var newMax = Math.Clamp(max, 1, _absoluteMaxGroupUserCount);

        if (group.MaxUserCount == newMax) return true;

        group.MaxUserCount = newMax;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Notify all group members of updated group info
        var groupPairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await DbContext.Entry(group).Reference(g => g.Owner).LoadAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.GetGroupPermissions())
        {
            IsTemporary = group.IsTemporary,
            ExpiresAt = group.ExpiresAt,
            AutoDetectVisible = group.AutoDetectVisible,
            PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
            MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
        }).ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeOwnership(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return;

        var (isInGroup, newOwnerPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!isInGroup) return;

        var ownedShells = await DbContext.Groups.CountAsync(g => g.OwnerUID == dto.User.UID).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await DbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.GroupUserUID == UserUID).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = newOwnerPair.GroupUser;
        group.Alias = null;
        newOwnerPair.IsPinned = true;
        newOwnerPair.IsModerator = false;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), newOwnerPair.GroupUser.ToUserData(), group.GetGroupPermissions())
        {
            IsTemporary = group.IsTemporary,
            ExpiresAt = group.ExpiresAt,
            AutoDetectVisible = group.AutoDetectVisible,
            PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
            MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
        }).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupChangePassword(GroupPasswordDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner || dto.Password.Length < 10) return false;

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        group.HashedPassword = StringUtils.Sha256String(dto.Password);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    private static int? SanitizeDisplayDuration(int? displayDurationHours)
    {
        if (!displayDurationHours.HasValue) return null;
        return Math.Clamp(displayDurationHours.Value, 1, 240);
    }

    private static int[]? SanitizeWeekdays(int[]? activeWeekdays)
    {
        if (activeWeekdays == null) return null;

        var sanitized = activeWeekdays
            .Where(d => d >= 0 && d <= 6)
            .Distinct()
            .OrderBy(d => d)
            .ToArray();

        return sanitized.Length == 0 ? Array.Empty<int>() : sanitized;
    }

    private static string? ParseLocalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)) return null;
        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return null;

        return new TimeSpan(parsed.Hours, parsed.Minutes, 0).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupClear(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !p.IsPinned && !p.IsModerator).Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var notPinned = groupPairs.Where(g => !g.IsPinned && !g.IsModerator).ToList();

        DbContext.GroupPairs.RemoveRange(notPinned);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned).Select(g => g.GroupUserUID)).Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);

            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var allUserPairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == pair.GroupUserUID).ToListAsync().ConfigureAwait(false);
            DbContext.CharaDataAllowances.RemoveRange(sharedData);

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, allUserPairs, pairIdent).ConfigureAwait(false);
            }
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupPasswordDto> GroupCreate(string? alias)
    {
        _logger.LogCallInfo();
        var existingGroupsByUser = await DbContext.Groups.CountAsync(u => u.OwnerUID == UserUID).ConfigureAwait(false);
        var existingJoinedGroups = await DbContext.GroupPairs.CountAsync(u => u.GroupUserUID == UserUID).ConfigureAwait(false);
        if (existingGroupsByUser >= _maxExistingGroupsByUser || existingJoinedGroups >= _maxJoinedGroupsByUser)
        {
            throw new System.Exception($"Max groups for user is {_maxExistingGroupsByUser}, max joined groups is {_maxJoinedGroupsByUser}.");
        }

        var gid = StringUtils.GenerateRandomString(9);
        while (await DbContext.Groups.AnyAsync(g => g.GID == "UMB-" + gid).ConfigureAwait(false))
        {
            gid = StringUtils.GenerateRandomString(9);
        }
        gid = "UMB-" + gid;

        var passwd = StringUtils.GenerateRandomString(16);
        var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        string? sanitizedAlias = null;
        if (!string.IsNullOrWhiteSpace(alias))
        {
            sanitizedAlias = alias.Trim();
            if (sanitizedAlias.Length > 50)
            {
                sanitizedAlias = sanitizedAlias[..50];
            }

            var aliasExists = await DbContext.Groups
                .AnyAsync(g => g.Alias != null && EF.Functions.ILike(g.Alias!, sanitizedAlias))
                .ConfigureAwait(false);
            if (aliasExists)
            {
                throw new System.Exception("Syncshell name is already in use.");
            }
        }

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = UserUID,
            Alias = sanitizedAlias,
            IsTemporary = false,
            ExpiresAt = null,
            MaxUserCount = _defaultGroupUserCount,
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = UserUID,
            IsPinned = true,
        };

        await DbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await DbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(newGroup.ToGroupData(), self.ToUserData(), GroupPermissions.NoneSet, GroupUserPermissions.NoneSet, GroupUserInfo.None)
        {
            IsTemporary = newGroup.IsTemporary,
            ExpiresAt = newGroup.ExpiresAt,
            AutoDetectVisible = newGroup.AutoDetectVisible,
            PasswordTemporarilyDisabled = newGroup.PasswordTemporarilyDisabled,
            MaxUserCount = Math.Min(newGroup.MaxUserCount > 0 ? newGroup.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
        }).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid));

        return new GroupPasswordDto(newGroup.ToGroupData(), passwd)
        {
            IsTemporary = newGroup.IsTemporary,
            ExpiresAt = newGroup.ExpiresAt,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupPasswordDto> GroupCreateTemporary(DateTime expiresAtUtc)
    {
        _logger.LogCallInfo();

        var now = DateTime.UtcNow;
        if (expiresAtUtc.Kind == DateTimeKind.Unspecified)
        {
            expiresAtUtc = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc);
        }

        if (expiresAtUtc <= now)
        {
            throw new System.Exception("Expiration must be in the future.");
        }

        if (expiresAtUtc > now.AddDays(7))
        {
            throw new System.Exception("Temporary syncshells may not last longer than 7 days.");
        }

        var existingGroupsByUser = await DbContext.Groups.CountAsync(u => u.OwnerUID == UserUID).ConfigureAwait(false);
        var existingJoinedGroups = await DbContext.GroupPairs.CountAsync(u => u.GroupUserUID == UserUID).ConfigureAwait(false);
        if (existingGroupsByUser >= _maxExistingGroupsByUser || existingJoinedGroups >= _maxJoinedGroupsByUser)
        {
            throw new System.Exception($"Max groups for user is {_maxExistingGroupsByUser}, max joined groups is {_maxJoinedGroupsByUser}.");
        }

        var gid = StringUtils.GenerateRandomString(9);
        while (await DbContext.Groups.AnyAsync(g => g.GID == "UMB-" + gid).ConfigureAwait(false))
        {
            gid = StringUtils.GenerateRandomString(9);
        }
        gid = "UMB-" + gid;

        var passwd = StringUtils.GenerateRandomString(16);
        var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = UserUID,
            Alias = null,
            IsTemporary = true,
            ExpiresAt = expiresAtUtc,
            MaxUserCount = _defaultGroupUserCount,
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = UserUID,
            IsPinned = true,
        };

        await DbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await DbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(newGroup.ToGroupData(), self.ToUserData(), GroupPermissions.NoneSet, GroupUserPermissions.NoneSet, GroupUserInfo.None)
        {
            IsTemporary = newGroup.IsTemporary,
            ExpiresAt = newGroup.ExpiresAt,
            AutoDetectVisible = newGroup.AutoDetectVisible,
            PasswordTemporarilyDisabled = newGroup.PasswordTemporarilyDisabled,
        }).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Temporary", expiresAtUtc));

        return new GroupPasswordDto(newGroup.ToGroupData(), passwd)
        {
            IsTemporary = newGroup.IsTemporary,
            ExpiresAt = newGroup.ExpiresAt,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<string>> GroupCreateTempInvite(GroupDto dto, int amount)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, amount));
        List<string> inviteCodes = new();
        List<GroupTempInvite> tempInvites = new();
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return new();

        var existingInvites = await DbContext.GroupTempInvites.Where(g => g.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);

        for (int i = 0; i < amount; i++)
        {
            bool hasValidInvite = false;
            string invite = string.Empty;
            string hashedInvite = string.Empty;
            while (!hasValidInvite)
            {
                invite = StringUtils.GenerateRandomString(10, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                hashedInvite = StringUtils.Sha256String(invite);
                if (existingInvites.Any(i => string.Equals(i.Invite, hashedInvite, StringComparison.Ordinal))) continue;
                hasValidInvite = true;
                inviteCodes.Add(invite);
            }

            tempInvites.Add(new GroupTempInvite()
            {
                ExpirationDate = DateTime.UtcNow.AddDays(1),
                GroupGID = group.GID,
                Invite = hashedInvite,
            });
        }

        DbContext.GroupTempInvites.AddRange(tempInvites);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return inviteCodes;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupDelete(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);
        DbContext.RemoveRange(groupPairs);
        DbContext.Remove(group);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.GID).ConfigureAwait(false);
        if (!userHasRights) return new List<BannedGroupUserDto>();

        var banEntries = await DbContext.GroupBans.Include(b => b.BannedUser).Where(g => g.GroupGID == dto.Group.GID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b =>
            new BannedGroupUserDto(group.ToGroupData(), b.BannedUser.ToUserData(), b.BannedReason, b.BannedOn,
                b.BannedByUID)).ToList();

        _logger.LogCallInfo(MareHubLogger.Args(dto, bannedGroupUsers.Count));

        return bannedGroupUsers;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoin(GroupPasswordDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(MareHubLogger.Args(dto.Group));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);

        return await JoinGroupInternal(group, aliasOrGid, hashedPw, allowPasswordless: false, skipInviteCheck: false, isSlotJoin: false).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> SyncshellDiscoveryJoin(GroupDto dto)
    {
        var gid = dto.Group.GID.Trim();

        _logger.LogCallInfo(MareHubLogger.Args(dto.Group));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);

        return await JoinGroupInternal(group, gid, hashedPassword: null, allowPasswordless: true, skipInviteCheck: true, isSlotJoin: false).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<SyncshellDiscoveryEntryDto>> SyncshellDiscoveryList()
    {
        _logger.LogCallInfo();

        var groups = await DbContext.Groups
            .Include(g => g.Owner)
            .Where(g => g.AutoDetectVisible && (!g.IsTemporary || g.ExpiresAt == null || g.ExpiresAt > DateTime.UtcNow))
            .ToListAsync().ConfigureAwait(false);

        // Enforce schedule gating at query time to avoid showing shells outside their window
        bool changed = false;
        foreach (var g in groups.ToList())
        {
            var schedule = _autoDetectScheduleCache.Get(g.GID);
            if (schedule == null) continue;

            var desired = AutoDetectScheduleEvaluator.EvaluateDesiredVisibility(schedule, DateTimeOffset.UtcNow);
            if (desired.HasValue && !desired.Value && g.AutoDetectVisible)
            {
                // mark invisible and keep password state consistent
                g.AutoDetectVisible = false;
                g.PasswordTemporarilyDisabled = false;
                changed = true;
            }

            if (desired.HasValue && !desired.Value)
            {
                groups.Remove(g);
            }
        }

        if (changed)
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        var groupIds = groups.Select(g => g.GID).ToArray();
        var memberCounts = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => groupIds.Contains(p.GroupGID))
            .GroupBy(p => p.GroupGID)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(k => k.Key, k => k.Count, StringComparer.OrdinalIgnoreCase)
            .ConfigureAwait(false);

        // Load group profiles for discovery enrichment
        var profiles = await DbContext.GroupProfiles.AsNoTracking()
            .Where(p => groupIds.Contains(p.GroupGID))
            .ToDictionaryAsync(p => p.GroupGID, StringComparer.OrdinalIgnoreCase)
            .ConfigureAwait(false);

        return groups
            .Where(g =>
            {
                // Exclude groups with disabled profiles
                if (profiles.TryGetValue(g.GID, out var p) && p.IsDisabled) return false;
                return true;
            })
            .Select(g =>
            {
                profiles.TryGetValue(g.GID, out var profile);
                return new SyncshellDiscoveryEntryDto
                {
                    GID = g.GID,
                    Alias = g.Alias,
                    OwnerUID = g.OwnerUID,
                    OwnerAlias = g.Owner.Alias,
                    MemberCount = memberCounts.TryGetValue(g.GID, out var count) ? count : 0,
                    AutoAcceptPairs = g.InvitesEnabled,
                    Description = profile?.Description,
                    MaxUserCount = Math.Min(g.MaxUserCount > 0 ? g.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
                    IsNsfw = profile?.IsNSFW ?? false,
                    Tags = profile?.Tags,
                };
            }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellDiscoveryStateDto?> SyncshellDiscoveryGetState(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.Group));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return null;

        var schedule = _autoDetectScheduleCache.Get(group.GID);

        // Determine effective mode from schedule state
        AutoDetectMode mode;
        if (!group.AutoDetectVisible && schedule == null)
            mode = AutoDetectMode.Off;
        else if (schedule == null)
            mode = AutoDetectMode.Fulltime;
        else if (schedule.Recurring)
            mode = AutoDetectMode.Recurring;
        else
            mode = AutoDetectMode.Duration;

        return new SyncshellDiscoveryStateDto
        {
            GID = group.GID,
            AutoDetectVisible = group.AutoDetectVisible,
            PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
            Mode = mode,
            DisplayDurationHours = schedule?.DisplayDurationHours,
            ActiveWeekdays = schedule?.ActiveWeekdays,
            TimeStartLocal = schedule?.TimeStartLocal,
            TimeEndLocal = schedule?.TimeEndLocal,
            TimeZone = schedule?.TimeZone,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> SyncshellDiscoverySetVisibility(SyncshellDiscoveryVisibilityRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));
        // Facade: traduire l'ancien contrat en "Policy" (Off / Duration / Recurring)
        var mode = dto.AutoDetectVisible ? AutoDetectMode.Duration : AutoDetectMode.Off;
        int? duration = null;
        var weekdays = SanitizeWeekdays(dto.ActiveWeekdays);
        var start = ParseLocalTime(dto.TimeStartLocal);
        var end = ParseLocalTime(dto.TimeEndLocal);
        var tz = string.IsNullOrWhiteSpace(dto.TimeZone) ? null : dto.TimeZone.Trim();

        if (dto.AutoDetectVisible)
        {
            // Recurring seulement si jours non vides ET start/end valides ET differentes
            bool canRecurring = weekdays != null && weekdays.Length > 0
                                && !string.IsNullOrWhiteSpace(start)
                                && !string.IsNullOrWhiteSpace(end)
                                && !string.Equals(start, end, StringComparison.Ordinal);
            if (canRecurring)
            {
                mode = AutoDetectMode.Recurring;
            }
            else
            {
                duration = SanitizeDisplayDuration(dto.DisplayDurationHours);
            }
        }

        var policy = new SyncshellDiscoverySetPolicyRequestDto
        {
            GID = dto.GID,
            Mode = mode,
            DisplayDurationHours = duration,
            ActiveWeekdays = mode == AutoDetectMode.Recurring ? weekdays : null,
            TimeStartLocal = mode == AutoDetectMode.Recurring ? start : null,
            TimeEndLocal = mode == AutoDetectMode.Recurring ? end : null,
            TimeZone = mode == AutoDetectMode.Recurring ? tz : null,
        };

        return await SyncshellDiscoverySetPolicy(policy).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> SyncshellDiscoverySetPolicy(SyncshellDiscoverySetPolicyRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.GID).ConfigureAwait(false);
        if (!hasRights) return false;

        // Defaults
        int? duration = null;
        int[]? weekdays = null;
        string? start = null;
        string? end = null;
        string? tz = null;
        bool recurring = false;
        DateTime? lastActivated = null;

        // Validate & normalize according to mode
        switch (dto.Mode)
        {
            case AutoDetectMode.Off:
                // clear policy and set invisible
                _autoDetectScheduleCache.Clear(group.GID);
                group.AutoDetectVisible = false;
                group.PasswordTemporarilyDisabled = false;
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
                await DbContext.Entry(group).Reference(g => g.Owner).LoadAsync().ConfigureAwait(false);
                {
                    var pairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
                    await Clients.Users(pairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.GetGroupPermissions())
                    {
                        IsTemporary = group.IsTemporary,
                        ExpiresAt = group.ExpiresAt,
                        AutoDetectVisible = group.AutoDetectVisible,
                        PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
                        MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
                    }).ConfigureAwait(false);
                }
                return true;

            case AutoDetectMode.Duration:
                duration = SanitizeDisplayDuration(dto.DisplayDurationHours);
                if (!duration.HasValue)
                {
                    return false;
                }
                recurring = false;
                lastActivated = DateTime.UtcNow;
                break;

            case AutoDetectMode.Recurring:
                weekdays = SanitizeWeekdays(dto.ActiveWeekdays);
                start = ParseLocalTime(dto.TimeStartLocal);
                end = ParseLocalTime(dto.TimeEndLocal);
                tz = string.IsNullOrWhiteSpace(dto.TimeZone) ? null : dto.TimeZone.Trim();
                if (weekdays == null || weekdays.Length == 0) return false;
                if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end)) return false;
                if (string.Equals(start, end, StringComparison.Ordinal)) return false;
                recurring = true;
                break;

            case AutoDetectMode.Fulltime:
                // Permanent visibility: clear any schedule and set visible immediately
                _autoDetectScheduleCache.Clear(group.GID);
                group.AutoDetectVisible = true;
                group.PasswordTemporarilyDisabled = true;
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
                await DbContext.Entry(group).Reference(g => g.Owner).LoadAsync().ConfigureAwait(false);
                {
                    var pairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
                    await Clients.Users(pairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.GetGroupPermissions())
                    {
                        IsTemporary = group.IsTemporary,
                        ExpiresAt = group.ExpiresAt,
                        AutoDetectVisible = group.AutoDetectVisible,
                        PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
                        MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
                    }).ConfigureAwait(false);
                }
                return true;

            default:
                return false;
        }

        // Persist policy
        _autoDetectScheduleCache.Set(group.GID, recurring, duration, weekdays, start, end, tz, lastActivated);

        // Compute effective visibility now
        var scheduleState = _autoDetectScheduleCache.Get(group.GID);
        bool effectiveVisible;
        if (dto.Mode == AutoDetectMode.Duration)
        {
            effectiveVisible = true; // starts immediately
        }
        else if (dto.Mode == AutoDetectMode.Recurring && scheduleState != null)
        {
            var desired = AutoDetectScheduleEvaluator.EvaluateDesiredVisibility(scheduleState, DateTimeOffset.UtcNow);
            effectiveVisible = desired ?? false;
        }
        else
        {
            effectiveVisible = false;
        }

        group.AutoDetectVisible = effectiveVisible;
        group.PasswordTemporarilyDisabled = effectiveVisible;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await DbContext.Entry(group).Reference(g => g.Owner).LoadAsync().ConfigureAwait(false);
        var groupPairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.GetGroupPermissions())
        {
            IsTemporary = group.IsTemporary,
            ExpiresAt = group.ExpiresAt,
            AutoDetectVisible = group.AutoDetectVisible,
            PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
            MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
        }).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> JoinGroupInternal(Group? group, string aliasOrGid, string? hashedPassword, bool allowPasswordless, bool skipInviteCheck, bool isSlotJoin)
    {
        if (group == null) 
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Group null"));
            return false;
        }

        var groupGid = group.GID;
        var existingPair = await DbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);

        GroupTempInvite? oneTimeInvite = null;
        if (!string.IsNullOrEmpty(hashedPassword))
        {
            oneTimeInvite = await DbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPassword).ConfigureAwait(false);
        }

        if (allowPasswordless && !group.AutoDetectVisible && !isSlotJoin)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Not visible and not slot join"));
            return false;
        }

        bool passwordBypass = group.PasswordTemporarilyDisabled || allowPasswordless || isSlotJoin;
        bool passwordMatches = !string.IsNullOrEmpty(hashedPassword) && string.Equals(group.HashedPassword, hashedPassword, StringComparison.Ordinal);
        bool hasValidCredential = passwordBypass || passwordMatches || oneTimeInvite != null;

        var effectiveGroupMax = group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount;
        var effectiveCap = Math.Min(effectiveGroupMax, _absoluteMaxGroupUserCount);

        if (!hasValidCredential)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: No valid credential"));
            return false;
        }
        if (existingPair != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Already in group"));
            return false;
        }
        if (existingUserCount >= effectiveCap)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Group full", existingUserCount, effectiveCap));
            return false;
        }
        if (!skipInviteCheck && !group.InvitesEnabled)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Invites disabled"));
            return false;
        }
        if (joinedGroups >= _maxJoinedGroupsByUser)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: Max joined groups reached"));
            return false;
        }
        if (isBanned)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Join failed: User is banned"));
            return false;
        }

        if (oneTimeInvite != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "TempInvite", oneTimeInvite.Invite));
            DbContext.Remove(oneTimeInvite);
        }

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = UserUID,
        };

        await DbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Success"));

        var owner = group.Owner ?? await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == group.OwnerUID).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), owner.ToUserData(), group.GetGroupPermissions(), newPair.GetGroupPairPermissions(), newPair.GetGroupPairUserInfo())
        {
            IsTemporary = group.IsTemporary,
            ExpiresAt = group.ExpiresAt,
            AutoDetectVisible = group.AutoDetectVisible,
            PasswordTemporarilyDisabled = group.PasswordTemporarilyDisabled,
            MaxUserCount = Math.Min(group.MaxUserCount > 0 ? group.MaxUserCount : _defaultGroupUserCount, _absoluteMaxGroupUserCount),
        }).ConfigureAwait(false);

        var self = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == group.GID && p.GroupUserUID != UserUID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(p => p.GroupUserUID))
            .Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(), self.ToUserData(), newPair.GetGroupPairUserInfo(), newPair.GetGroupPairPermissions())).ConfigureAwait(false);
        foreach (var pair in groupPairs)
        {
            await Clients.User(UserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(), pair.ToUserData(), pair.GetGroupPairUserInfo(), pair.GetGroupPairPermissions())).ConfigureAwait(false);
        }

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));

            if (userPair != null)
            {
                if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
                if (userPair.IsPausedExcludingGroup(group.GID) is PauseInfo.Unpaused) continue;
                if (userPair.IsPausedPerGroup is PauseInfo.Paused) continue;
            }

            var groupUserIdent = await GetUserIdent(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(UserUID).Client_UserSendOnline(new(groupUserPair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID)
                    .Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
            }
        }

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupLeave(GroupDto dto)
    {
        await UserLeaveGroup(dto, UserUID).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<int> GroupPrune(GroupDto dto, int days, bool execute)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, days, execute));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return -1;

        var allGroupUsers = await DbContext.GroupPairs.Include(p => p.GroupUser).Include(p => p.Group)
            .Where(g => g.GroupGID == dto.Group.GID)
            .ToListAsync().ConfigureAwait(false);
        var usersToPrune = allGroupUsers.Where(p => !p.IsPinned && !p.IsModerator
            && p.GroupUserUID != UserUID
            && p.Group.OwnerUID != p.GroupUserUID
            && p.GroupUser.LastLoggedIn.AddDays(days) < DateTime.UtcNow);

        if (!execute) return usersToPrune.Count();

        DbContext.GroupPairs.RemoveRange(usersToPrune);

        foreach (var pair in usersToPrune)
        {
            await Clients.Users(allGroupUsers.Where(p => !usersToPrune.Contains(p)).Select(g => g.GroupUserUID))
                .Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return usersToPrune.Count();
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupRemoveUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;
        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        DbContext.GroupPairs.Remove(groupPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = DbContext.GroupPairs.Where(p => p.GroupGID == group.GID).AsNoTracking().ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupPairLeft(dto).ConfigureAwait(false);

        var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == dto.UID).ToListAsync().ConfigureAwait(false);
        DbContext.CharaDataAllowances.RemoveRange(sharedData);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var userIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (userIdent == null) return;

        await Clients.User(dto.User.UID).Client_GroupDelete(new GroupDto(dto.Group)).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState(dto.User.UID).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent, dto.User.UID).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userExists, userPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        var (userIsOwner, _) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        var (userIsModerator, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);

        if (dto.GroupUserInfo.HasFlag(GroupUserInfo.IsPinned) && userIsModerator && !userPair.IsPinned)
        {
            userPair.IsPinned = true;
        }
        else if (userIsModerator && userPair.IsPinned)
        {
            userPair.IsPinned = false;
        }

        if (dto.GroupUserInfo.HasFlag(GroupUserInfo.IsModerator) && userIsOwner && !userPair.IsModerator)
        {
            userPair.IsModerator = true;
        }
        else if (userIsOwner && userPair.IsModerator)
        {
            userPair.IsModerator = false;
        }


        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupPairChangeUserInfo(new GroupPairUserInfoDto(dto.Group, dto.User, userPair.GetGroupPairUserInfo())).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        _logger.LogCallInfo();

        var groups = await DbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        return groups.Select(g => new GroupFullInfoDto(g.Group.ToGroupData(), g.Group.Owner.ToUserData(),
                g.Group.GetGroupPermissions(), g.GetGroupPairPermissions(), g.GetGroupPairUserInfo())
        {
            IsTemporary = g.Group.IsTemporary,
            ExpiresAt = g.Group.ExpiresAt,
            AutoDetectVisible = g.Group.AutoDetectVisible,
            PasswordTemporarilyDisabled = g.Group.PasswordTemporarilyDisabled,
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupPairFullInfoDto>> GroupsGetUsersInGroup(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (inGroup, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!inGroup) return new List<GroupPairFullInfoDto>();

        var group = await DbContext.Groups.AsNoTracking().SingleAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        var allPairs = await DbContext.GroupPairs.Include(g => g.GroupUser).Where(g => g.GroupGID == dto.Group.GID && g.GroupUserUID != UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        return allPairs.Select(p => new GroupPairFullInfoDto(group.ToGroupData(), p.GroupUser.ToUserData(), p.GetGroupPairUserInfo(), p.GetGroupPairPermissions())).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupProfileDto?> GroupGetProfile(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (inGroup, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!inGroup) return null;

        var profile = await DbContext.GroupProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.GroupGID == dto.Group.GID).ConfigureAwait(false);

        if (profile == null)
        {
            return new GroupProfileDto { Group = dto.Group };
        }

        if (profile.IsDisabled)
        {
            return new GroupProfileDto { Group = dto.Group, IsDisabled = true };
        }

        return new GroupProfileDto
        {
            Group = dto.Group,
            Description = profile.Description,
            Tags = profile.Tags,
            ProfileImageBase64 = profile.Base64ProfileImage,
            BannerImageBase64 = profile.Base64BannerImage,
            IsNsfw = profile.IsNSFW,
            IsDisabled = false,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetProfile(GroupProfileDto dto)
    {
        if (dto.Group == null) return;

        _logger.LogCallInfo(MareHubLogger.Args(dto.Group));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        // Validate description
        var description = dto.Description;
        if (description != null && description.Length > 1500)
        {
            description = description[..1500];
        }

        // Validate tags
        var tags = dto.Tags;
        if (tags != null)
        {
            tags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Length > 50 ? t[..50] : t).Take(20).ToArray();
            if (tags.Length == 0) tags = null;
        }

        // Validate images (size check only — full validation would use ImageSharp)
        var profileImage = dto.ProfileImageBase64;
        if (profileImage != null)
        {
            try
            {
                var bytes = Convert.FromBase64String(profileImage);
                if (bytes.Length > 2 * 1024 * 1024) profileImage = null;
            }
            catch { profileImage = null; }
        }

        var bannerImage = dto.BannerImageBase64;
        if (bannerImage != null)
        {
            try
            {
                var bytes = Convert.FromBase64String(bannerImage);
                if (bytes.Length > 2 * 1024 * 1024) bannerImage = null;
            }
            catch { bannerImage = null; }
        }

        var profile = await DbContext.GroupProfiles
            .SingleOrDefaultAsync(p => p.GroupGID == dto.Group.GID).ConfigureAwait(false);

        if (profile == null)
        {
            profile = new GroupProfile
            {
                GroupGID = dto.Group.GID,
            };
            DbContext.GroupProfiles.Add(profile);
        }

        profile.Description = description;
        profile.Tags = tags;
        profile.Base64ProfileImage = profileImage;
        profile.Base64BannerImage = bannerImage;
        profile.IsNSFW = dto.IsNsfw;
        profile.IsDisabled = dto.IsDisabled;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Broadcast to all group members
        var resultDto = new GroupProfileDto
        {
            Group = dto.Group,
            Description = profile.Description,
            Tags = profile.Tags,
            ProfileImageBase64 = profile.Base64ProfileImage,
            BannerImageBase64 = profile.Base64BannerImage,
            IsNsfw = profile.IsNSFW,
            IsDisabled = profile.IsDisabled,
        };

        var groupPairs = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == dto.Group.GID)
            .Select(p => p.GroupUserUID)
            .ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).Client_GroupSendProfile(resultDto).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto.Group, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupUnbanUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var banEntry = await DbContext.GroupBans.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.BannedUserUID == dto.User.UID).ConfigureAwait(false);
        if (banEntry == null) return;

        DbContext.Remove(banEntry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));
    }
}
