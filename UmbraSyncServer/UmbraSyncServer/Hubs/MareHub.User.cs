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
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private static readonly string[] AllowedExtensionsForGamePaths = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pushIntegrityChecked = new(StringComparer.Ordinal);
    private static readonly TimeSpan PushIntegrityTtl = TimeSpan.FromMinutes(30);

    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (dto?.User == null || string.IsNullOrWhiteSpace(dto.User.UID)) return;
        var uid = dto.User.UID.Trim();
        if (string.Equals(uid, UserUID, StringComparison.Ordinal)) return;

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.Database, "pairadd", UserUID, PairAddMaxPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
        {
            _logger.LogCallWarning(MareHubLogger.Args(dto, "RateLimited"));
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "Trop de demandes d'appairage en peu de temps, réessaie dans une minute.").ConfigureAwait(false);
            return;
        }

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
            // Idempotence : on resynchronise l'état au caller au lieu de renvoyer un warning bloquant.
            var oppositeEntry = OppositeEntry(otherUser.UID);
            var ownPermsResync = await DbContext.Permissions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == otherUser.UID).ConfigureAwait(false);
            var otherPermsResync = oppositeEntry == null ? null : await DbContext.Permissions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.UserUID == otherUser.UID && p.OtherUserUID == UserUID).ConfigureAwait(false);
            var ownPermResync = ownPermsResync.ToUserPermissions(setSticky: true);
            var otherPermResync = otherPermsResync.ToUserPermissions();
            var resyncStatus = oppositeEntry != null ? IndividualPairStatus.Bidirectional : IndividualPairStatus.OneSided;
            await Clients.Caller.Client_UserAddClientPair(new UserPairDto(otherUser.ToUserData(), resyncStatus, ownPermResync, otherPermResync)).ConfigureAwait(false);
            _logger.LogCallInfo(MareHubLogger.Args(dto, "AlreadyPaired-Resync"));
            return;
        }

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var blocked = await UserBlockQueries.IsBlockedEitherWayAsync(DbContext, UserUID, otherUser.UID).ConfigureAwait(false);
        var permissions = await AddPairEntryAsync(user, otherUser).ConfigureAwait(false);

        // L'autre joueur nous avait envoyé une demande : notre ajout vaut acceptation, on crée aussi
        // son côté de la paire au lieu de lui renvoyer une demande à accepter.
        UserPermissionSet requesterPermissions = null;
        if (!blocked
            && OppositeEntry(otherUser.UID) == null
            && await PairRequestRedis.ConsumeAsync(_redis.Database, otherUser.UID, UserUID).ConfigureAwait(false))
        {
            requesterPermissions = await AddPairEntryAsync(otherUser, user).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var otherEntry = OppositeEntry(otherUser.UID);
        var otherIdent = await GetUserIdent(otherUser.UID).ConfigureAwait(false);

        var otherPermsEntry = otherEntry == null ? null : await DbContext.Permissions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == otherUser.UID && p.OtherUserUID == UserUID).ConfigureAwait(false);

        var ownPerm = permissions.ToUserPermissions(setSticky: true);
        var otherPerm = otherPermsEntry.ToUserPermissions();

        var status = otherEntry == null ? IndividualPairStatus.OneSided : IndividualPairStatus.Bidirectional;
        var userPairResponse = new UserPairDto(otherUser.ToUserData(), status, ownPerm, otherPerm);
        await Clients.User(user.UID).Client_UserAddClientPair(userPairResponse).ConfigureAwait(false);

        if (requesterPermissions != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(dto, "PairRequestCompleted"));
            await Clients.User(otherUser.UID).Client_UserAddClientPair(new UserPairDto(user.ToUserData(), IndividualPairStatus.Bidirectional,
                requesterPermissions.ToUserPermissions(setSticky: true), permissions.ToUserPermissions())).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_PairRequestAccepted(new UserDto(user.ToUserData())).ConfigureAwait(false);
        }
        else if (otherEntry != null)
        {
            // Paire par UID : l'autre nous avait ajouté et attendait notre réponse
            if (await PairRequestRedis.ConsumeAsync(_redis.Database, otherUser.UID, UserUID).ConfigureAwait(false))
                await Clients.User(otherUser.UID).Client_PairRequestAccepted(new UserDto(user.ToUserData())).ConfigureAwait(false);
        }
        else if (!blocked)
        {
            await PairRequestRedis.RegisterAsync(_redis.Database, UserUID, otherUser.UID).ConfigureAwait(false);
            if (otherIdent != null)
                await Clients.User(otherUser.UID).Client_ReceivePairRequest(new UserDto(user.ToUserData())).ConfigureAwait(false);
        }

        if (otherIdent == null || otherEntry == null) return;

        if (requesterPermissions == null)
        {
            // Pair devient bidirectionnel : on prévient les deux côtés du nouveau statut.
            await Clients.User(otherUser.UID)
                .Client_UpdateUserIndividualPairStatusDto(new UserIndividualPairStatusDto(user.ToUserData(), IndividualPairStatus.Bidirectional)).ConfigureAwait(false);

            await Clients.User(otherUser.UID)
                .Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(user.ToUserData(), permissions.ToUserPermissions())).ConfigureAwait(false);
        }

        if (!ownPerm.IsPaused() && !otherPerm.IsPaused())
        {
            await Clients.User(UserUID).Client_UserSendOnline(new(otherUser.ToUserData(), otherIdent)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserSendOnline(new(user.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
        }
    }

    private const int PairAddMaxPerMinute = 20;
    private const int BlockMaxPerMinute = 30;

    /// <summary>
    /// Ajoute l'entrée <paramref name="owner"/> → <paramref name="other"/> et initialise ses permissions avec les préférences
    /// par défaut de <paramref name="owner"/>, sauf si une entrée existe déjà avec sticky=true (paire re-créée après
    /// suppression : on respecte les anciennes préférences). N'enregistre pas : l'appelant fait SaveChanges.
    /// </summary>
    private async Task<UserPermissionSet> AddPairEntryAsync(User owner, User other)
    {
        await DbContext.ClientPairs.AddAsync(new ClientPair { OtherUser = other, User = owner }).ConfigureAwait(false);

        var existingPerms = await DbContext.Permissions
            .SingleOrDefaultAsync(p => p.UserUID == owner.UID && p.OtherUserUID == other.UID).ConfigureAwait(false);
        if (existingPerms != null && existingPerms.Sticky) return existingPerms;

        var ownDefaultPerms = await DbContext.UserDefaultPreferredPermissions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == owner.UID).ConfigureAwait(false);
        if (existingPerms == null)
        {
            var permissions = new UserPermissionSet
            {
                User = owner,
                OtherUser = other,
                DisableAnimations = ownDefaultPerms?.DisableIndividualAnimations ?? false,
                DisableSounds = ownDefaultPerms?.DisableIndividualSounds ?? false,
                DisableVFX = ownDefaultPerms?.DisableIndividualVFX ?? false,
                IsPaused = false,
                Sticky = true,
            };
            await DbContext.Permissions.AddAsync(permissions).ConfigureAwait(false);
            return permissions;
        }

        existingPerms.DisableAnimations = ownDefaultPerms?.DisableIndividualAnimations ?? false;
        existingPerms.DisableSounds = ownDefaultPerms?.DisableIndividualSounds ?? false;
        existingPerms.DisableVFX = ownDefaultPerms?.DisableIndividualVFX ?? false;
        existingPerms.IsPaused = false;
        existingPerms.Sticky = true;
        DbContext.Permissions.Update(existingPerms);
        return existingPerms;
    }

    [Authorize(Policy = "Identified")]
    public async Task UserBlock(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));
        if (dto?.User == null || string.IsNullOrWhiteSpace(dto.User.UID)) return;
        var target = dto.User.UID.Trim();
        if (string.Equals(target, UserUID, StringComparison.Ordinal)) return;

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.Database, "userblock", UserUID, BlockMaxPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
        {
            _logger.LogCallWarning(MareHubLogger.Args(dto, "RateLimited"));
            return;
        }

        var targetUser = await DbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UID == target).ConfigureAwait(false);
        if (targetUser == null) return;

        // Une demande en attente dans un sens ou dans l'autre n'a plus de raison d'aboutir
        await PairRequestRedis.ClearBetweenAsync(_redis.Database, UserUID, targetUser.UID).ConfigureAwait(false);

        var exists = await DbContext.UserBlocks.AnyAsync(b => b.UserUID == UserUID && b.BlockedUserUID == targetUser.UID).ConfigureAwait(false);
        if (exists) return;

        await DbContext.UserBlocks.AddAsync(new UserBlock
        {
            UserUID = UserUID,
            BlockedUserUID = targetUser.UID,
            CreatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);

        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Double appel concurrent : le blocage existe déjà, c'est le résultat attendu
            _logger.LogCallInfo(MareHubLogger.Args(dto, "AlreadyBlocked"));
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUnblock(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));
        if (dto?.User == null || string.IsNullOrWhiteSpace(dto.User.UID)) return;
        var target = dto.User.UID.Trim();

        var entry = await DbContext.UserBlocks.SingleOrDefaultAsync(b => b.UserUID == UserUID && b.BlockedUserUID == target).ConfigureAwait(false);
        if (entry == null) return;

        DbContext.UserBlocks.Remove(entry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserData>> UserGetBlockedUsers()
    {
        _logger.LogCallInfo();

        var blocked = await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID)
            .Select(b => b.BlockedUser)
            .ToListAsync().ConfigureAwait(false);

        return blocked.Select(u => u.ToUserData()).ToList();
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
    public async Task<List<UserFullPairDto>> UserGetPairedClients()
    {
        _logger.LogCallInfo();

        // Vue unifiée pairs directs + pairs implicites via syncshells (1 helper, pas de N+1).
        var pairs = await GetAllPairInfo(UserUID).ConfigureAwait(false);

        return pairs.Select(p =>
        {
            var info = p.Value;

            // IndividualPairStatus :
            //  - Bidirectional : pair direct ET l'autre a aussi appairé en direct
            //  - OneSided     : pair direct mais miroir manquant
            //  - None         : pair purement via syncshell
            IndividualPairStatus status;
            if (info.IndividuallyPaired)
                status = IndividualPairStatus.Bidirectional;
            else if (info.GIDs.Contains(IndividualPairKey, StringComparer.Ordinal))
                status = IndividualPairStatus.OneSided;
            else
                status = IndividualPairStatus.None;

            var ownPerm = info.OwnPermissions.ToUserPermissions(setSticky: true);
            var otherPerm = info.OtherPermissions.ToUserPermissions();
            // OneSided sortant : l'autre n'a pas appairé en direct. Si une UserPermissionSet miroir
            // existe (rare), ToUserPermissions met Paired=true par défaut, on force Paired=false ici
            // pour préserver le contrat client historique.
            if (status == IndividualPairStatus.OneSided) otherPerm.SetPaired(false);

            // GIDs réseau : on retire la sentinelle interne "Individual", le client n'utilise que les vrais GroupGIDs.
            var gids = info.GIDs.Where(g => !string.Equals(g, IndividualPairKey, StringComparison.Ordinal)).ToList();

            return new UserFullPairDto(new UserData(p.Key, info.Alias), status, gids, ownPerm, otherPerm);
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

        // Validate format: 3-15 chars, letters/digits/_/- only (DB column is varchar(15))
        var norm = trimmed.Normalize(NormalizationForm.FormKC);
        if (norm.Length < 3 || norm.Length > 15 || !Regex.IsMatch(norm, "^[A-Za-z0-9_-]+$"))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Invalid Custom ID. Use 3-15 characters: letters, digits, underscore or hyphen.").ConfigureAwait(false);
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
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            _logger.LogWarning(ex, "DbUpdateException while setting alias '{Alias}' for user {UID}", norm, UserUID);
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, $"Custom ID '{norm}' could not be saved. It may already be taken.").ConfigureAwait(false);
            return;
        }
        await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, $"Your Custom ID is now '{norm}'.").ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<UserProfileDto> UserGetProfile(UserDto user)
    {
        _logger.LogCallInfo(MareHubLogger.Args(user));

        var isSelf = string.Equals(user.User.UID, UserUID, StringComparison.Ordinal);
        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var isPaired = allUserPairs.Contains(user.User.UID, StringComparer.Ordinal);

        CharacterRpProfileData? rpData = null;
        if (!string.IsNullOrEmpty(user.CharacterName) && user.WorldId.HasValue)
        {
            rpData = await DbContext.CharacterRpProfiles.SingleOrDefaultAsync(u => u.UserUID == user.User.UID && u.CharacterName == user.CharacterName && u.WorldId == user.WorldId).ConfigureAwait(false);
        }

        // Le profil HRP reste réservé à soi-même et aux paires ; la visibilité ne gouverne que le profil RP du personnage.
        var hrpAllowed = isSelf || isPaired;
        var isDirectPaired = rpData is { Visibility: RpProfileVisibility.PairsOnly } && isPaired && await IsDirectPairedAsync(user.User.UID).ConfigureAwait(false);
        var rpAllowed = rpData != null && CanViewRpProfile(rpData.Visibility, isSelf, isPaired, isDirectPaired);

        if (!hrpAllowed && !rpAllowed)
        {
            return new UserProfileDto(user.User, false, null, null, "Due to the pause status you cannot access this users profile.");
        }

        var hrpRow = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.User.UID).ConfigureAwait(false);

        // La modération vaut pour tous les accès, même pour un profil RP public vu par un inconnu.
        if (hrpRow?.FlaggedForReport ?? false) return new UserProfileDto(user.User, true, null, null, "This profile is flagged for report and pending evaluation");
        if (hrpRow?.ProfileDisabled ?? false) return new UserProfileDto(user.User, true, null, null, "This profile was permanently disabled");

        var hrpData = hrpAllowed ? hrpRow : null;

        // Le propriétaire connaît toujours son réglage. Un lecteur refusé reçoit le niveau (jamais le contenu) :
        // son client sait ainsi que la fiche n'est plus visible et purge sa copie locale.
        var shownVisibility = isSelf || !rpAllowed ? rpData?.Visibility : null;
        if (!rpAllowed) rpData = null;

        if (hrpData == null && rpData == null && shownVisibility == null) return new UserProfileDto(user.User, false, null, null, null);

        return ToUserProfileDto(user, hrpData, rpData, shownVisibility);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserProfileDto>> UserGetAllCharacterProfiles(UserDto user)
    {
        _logger.LogCallInfo(MareHubLogger.Args(user));

        // Anti-stalk: the caller must provide the CharacterName+WorldId of the character they encountered
        if (string.IsNullOrEmpty(user.CharacterName) || !user.WorldId.HasValue || user.WorldId.Value == 0)
        {
            return [];
        }

        var isSelf = string.Equals(user.User.UID, UserUID, StringComparison.Ordinal);
        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var isPaired = allUserPairs.Contains(user.User.UID, StringComparer.Ordinal);

        // Anti-stalk: verify that the claimed encountered character actually exists in DB for this UID
        var encountered = await DbContext.CharacterRpProfiles
            .SingleOrDefaultAsync(u => u.UserUID == user.User.UID && u.CharacterName == user.CharacterName && u.WorldId == user.WorldId)
            .ConfigureAwait(false);

        if (encountered == null)
        {
            return [];
        }

        var hrpRow = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.User.UID).ConfigureAwait(false);

        if (hrpRow?.FlaggedForReport ?? false) return [];
        if (hrpRow?.ProfileDisabled ?? false) return [];

        List<CharacterRpProfileData> rpProfiles;
        if (isSelf || isPaired)
        {
            rpProfiles = await DbContext.CharacterRpProfiles
                .Where(u => u.UserUID == user.User.UID)
                .ToListAsync()
                .ConfigureAwait(false);
        }
        else
        {
            // Un inconnu ne reçoit que le personnage croisé, jamais les autres personnages du compte (pas de lien entre alts).
            rpProfiles = [encountered];
        }

        var isDirectPaired = isPaired && rpProfiles.Any(p => p.Visibility == RpProfileVisibility.PairsOnly)
            && await IsDirectPairedAsync(user.User.UID).ConfigureAwait(false);
        rpProfiles = rpProfiles.Where(p => CanViewRpProfile(p.Visibility, isSelf, isPaired, isDirectPaired)).ToList();

        var hrpData = isSelf || isPaired ? hrpRow : null;

        if (rpProfiles.Count == 0 && hrpData == null) return [];

        return rpProfiles.Select(rpData => ToUserProfileDto(user, hrpData, rpData, isSelf ? rpData.Visibility : null)).ToList();
    }

    private static UserProfileDto ToUserProfileDto(UserDto user, UserProfileData hrpData, CharacterRpProfileData rpData, RpProfileVisibility? visibility)
    {
        return new UserProfileDto(user.User, false, hrpData?.IsNSFW, hrpData?.Base64ProfileImage, hrpData?.UserDescription,
            rpData?.RpProfilePictureBase64, rpData?.RpDescription, rpData?.IsRpNSFW,
            rpData?.RpFirstName, rpData?.RpLastName, rpData?.RpTitle, rpData?.RpAge,
            rpData?.RpRace, rpData?.RpEthnicity,
            rpData?.RpHeight, rpData?.RpBuild, rpData?.RpResidence, rpData?.RpOccupation, rpData?.RpAffiliation,
            rpData?.RpAlignment, rpData?.RpAdditionalInfo,
            rpData?.RpNameColor,
            rpData?.RpCustomFields,
            rpData?.MoodlesData,
            rpData?.ChatIcon,
            rpData?.RpLevel,
            rpData?.CharacterName, rpData?.WorldId,
            visibility);
    }

    private static bool CanViewRpProfile(RpProfileVisibility visibility, bool isSelf, bool isPaired, bool isDirectPaired)
    {
        if (isSelf) return true;
        return visibility switch
        {
            RpProfileVisibility.Public => true,
            RpProfileVisibility.Hidden => false,
            RpProfileVisibility.PairsOnly => isPaired && isDirectPaired,
            _ => isPaired,
        };
    }

    private async Task<bool> IsDirectPairedAsync(string uid)
    {
        var direct = await GetDirectPairedUnpausedUsers().ConfigureAwait(false);
        return direct.Contains(uid, StringComparer.Ordinal);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        if (dto?.CharaData == null || dto.Recipients == null)
        {
            _logger.LogCallWarning(MareHubLogger.Args("malformed_push", dto?.CharaData == null ? "no_chara_data" : "no_recipients"));
            return;
        }

        dto.CharaData.FileReplacements ??= new();

        var fileReplacementsTotal = dto.CharaData.FileReplacements?.Values.Sum(v => v?.Count ?? 0) ?? 0;
        var glamourerLen = dto.CharaData.GlamourerData?.Values.Sum(v => v?.Length ?? 0) ?? 0;
        var customizeLen = dto.CharaData.CustomizePlusData?.Values.Sum(v => v?.Length ?? 0) ?? 0;
        var manipulationLen = dto.CharaData.ManipulationData?.Length ?? 0;
        _logger.LogCallInfo(MareHubLogger.Args(
            "fileReplacements", fileReplacementsTotal,
            "recipients", dto.Recipients?.Count ?? 0,
            "sizeGlam", glamourerLen,
            "sizeManip", manipulationLen,
            "sizeCust", customizeLen));

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

        // Une entrée invalide est retirée du push au lieu de faire rejeter tout l'envoi :
        // sinon un seul chemin de mod exotique bloque toutes les mises à jour de l'émetteur.
        int removedEntries = 0;
        foreach (var kind in dto.CharaData.FileReplacements.Keys.ToList())
        {
            var replacements = dto.CharaData.FileReplacements[kind];
            if (replacements == null)
            {
                dto.CharaData.FileReplacements[kind] = [];
                continue;
            }

            List<FileReplacementData> kept = new(replacements.Count);
            foreach (var replacement in replacements)
            {
                if (replacement == null) continue;

                var gamePaths = replacement.GamePaths ?? [];
                var invalidPaths = gamePaths.Where(p => p == null || !GamePathRegex().IsMatch(p)
                    || !AllowedExtensionsForGamePaths.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
                replacement.GamePaths = gamePaths.Where(p => !invalidPaths.Contains(p, StringComparer.Ordinal)).ToArray();
                bool validGamePaths = replacement.GamePaths.Length > 0;
                bool validHash = string.IsNullOrEmpty(replacement.Hash) || HashRegex().IsMatch(replacement.Hash);
                bool validFileSwapPath = string.IsNullOrEmpty(replacement.FileSwapPath) || GamePathRegex().IsMatch(replacement.FileSwapPath);

                if (invalidPaths.Count > 0 || !validHash || !validFileSwapPath)
                {
                    _logger.LogCallWarning(MareHubLogger.Args("Invalid Data", "GamePaths", validGamePaths, string.Join(",", invalidPaths), "Hash", validHash, replacement.Hash, "FileSwap", validFileSwapPath, replacement.FileSwapPath));
                }

                if (!validGamePaths || !validHash || !validFileSwapPath)
                {
                    removedEntries++;
                    continue;
                }

                kept.Add(replacement);
            }

            dto.CharaData.FileReplacements[kind] = kept;
        }

        // Un même jeu de données est repoussé à chaque nouveau pair visible : un seul avertissement par version
        if (removedEntries > 0
            && _pushIntegrityChecked.TryAdd(UserUID + ":invalid:" + dto.CharaData.DataHash.Value, DateTime.UtcNow))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning,
                $"{removedEntries} entrée(s) de mod invalide(s) ont été retirées de votre envoi (chemin ou hash non conforme). Le reste de votre apparence a bien été transmis. Consultez /xllog pour le détail.").ConfigureAwait(false);
        }

        try
        {
            var texturesToClassify = new Dictionary<string, Bc7TextureRole>(StringComparer.Ordinal);
            foreach (var replacement in dto.CharaData.FileReplacements.SelectMany(p => p.Value))
            {
                if (string.IsNullOrEmpty(replacement.Hash)) continue;
                var role = Bc7TextureClassifier.Classify(replacement.GamePaths);
                if (role == null) continue;
                if (!texturesToClassify.TryGetValue(replacement.Hash, out var existing) || role.Value == Bc7TextureRole.Normal)
                    texturesToClassify[replacement.Hash] = role.Value;
            }
            if (texturesToClassify.Count > 0)
                _ = QueueBc7ClassificationAsync(texturesToClassify);
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("bc7_classify_failed", ex.Message));
        }

        await WarnOnMissingUploadsAsync(dto).ConfigureAwait(false);

        var recipientUids = dto.Recipients
            .Where(r => r != null && !string.IsNullOrEmpty(r.UID))
            .Select(r => r.UID)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
        
        try
        {
            await UpdateUserOnRedis().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("presence_refresh_failed", ex.Message));
        }

        await Clients.Users(validRecipients).Client_UserReceiveCharacterData(
            new OnlineUserCharaDataDto(new UserData(UserUID), dto.CharaData)).ConfigureAwait(false);

        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, validRecipients.Count);
    }
    
    private async Task WarnOnMissingUploadsAsync(UserCharaDataMessageDto dto)
    {
        try
        {
            var hashes = dto.CharaData.FileReplacements
                .SelectMany(p => p.Value)
                .Select(r => r.Hash)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (hashes.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var stale in _pushIntegrityChecked.Where(kvp => now - kvp.Value > PushIntegrityTtl).Select(kvp => kvp.Key).ToList())
                _pushIntegrityChecked.TryRemove(stale, out _);

            // Le même jeu de données est repoussé à chaque nouveau pair visible : une seule vérification suffit.
            if (!_pushIntegrityChecked.TryAdd(UserUID + ":" + dto.CharaData.DataHash.Value, now)) return;

            var uploadedHashes = await DbContext.Files.AsNoTracking()
                .Where(f => hashes.Contains(f.Hash) && f.Uploaded)
                .Select(f => f.Hash)
                .ToListAsync().ConfigureAwait(false);

            var missing = hashes.Except(uploadedHashes, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0) return;

            _logger.LogCallWarning(MareHubLogger.Args("push_missing_uploads", missing.Count, hashes.Count,
                string.Join(",", missing.Take(10))));

            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning,
                $"{missing.Count} fichier(s) de votre apparence sur {hashes.Count} ne sont pas présents sur le serveur : "
                + "vos partenaires verront des pièces manquantes. Un /usync rescan relancera l'envoi.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("push_integrity_check_failed", ex.Message));
        }
    }

   private async Task QueueBc7ClassificationAsync(Dictionary<string, Bc7TextureRole> textures)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var list = textures.ToList();
            var sb = new StringBuilder("INSERT INTO file_bc7_conversions (source_hash, role, state, updated_at) VALUES ");
            var parameters = new object[list.Count * 2];
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('(').Append('{').Append(i * 2).Append("}, {").Append(i * 2 + 1).Append("}, 0, now())");
                parameters[i * 2] = list[i].Key;
                parameters[i * 2 + 1] = (int)list[i].Value;
            }
            var normalRole = (int)Bc7TextureRole.Normal;
            sb.Append(" ON CONFLICT (source_hash) DO UPDATE SET role = ").Append(normalRole).Append(", updated_at = now()")
              .Append(" WHERE EXCLUDED.role = ").Append(normalRole)
              .Append(" AND file_bc7_conversions.role <> ").Append(normalRole).Append(';');

            await db.Database.ExecuteSqlRawAsync(sb.ToString(), parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("bc7_classify_upsert_failed", ex.Message));
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRemovePair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;

        ClientPair callerPair =
            await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (callerPair == null) return;

        // Permissions caller pour cette pair (avant suppression)
        var ownPerms = await DbContext.Permissions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        bool callerHadPaused = ownPerms?.IsPaused ?? false;

        DbContext.ClientPairs.Remove(callerPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _pairCacheService.InvalidateUsers([UserUID, dto.User.UID]);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.User(UserUID).Client_UserRemoveClientPair(dto).ConfigureAwait(false);

        var oppositeClientPair = OppositeEntry(dto.User.UID);
        if (oppositeClientPair == null) return;

        // Notifie l'autre user de la transition Bidirectional → OneSided
        await Clients.User(dto.User.UID)
            .Client_UpdateUserIndividualPairStatusDto(new UserIndividualPairStatusDto(new UserData(UserUID), IndividualPairStatus.OneSided))
            .ConfigureAwait(false);

        var otherIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        await Clients.User(dto.User.UID)
            .Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID), UserPermissions.NoneSet)).ConfigureAwait(false);

        var otherPerms = await DbContext.Permissions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == dto.User.UID && p.OtherUserUID == UserUID).ConfigureAwait(false);
        bool otherHadPaused = otherPerms?.IsPaused ?? false;
        if (!callerHadPaused && otherHadPaused) return;

        var allUsers = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
        var pauseEntry = allUsers.SingleOrDefault(f => string.Equals(f.UID, dto.User.UID, StringComparison.Ordinal));
        var isPausedInGroup = pauseEntry == null || pauseEntry.IsPausedPerGroup is PauseInfo.Paused or PauseInfo.NoConnection;

        if (!callerHadPaused && !otherHadPaused && !isPausedInGroup) return;

        if (!callerHadPaused && !otherHadPaused && isPausedInGroup)
        {
            await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
            await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
        }

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

        var permsRow = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (permsRow == null)
        {
            permsRow = new UserPermissionSet
            {
                UserUID = UserUID,
                OtherUserUID = dto.User.UID,
                Sticky = true,
            };
            await DbContext.Permissions.AddAsync(permsRow).ConfigureAwait(false);
        }

        var pauseChange = permsRow.IsPaused != dto.Permissions.IsPaused();

        permsRow.IsPaused = dto.Permissions.IsPaused();
        permsRow.DisableAnimations = dto.Permissions.IsDisableAnimations();
        permsRow.DisableSounds = dto.Permissions.IsDisableSounds();
        permsRow.DisableVFX = dto.Permissions.IsDisableVFX();
        permsRow.Sticky = dto.Permissions.IsSticky() || permsRow.Sticky;
        DbContext.Update(permsRow);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (pauseChange)
            _pairCacheService.InvalidateUsers([UserUID, dto.User.UID]);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var otherEntry = OppositeEntry(dto.User.UID);

        // On renvoie au caller un dto enrichi du sticky état
        var ownPerm = dto.Permissions;
        ownPerm.SetSticky(permsRow.Sticky);
        await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(new UserPermissionsDto(dto.User, ownPerm)).ConfigureAwait(false);

        if (otherEntry != null)
        {
            // L'autre user reçoit l'update sans la flag Sticky (c'est privé)
            var otherDtoPerm = dto.Permissions;
            otherDtoPerm.SetSticky(false);
            await Clients.User(dto.User.UID).Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID), otherDtoPerm)).ConfigureAwait(false);

            var otherEntryPerms = await DbContext.Permissions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.UserUID == dto.User.UID && p.OtherUserUID == UserUID).ConfigureAwait(false);

            if (pauseChange && _broadcastPresenceOnPermissionChange)
            {
                var otherCharaIdent = await GetUserIdent(pair.OtherUserUID).ConfigureAwait(false);

                if (UserCharaIdent == null || otherCharaIdent == null || (otherEntryPerms?.IsPaused ?? false)) return;

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
                if (dto.MoodlesData != null) existingRpData.MoodlesData = dto.MoodlesData;
                if (dto.ChatIcon.HasValue) existingRpData.ChatIcon = dto.ChatIcon.Value;
                if (dto.RpLevel.HasValue) existingRpData.RpLevel = dto.RpLevel.Value;
                if (dto.RpVisibility is { } visibility && System.Enum.IsDefined(visibility)) existingRpData.Visibility = visibility;
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
                    RpCustomFields = dto.RpCustomFields ?? null,
                    MoodlesData = dto.MoodlesData ?? null,
                    ChatIcon = dto.ChatIcon ?? 0,
                    RpLevel = dto.RpLevel ?? 0,
                    Visibility = dto.RpVisibility is { } newVisibility && System.Enum.IsDefined(newVisibility) ? newVisibility : RpProfileVisibility.PairsAndSyncshell
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