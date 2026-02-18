using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private static readonly string[] AllowedExtensionsForGamePaths = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb" };

    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        // don't allow adding nothing
        var uid = dto.User.UID.Trim();
        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(dto.User.UID)) return;

        // grab other user, check if it exists and if a pair already exists
        var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false);
        if (otherUser == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, UID does not exist").ConfigureAwait(false);
            return;
        }

        if (string.Equals(otherUser.UID, UserUID, StringComparison.Ordinal))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"My god you can't pair with yourself why would you do that please stop").ConfigureAwait(false);
            return;
        }

        var existingEntry =
            await DbContext.ClientPairs.AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.User.UID == UserUID && p.OtherUserUID == otherUser.UID).ConfigureAwait(false);

        if (existingEntry != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, already paired").ConfigureAwait(false);
            return;
        }

        // grab self create new client pair and save
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        ClientPair wl = new ClientPair()
        {
            IsPaused = false,
            OtherUser = otherUser,
            User = user,
        };
        await DbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // get the opposite entry of the client pair
        var otherEntry = OppositeEntry(otherUser.UID);
        var otherIdent = await GetUserIdent(otherUser.UID).ConfigureAwait(false);

        var ownPerm = UserPermissions.Paired;
        var otherPerm = UserPermissions.NoneSet;
        otherPerm.SetPaired(otherEntry != null);
        otherPerm.SetPaused(otherEntry?.IsPaused ?? false);
        var userPairResponse = new UserPairDto(otherUser.ToUserData(), ownPerm, otherPerm);
        await Clients.User(user.UID).Client_UserAddClientPair(userPairResponse).ConfigureAwait(false);

        // check if other user is online
        if (otherIdent == null || otherEntry == null) return;

        // send push with update to other user if other user is online
        await Clients.User(otherUser.UID).Client_UserAddClientPair(new UserPairDto(user.ToUserData(), otherPerm, ownPerm)).ConfigureAwait(false);

        if (!otherPerm.IsPaused())
        {
            await Clients.User(UserUID).Client_UserSendOnline(new(otherUser.ToUserData(), otherIdent)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserSendOnline(new(user.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserDelete()
    {
        _logger.LogCallInfo();

        var userEntry = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var secondaryUsers = await DbContext.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == UserUID).Select(c => c.User).ToListAsync().ConfigureAwait(false);
        foreach (var user in secondaryUsers)
        {
            await DeleteUser(user).ConfigureAwait(false);
        }

        await DeleteUser(userEntry).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs()
    {
        _logger.LogCallInfo();

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await SendOnlineToAllPairedUsers().ConfigureAwait(false);

        return pairs.Select(p => new OnlineUserIdentDto(new UserData(p.Key), p.Value)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserPairDto>> UserGetPairedClients()
    {
        _logger.LogCallInfo();

        var query =
            from userToOther in DbContext.ClientPairs
            join otherToUser in DbContext.ClientPairs
                on new
                {
                    user = userToOther.UserUID,
                    other = userToOther.OtherUserUID,
                } equals new
                {
                    user = otherToUser.OtherUserUID,
                    other = otherToUser.UserUID,
                } into leftJoin
            from otherEntry in leftJoin.DefaultIfEmpty()
            where
                userToOther.UserUID == UserUID
            select new
            {
                userToOther.OtherUser.Alias,
                userToOther.IsPaused,
                OtherIsPaused = otherEntry != null && otherEntry.IsPaused,
                userToOther.OtherUserUID,
                IsSynced = otherEntry != null,
                DisableOwnAnimations = userToOther.DisableAnimations,
                DisableOwnSounds = userToOther.DisableSounds,
                DisableOwnVFX = userToOther.DisableVFX,
                DisableOtherAnimations = otherEntry == null ? false : otherEntry.DisableAnimations,
                DisableOtherSounds = otherEntry == null ? false : otherEntry.DisableSounds,
                DisableOtherVFX = otherEntry == null ? false : otherEntry.DisableVFX
            };

        var results = await query.AsNoTracking().ToListAsync().ConfigureAwait(false);

        return results.Select(c =>
        {
            var ownPerm = UserPermissions.Paired;
            ownPerm.SetPaused(c.IsPaused);
            ownPerm.SetDisableAnimations(c.DisableOwnAnimations);
            ownPerm.SetDisableSounds(c.DisableOwnSounds);
            ownPerm.SetDisableVFX(c.DisableOwnVFX);
            var otherPerm = UserPermissions.NoneSet;
            otherPerm.SetPaired(c.IsSynced);
            otherPerm.SetPaused(c.OtherIsPaused);
            otherPerm.SetDisableAnimations(c.DisableOtherAnimations);
            otherPerm.SetDisableSounds(c.DisableOtherSounds);
            otherPerm.SetDisableVFX(c.DisableOtherVFX);
            return new UserPairDto(new(c.OtherUserUID, c.Alias), ownPerm, otherPerm);
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetAlias(string? alias)
    {
        _logger.LogCallInfo(MareHubLogger.Args(alias ?? "<clear>"));

        // Normalize input
        var trimmed = alias?.Trim();
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        // Clear alias if null/empty
        if (string.IsNullOrEmpty(trimmed))
        {
            user.Alias = null;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, "Your custom ID has been cleared.").ConfigureAwait(false);
            return;
        }

        // Validate format: 3-32 chars, letters/digits/_/- only
        var norm = trimmed.Normalize(NormalizationForm.FormKC);
        if (norm.Length < 3 || norm.Length > 32 || !Regex.IsMatch(norm, "^[A-Za-z0-9_-]+$"))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Invalid Custom ID. Use 3-32 characters: letters, digits, underscore or hyphen.").ConfigureAwait(false);
            return;
        }

        // Enforce uniqueness
        var exists = await DbContext.Users
            .AnyAsync(u => u.Alias != null && EF.Functions.ILike(u.Alias!, norm) && u.UID != UserUID)
            .ConfigureAwait(false);
        if (exists)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Custom ID '{norm}' is already taken.").ConfigureAwait(false);
            return;
        }

        user.Alias = norm;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, $"Your Custom ID is now '{norm}'.").ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<UserProfileDto> UserGetProfile(UserDto user)
    {
        _logger.LogCallInfo(MareHubLogger.Args(user));

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);

        if (!allUserPairs.Contains(user.User.UID) && !string.Equals(user.User.UID, UserUID, StringComparison.Ordinal))
        {
            return new UserProfileDto(user.User, false, null, null, "Due to the pause status you cannot access this users profile.");
        }

        var hrpData = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.User.UID).ConfigureAwait(false);
        CharacterRpProfileData? rpData = null;
        if (!string.IsNullOrEmpty(user.CharacterName) && user.WorldId.HasValue)
        {
            rpData = await DbContext.CharacterRpProfiles.SingleOrDefaultAsync(u => u.UserUID == user.User.UID && u.CharacterName == user.CharacterName && u.WorldId == user.WorldId).ConfigureAwait(false);
        }
        else
        {
            rpData = await DbContext.CharacterRpProfiles
                .Where(u => u.UserUID == user.User.UID)
                .OrderByDescending(u => !string.IsNullOrEmpty(u.RpFirstName) ? 1 : 0)
                .ThenByDescending(u => u.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
        }

        if (hrpData == null && rpData == null) return new UserProfileDto(user.User, false, null, null, null);

        if (hrpData?.FlaggedForReport ?? false) return new UserProfileDto(user.User, true, null, null, "This profile is flagged for report and pending evaluation");
        if (hrpData?.ProfileDisabled ?? false) return new UserProfileDto(user.User, true, null, null, "This profile was permanently disabled");

        return new UserProfileDto(user.User, false, hrpData?.IsNSFW, hrpData?.Base64ProfileImage, hrpData?.UserDescription,
            rpData?.RpProfilePictureBase64, rpData?.RpDescription, rpData?.IsRpNSFW,
            rpData?.RpFirstName, rpData?.RpLastName, rpData?.RpTitle, rpData?.RpAge,
            rpData?.RpRace, rpData?.RpEthnicity,
            rpData?.RpHeight, rpData?.RpBuild, rpData?.RpResidence, rpData?.RpOccupation, rpData?.RpAffiliation,
            rpData?.RpAlignment, rpData?.RpAdditionalInfo,
            rpData?.RpNameColor,
            rpData?.RpCustomFields,
            rpData?.CharacterName, rpData?.WorldId);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.CharaData.FileReplacements.Count));

        // check for honorific containing . and /
        try
        {
            var honorificJson = Encoding.Default.GetString(Convert.FromBase64String(dto.CharaData.HonorificData));
            var deserialized = JsonSerializer.Deserialize<JsonElement>(honorificJson);
            if (deserialized.TryGetProperty("Title", out var honorificTitle))
            {
                var title = honorificTitle.GetString().Normalize(NormalizationForm.FormKD);
                if (UrlRegex().IsMatch(title))
                {
                    await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your data was not pushed: The usage of URLs the Honorific titles is prohibited. Remove them to be able to continue to push data.").ConfigureAwait(false);
                    throw new HubException("Invalid data provided, Honorific title invalid: " + title);
                }
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception)
        {
            // swallow
        }

        bool hadInvalidData = false;
        List<string> invalidGamePaths = new();
        List<string> invalidFileSwapPaths = new();
        foreach (var replacement in dto.CharaData.FileReplacements.SelectMany(p => p.Value))
        {
            var invalidPaths = replacement.GamePaths.Where(p => !GamePathRegex().IsMatch(p)).ToList();
            invalidPaths.AddRange(replacement.GamePaths.Where(p => !AllowedExtensionsForGamePaths.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))));
            replacement.GamePaths = replacement.GamePaths.Where(p => !invalidPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
            bool validGamePaths = replacement.GamePaths.Any();
            bool validHash = string.IsNullOrEmpty(replacement.Hash) || HashRegex().IsMatch(replacement.Hash);
            bool validFileSwapPath = string.IsNullOrEmpty(replacement.FileSwapPath) || GamePathRegex().IsMatch(replacement.FileSwapPath);
            if (!validGamePaths || !validHash || !validFileSwapPath)
            {
                _logger.LogCallWarning(MareHubLogger.Args("Invalid Data", "GamePaths", validGamePaths, string.Join(",", invalidPaths), "Hash", validHash, replacement.Hash, "FileSwap", validFileSwapPath, replacement.FileSwapPath));
                hadInvalidData = true;
                if (!validFileSwapPath) invalidFileSwapPaths.Add(replacement.FileSwapPath);
                if (!validGamePaths) invalidGamePaths.AddRange(replacement.GamePaths);
                if (!validHash) invalidFileSwapPaths.Add(replacement.Hash);
            }
        }

        if (hadInvalidData)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "One or more of your supplied mods were rejected from the server. Consult /xllog for more information.").ConfigureAwait(false);
            throw new HubException("Invalid data provided, contact the appropriate mod creator to resolve those issues"
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidGamePaths.Select(p => "Invalid Game Path: " + p))
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidFileSwapPaths.Select(p => "Invalid FileSwap Path: " + p)));
        }

        var recipientUids = dto.Recipients.Select(r => r.UID).ToList();

        bool allCached = await _pairCacheService
            .AreAllPlayersCached(UserUID, recipientUids, Context.ConnectionAborted)
            .ConfigureAwait(false);

        List<string> validRecipients;
        if (!allCached)
        {
            var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
            validRecipients = allPairedUsers
                .Where(uid => recipientUids.Contains(uid, StringComparer.Ordinal))
                .ToList();

            await _pairCacheService
                .CachePlayers(UserUID, allPairedUsers, Context.ConnectionAborted)
                .ConfigureAwait(false);

            _logger.LogCallInfo(MareHubLogger.Args("cache_miss", "pairs", allPairedUsers.Count, "recipients", validRecipients.Count));
        }
        else
        {
            validRecipients = recipientUids;
            _logger.LogCallInfo(MareHubLogger.Args("cache_hit", "recipients", validRecipients.Count));
        }

        if (validRecipients.Count == 0)
        {
            _logger.LogCallWarning(MareHubLogger.Args("no_recipients", "requested", recipientUids.Count));
        }

        await Clients.Users(validRecipients).Client_UserReceiveCharacterData(
            new OnlineUserCharaDataDto(new UserData(UserUID), dto.CharaData)).ConfigureAwait(false);

        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, validRecipients.Count);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRemovePair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;

        // check if client pair even exists
        ClientPair callerPair =
            await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (callerPair == null) return;

        bool callerHadPaused = callerPair.IsPaused;

        // delete from database, send update info to users pair list
        DbContext.ClientPairs.Remove(callerPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.User(UserUID).Client_UserRemoveClientPair(dto).ConfigureAwait(false);

        // check if opposite entry exists
        var oppositeClientPair = OppositeEntry(dto.User.UID);
        if (oppositeClientPair == null) return;

        // check if other user is online, if no then there is no need to do anything further
        var otherIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        // get own ident and
        await Clients.User(dto.User.UID)
            .Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID),
                UserPermissions.NoneSet)).ConfigureAwait(false);

        // if the other user had paused the user the state will be offline for either, do nothing
        bool otherHadPaused = oppositeClientPair.IsPaused;
        if (!callerHadPaused && otherHadPaused) return;

        var allUsers = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
        var pauseEntry = allUsers.SingleOrDefault(f => string.Equals(f.UID, dto.User.UID, StringComparison.Ordinal));
        var isPausedInGroup = pauseEntry == null || pauseEntry.IsPausedPerGroup is PauseInfo.Paused or PauseInfo.NoConnection;

        // if neither user had paused each other and both are in unpaused groups, state will be online for both, do nothing
        if (!callerHadPaused && !otherHadPaused && !isPausedInGroup) return;

        // if neither user had paused each other and either is not in an unpaused group with each other, change state to offline
        if (!callerHadPaused && !otherHadPaused && isPausedInGroup)
        {
            await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
            await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
        }

        // if the caller had paused other but not the other has paused the caller and they are in an unpaused group together, change state to online
        if (callerHadPaused && !otherHadPaused && !isPausedInGroup)
        {
            await Clients.User(UserUID).Client_UserSendOnline(new(dto.User, otherIdent)).ConfigureAwait(false);
            await Clients.User(dto.User.UID).Client_UserSendOnline(new(new(UserUID), UserCharaIdent)).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserReportProfile(UserProfileReportDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        UserProfileDataReport report = await DbContext.UserProfileReports.SingleOrDefaultAsync(u => u.ReportedUserUID == dto.User.UID && u.ReportingUserUID == UserUID).ConfigureAwait(false);
        if (report != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "You already reported this profile and it's pending validation").ConfigureAwait(false);
            return;
        }

        UserProfileData profile = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID).ConfigureAwait(false);
        if (profile == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "This user has no profile").ConfigureAwait(false);
            return;
        }

        UserProfileDataReport reportToAdd = new()
        {
            ReportDate = DateTime.UtcNow,
            ReportingUserUID = UserUID,
            ReportReason = dto.ProfileReport,
            ReportedUserUID = dto.User.UID,
        };

        profile.FlaggedForReport = true;

        await DbContext.UserProfileReports.AddAsync(reportToAdd).ConfigureAwait(false);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var allPairedUsers = await GetAllPairedUnpausedUsers(dto.User.UID).ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await Clients.Users(pairs.Select(p => p.Key)).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
        await Clients.Users(dto.User.UID).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetPairPermissions(UserPermissionsDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;
        ClientPair pair = await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (pair == null) return;

        var pauseChange = pair.IsPaused != dto.Permissions.IsPaused();

        pair.IsPaused = dto.Permissions.IsPaused();
        pair.DisableAnimations = dto.Permissions.IsDisableAnimations();
        pair.DisableSounds = dto.Permissions.IsDisableSounds();
        pair.DisableVFX = dto.Permissions.IsDisableVFX();
        DbContext.Update(pair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var otherEntry = OppositeEntry(dto.User.UID);

        await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(dto).ConfigureAwait(false);

        if (otherEntry != null)
        {
            await Clients.User(dto.User.UID).Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID), dto.Permissions)).ConfigureAwait(false);

            if (pauseChange && _broadcastPresenceOnPermissionChange)
            {
                var otherCharaIdent = await GetUserIdent(pair.OtherUserUID).ConfigureAwait(false);

                if (UserCharaIdent == null || otherCharaIdent == null || otherEntry.IsPaused) return;

                if (dto.Permissions.IsPaused())
                {
                    await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
                    await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(dto.User, otherCharaIdent)).ConfigureAwait(false);
                    await Clients.User(dto.User.UID).Client_UserSendOnline(new(new(UserUID), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }
        
        var pausedState = dto.Permissions.IsPaused();
        _logger.LogCallInfo(MareHubLogger.Args("PermissionsUpdated",
            $"uid={dto.User.UID}",
            $"paused={pausedState}",
            "peers=1",
            "groups=0",
            "broadcast: SelfPairPermissions + OtherPairPermissions",
            _broadcastPresenceOnPermissionChange ? "presence-broadcast=legacy-enabled" : "no offline/online broadcast"));
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetProfile(UserProfileDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) throw new HubException("Cannot modify profile data for anyone but yourself");

        var existingHrpData = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID).ConfigureAwait(false);

        if (existingHrpData?.FlaggedForReport ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile is currently flagged for report and cannot be edited").ConfigureAwait(false);
            return;
        }

        if (existingHrpData?.ProfileDisabled ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile was permanently disabled and cannot be edited").ConfigureAwait(false);
            return;
        }

        // --- Handle HRP Profile ---
        if (existingHrpData != null)
        {
            if (string.Equals("", dto.ProfilePictureBase64, StringComparison.OrdinalIgnoreCase))
            {
                existingHrpData.Base64ProfileImage = null;
            }
            else if (dto.ProfilePictureBase64 != null)
            {
                existingHrpData.Base64ProfileImage = dto.ProfilePictureBase64;
            }

            if (dto.IsNSFW != null)
            {
                existingHrpData.IsNSFW = dto.IsNSFW.Value;
            }

            if (dto.Description != null)
            {
                existingHrpData.UserDescription = dto.Description;
            }
        }
        else
        {
            UserProfileData userProfileData = new()
            {
                UserUID = dto.User.UID,
                Base64ProfileImage = dto.ProfilePictureBase64 ?? null,
                UserDescription = dto.Description ?? null,
                IsNSFW = dto.IsNSFW ?? false,
            };
            await DbContext.UserProfileData.AddAsync(userProfileData).ConfigureAwait(false);
        }

        // --- Handle RP Profile (Character specific) ---
        if (!string.IsNullOrEmpty(dto.CharacterName) && dto.WorldId.HasValue)
        {
            var existingRpData = await DbContext.CharacterRpProfiles.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID && u.CharacterName == dto.CharacterName && u.WorldId == dto.WorldId).ConfigureAwait(false);
            if (existingRpData != null)
            {
                if (dto.RpDescription != null) existingRpData.RpDescription = dto.RpDescription;
                if (dto.RpProfilePictureBase64 != null) existingRpData.RpProfilePictureBase64 = dto.RpProfilePictureBase64;
                if (dto.IsRpNSFW != null) existingRpData.IsRpNSFW = dto.IsRpNSFW.Value;
                if (dto.RpFirstName != null) existingRpData.RpFirstName = dto.RpFirstName;
                if (dto.RpLastName != null) existingRpData.RpLastName = dto.RpLastName;
                if (dto.RpTitle != null) existingRpData.RpTitle = dto.RpTitle;
                if (dto.RpAge != null) existingRpData.RpAge = dto.RpAge;
                if (dto.RpRace != null) existingRpData.RpRace = dto.RpRace;
                if (dto.RpEthnicity != null) existingRpData.RpEthnicity = dto.RpEthnicity;
                if (dto.RpHeight != null) existingRpData.RpHeight = dto.RpHeight;
                if (dto.RpBuild != null) existingRpData.RpBuild = dto.RpBuild;
                if (dto.RpResidence != null) existingRpData.RpResidence = dto.RpResidence;
                if (dto.RpOccupation != null) existingRpData.RpOccupation = dto.RpOccupation;
                if (dto.RpAffiliation != null) existingRpData.RpAffiliation = dto.RpAffiliation;
                if (dto.RpAlignment != null) existingRpData.RpAlignment = dto.RpAlignment;
                if (dto.RpAdditionalInfo != null) existingRpData.RpAdditionalInfo = dto.RpAdditionalInfo;
                if (dto.RpNameColor != null) existingRpData.RpNameColor = dto.RpNameColor;
                if (dto.RpCustomFields != null) existingRpData.RpCustomFields = dto.RpCustomFields;
            }
            else
            {
                CharacterRpProfileData rpProfileData = new()
                {
                    UserUID = dto.User.UID,
                    CharacterName = dto.CharacterName,
                    WorldId = dto.WorldId.Value,
                    RpProfilePictureBase64 = dto.RpProfilePictureBase64 ?? null,
                    RpDescription = dto.RpDescription ?? null,
                    IsRpNSFW = dto.IsRpNSFW ?? false,
                    RpFirstName = dto.RpFirstName ?? null,
                    RpLastName = dto.RpLastName ?? null,
                    RpTitle = dto.RpTitle ?? null,
                    RpAge = dto.RpAge ?? null,
                    RpRace = dto.RpRace ?? null,
                    RpEthnicity = dto.RpEthnicity ?? null,
                    RpHeight = dto.RpHeight ?? null,
                    RpBuild = dto.RpBuild ?? null,
                    RpResidence = dto.RpResidence ?? null,
                    RpOccupation = dto.RpOccupation ?? null,
                    RpAffiliation = dto.RpAffiliation ?? null,
                    RpAlignment = dto.RpAlignment ?? null,
                    RpAdditionalInfo = dto.RpAdditionalInfo ?? null,
                    RpNameColor = dto.RpNameColor ?? null,
                    RpCustomFields = dto.RpCustomFields ?? null
                };
                await DbContext.CharacterRpProfiles.AddAsync(rpProfileData).ConfigureAwait(false);
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await Clients.Users(pairs.Select(p => p.Key)).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
        await Clients.Caller.Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^([a-z0-9_ '+&,\.\-\{\}]+\/)+([a-z0-9_ '+&,\.\-\{\}]+\.[a-z]{3,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex GamePathRegex();

    [GeneratedRegex(@"^[A-Z0-9]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex HashRegex();

    [GeneratedRegex("^[-a-zA-Z0-9@:%._\\+~#=]{1,256}[\\.,][a-zA-Z0-9()]{1,6}\\b(?:[-a-zA-Z0-9()@:%_\\+.~#?&\\/=]*)$")]
    private static partial Regex UrlRegex();

    private ClientPair OppositeEntry(string otherUID) =>
                                    DbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == UserUID);
}