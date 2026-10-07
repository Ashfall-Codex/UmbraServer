using UmbraSync.API.Dto.Rgpd;
using MareSynchronosServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const string EncryptedContentNotice =
        "Le contenu des partages (MCDF, agencements de logement, scénarios PNJ) est stocké sous forme chiffrée. "
        + "Cet export n'en fournit que les métadonnées ; le contenu lui-même reste disponible depuis votre client, qui en est la source.";

    private const string TechnicalLogsNotice =
        "Les journaux techniques du serveur (adresse IP, identifiant de compte, date et heure de connexion) sont conservés 30 jours "
        + "à des fins de sécurité et ne figurent pas dans cet export.";

    private const string ExternalServicesNotice =
        "Ashfall Connect est un service distinct de ce serveur. La fiche RP enrichie et l'identité Discord ou XIVAuth que vous y avez associées "
        + "ne figurent pas dans cet export et doivent être demandées séparément depuis votre compte Connect.";

    [Authorize(Policy = "Identified")]
    public async Task<RgpdDataExportDto> UserRgpdExportData()
    {
        _logger.LogCallInfo();

        var user = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        var pairUIDs = await DbContext.ClientPairs
            .Where(p => p.UserUID == UserUID)
            .Select(p => p.OtherUserUID)
            .ToListAsync().ConfigureAwait(false);

        var groupGIDs = await DbContext.GroupPairs
            .Where(g => g.GroupUserUID == UserUID)
            .Select(g => g.GroupGID)
            .ToListAsync().ConfigureAwait(false);

        var profile = await DbContext.UserProfileData.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == UserUID).ConfigureAwait(false);

        var rpProfiles = (await GetRgpdRpProfiles([UserUID]).ConfigureAwait(false))
            .Select(r => r.Profile)
            .ToList();

        // Les établissements sont matérialisés avant projection : les colonnes text[] (Languages,
        // Tags) ne se projettent pas de façon fiable côté SQL. Le volume est celui d'un seul compte.
        var establishmentEntities = await DbContext.Establishments.AsNoTracking()
            .Include(e => e.Events)
            .Where(e => e.OwnerUID == UserUID)
            .ToListAsync().ConfigureAwait(false);

        var establishments = establishmentEntities
            .Select(e => new RgpdEstablishmentDto
            {
                Id = e.Id,
                Name = e.Name,
                Description = e.Description,
                Category = (int)e.Category,
                Languages = e.Languages.ToList(),
                Tags = e.Tags.ToList(),
                FactionTag = e.FactionTag,
                Schedule = e.Schedule,
                IsPublic = e.IsPublic,
                CreatedUtc = e.CreatedUtc,
                UpdatedUtc = e.UpdatedUtc,
                LocationType = (int)e.LocationType,
                TerritoryId = e.TerritoryId,
                ServerId = e.ServerId,
                WardId = e.WardId,
                PlotId = e.PlotId,
                DivisionId = e.DivisionId,
                RoomId = e.RoomId,
                IsApartment = e.IsApartment,
                LogoImageBase64 = e.LogoImageBase64,
                BannerImageBase64 = e.BannerImageBase64,
                ManagerRpProfileId = e.ManagerRpProfileId,
                X = e.X,
                Y = e.Y,
                Z = e.Z,
                Radius = e.Radius,
                Events = e.Events.Select(ev => new RgpdEstablishmentEventDto
                {
                    Id = ev.Id,
                    Title = ev.Title,
                    Description = ev.Description,
                    StartsAtUtc = ev.StartsAtUtc,
                    EndsAtUtc = ev.EndsAtUtc,
                    Recurrence = ev.Recurrence,
                    CreatedUtc = ev.CreatedUtc,
                }).ToList(),
            })
            .ToList();

        var wildRp = await DbContext.WildRpAnnouncements.AsNoTracking()
            .Where(a => a.UserUID == UserUID)
            .Select(a => new RgpdWildRpAnnouncementDto
            {
                Id = a.Id,
                CharacterName = a.CharacterName,
                WorldId = a.WorldId,
                TerritoryId = a.TerritoryId,
                WardId = a.WardId,
                Message = a.Message,
                RpProfileId = a.RpProfileId,
                CreatedAtUtc = a.CreatedAtUtc,
                ExpiresAtUtc = a.ExpiresAtUtc,
            })
            .ToListAsync().ConfigureAwait(false);

        var mcdfShares = await DbContext.McdfShares.AsNoTracking()
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .Select(s => new RgpdShareSummaryDto
            {
                Id = s.Id,
                Description = s.Description,
                CreatedUtc = s.CreatedUtc,
                UpdatedUtc = s.UpdatedUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                DownloadCount = s.DownloadCount,
                AllowedUIDs = s.AllowedIndividuals.Select(a => a.AllowedIndividualUid).ToList(),
                AllowedGIDs = s.AllowedSyncshells.Select(a => a.AllowedGroupGid).ToList(),
                EncryptedPayloadSizeBytes = s.CipherData.Length,
            })
            .ToListAsync().ConfigureAwait(false);

        var housingShares = await DbContext.HousingShares.AsNoTracking()
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .Select(s => new RgpdShareSummaryDto
            {
                Id = s.Id,
                Description = s.Description,
                CreatedUtc = s.CreatedUtc,
                UpdatedUtc = s.UpdatedUtc,
                AllowedUIDs = s.AllowedIndividuals.Select(a => a.AllowedIndividualUid).ToList(),
                AllowedGIDs = s.AllowedSyncshells.Select(a => a.AllowedGroupGid).ToList(),
                EncryptedPayloadSizeBytes = s.CipherData.Length,
                ServerId = s.ServerId,
                TerritoryId = s.TerritoryId,
                WardId = s.WardId,
                HouseId = s.HouseId,
                RoomId = s.RoomId,
            })
            .ToListAsync().ConfigureAwait(false);

        var housingScenarios = await DbContext.HousingScenarios.AsNoTracking()
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .Select(s => new RgpdShareSummaryDto
            {
                Id = s.Id,
                Description = s.Description,
                CreatedUtc = s.CreatedUtc,
                UpdatedUtc = s.UpdatedUtc,
                AllowedUIDs = s.AllowedIndividuals.Select(a => a.AllowedIndividualUid).ToList(),
                AllowedGIDs = s.AllowedSyncshells.Select(a => a.AllowedGroupGid).ToList(),
                EncryptedPayloadSizeBytes = s.CipherData.Length,
                ServerId = s.ServerId,
                TerritoryId = s.TerritoryId,
                WardId = s.WardId,
                HouseId = s.HouseId,
                RoomId = s.RoomId,
            })
            .ToListAsync().ConfigureAwait(false);

        var charaData = await DbContext.CharaData.AsNoTracking()
            .Where(c => c.UploaderUID == UserUID)
            .Select(c => new RgpdCharaDataSummaryDto
            {
                Id = c.Id,
                Description = c.Description,
                CreatedDate = c.CreatedDate,
                UpdatedDate = c.UpdatedDate,
                ExpiryDate = c.ExpiryDate,
                DownloadCount = c.DownloadCount,
                FileCount = c.Files.Count,
                PoseCount = c.Poses.Count,
                AllowedUIDs = c.AllowedIndividiuals.Where(a => a.AllowedUserUID != null).Select(a => a.AllowedUserUID!).ToList(),
                AllowedGIDs = c.AllowedIndividiuals.Where(a => a.AllowedGroupGID != null).Select(a => a.AllowedGroupGID!).ToList(),
            })
            .ToListAsync().ConfigureAwait(false);

        var uploadedFiles = await DbContext.Files.AsNoTracking()
            .Where(f => f.UploaderUID == UserUID)
            .Select(f => new RgpdUploadedFileDto
            {
                Hash = f.Hash,
                Size = f.Size,
                UploadDate = f.UploadDate,
            })
            .ToListAsync().ConfigureAwait(false);

        var lodestone = await DbContext.LodeStoneAuth.AsNoTracking()
            .FirstOrDefaultAsync(l => l.User.UID == UserUID).ConfigureAwait(false);

        var export = new RgpdDataExportDto
        {
            UID = user.UID,
            Alias = user.Alias,
            LastLoggedIn = user.LastLoggedIn,
            ExportDate = DateTime.UtcNow,
            PairCount = pairUIDs.Count,
            PairedUIDs = pairUIDs,
            GroupCount = groupGIDs.Count,
            GroupGIDs = groupGIDs,
            HasProfile = profile != null,
            ProfileDescription = profile?.UserDescription,
            ProfileImageBase64 = profile?.Base64ProfileImage,
            ProfileIsNsfw = profile?.IsNSFW ?? false,
            ProfileDisabled = profile?.ProfileDisabled ?? false,
            RpProfileCount = rpProfiles.Count,
            RpProfiles = rpProfiles,
            CharaDataCount = charaData.Count,
            CharaData = charaData,
            McdfShareCount = mcdfShares.Count,
            McdfShares = mcdfShares,
            HousingShareCount = housingShares.Count,
            HousingShares = housingShares,
            HousingScenarioCount = housingScenarios.Count,
            HousingScenarios = housingScenarios,
            EstablishmentCount = establishments.Count,
            Establishments = establishments,
            WildRpAnnouncementCount = wildRp.Count,
            WildRpAnnouncements = wildRp,
            UploadedFileCount = uploadedFiles.Count,
            UploadedFiles = uploadedFiles,
            HasLodestoneAuth = lodestone != null,
            EncryptedContentNotice = EncryptedContentNotice,
            ExternalServicesNotice = ExternalServicesNotice,
            LinkedDiscordId = lodestone?.DiscordId,
            TechnicalLogsNotice = TechnicalLogsNotice,
        };

        await AddRgpdAccountRelations(export).ConfigureAwait(false);
        await AddRgpdSecondaryAccounts(export).ConfigureAwait(false);
        await AddRgpdGroupBans(export).ConfigureAwait(false);
        await AddRgpdPermissions(export).ConfigureAwait(false);
        await AddRgpdScenarioAccessAndReports(export).ConfigureAwait(false);

        return export;
    }

    private sealed record RgpdRpProfileRow(string UserUID, RgpdRpProfileSummaryDto Profile);

    private async Task<List<RgpdRpProfileRow>> GetRgpdRpProfiles(List<string> uids)
    {
        // Projection en mémoire : le volume est celui d'un seul compte et de ses comptes secondaires.
        var profiles = await DbContext.CharacterRpProfiles.AsNoTracking()
            .Where(r => uids.Contains(r.UserUID))
            .ToListAsync().ConfigureAwait(false);

        return profiles
            .Select(r => new RgpdRpProfileRow(r.UserUID, new RgpdRpProfileSummaryDto
            {
                CharacterName = r.CharacterName,
                WorldId = r.WorldId,
                RpFirstName = r.RpFirstName,
                RpLastName = r.RpLastName,
                RpTitle = r.RpTitle,
                RpDescription = r.RpDescription,
                RpProfilePictureBase64 = r.RpProfilePictureBase64,
                RpAge = r.RpAge,
                RpRace = r.RpRace,
                RpEthnicity = r.RpEthnicity,
                RpHeight = r.RpHeight,
                RpBuild = r.RpBuild,
                RpResidence = r.RpResidence,
                RpOccupation = r.RpOccupation,
                RpAffiliation = r.RpAffiliation,
                RpAlignment = r.RpAlignment,
                RpAdditionalInfo = r.RpAdditionalInfo,
                RpNameColor = r.RpNameColor,
                RpCustomFields = r.RpCustomFields,
                MoodlesData = r.MoodlesData,
                EnrichedProfileJson = r.EnrichedProfileJson,
                EnrichedProfileVisibility = r.EnrichedProfileVisibility,
                IsRpNSFW = r.IsRpNSFW,
                ChatIcon = r.ChatIcon,
                RpLevel = r.RpLevel,
            }))
            .ToList();
    }

    private async Task AddRgpdAccountRelations(RgpdDataExportDto export)
    {
        export.IsBanned = await DbContext.Auth.AsNoTracking()
            .AnyAsync(a => a.UserUID == UserUID && a.IsBanned).ConfigureAwait(false);

        export.IncomingPairUIDs = await DbContext.ClientPairs.AsNoTracking()
            .Where(p => p.OtherUserUID == UserUID)
            .Select(p => p.UserUID)
            .ToListAsync().ConfigureAwait(false);

        export.OwnedGroupGIDs = await DbContext.Groups.AsNoTracking()
            .Where(g => g.OwnerUID == UserUID)
            .Select(g => g.GID)
            .ToListAsync().ConfigureAwait(false);
    }

    private async Task AddRgpdSecondaryAccounts(RgpdDataExportDto export)
    {
        var secondaryAuths = await DbContext.Auth.AsNoTracking()
            .Include(a => a.User)
            .Where(a => a.PrimaryUserUID == UserUID)
            .ToListAsync().ConfigureAwait(false);

        var secondaryUIDs = secondaryAuths.Select(a => a.UserUID).Distinct(StringComparer.Ordinal).ToList();
        var secondaryRpProfiles = secondaryUIDs.Count == 0
            ? []
            : await GetRgpdRpProfiles(secondaryUIDs).ConfigureAwait(false);

        export.SecondaryAccountCount = secondaryAuths.Count;
        export.SecondaryAccountUIDs = secondaryUIDs;
        export.SecondaryAccounts = secondaryAuths
            .Select(a => new RgpdSecondaryAccountDto
            {
                UID = a.UserUID,
                Alias = a.User?.Alias,
                LastLoggedIn = a.User?.LastLoggedIn ?? default,
                IsBanned = a.IsBanned,
                RpProfiles = secondaryRpProfiles
                    .Where(r => string.Equals(r.UserUID, a.UserUID, StringComparison.Ordinal))
                    .Select(r => r.Profile)
                    .ToList(),
            })
            .ToList();
    }

    private async Task AddRgpdGroupBans(RgpdDataExportDto export)
    {
        export.GroupBansReceived = await DbContext.GroupBans.AsNoTracking()
            .Where(b => b.BannedUserUID == UserUID)
            .Select(b => new RgpdGroupBanDto
            {
                GID = b.GroupGID,
                BannedOn = b.BannedOn,
                Reason = b.BannedReason,
                OtherUID = b.BannedByUID,
            })
            .ToListAsync().ConfigureAwait(false);

        export.GroupBansIssued = await DbContext.GroupBans.AsNoTracking()
            .Where(b => b.BannedByUID == UserUID)
            .Select(b => new RgpdGroupBanDto
            {
                GID = b.GroupGID,
                BannedOn = b.BannedOn,
                Reason = b.BannedReason,
                OtherUID = b.BannedUserUID,
            })
            .ToListAsync().ConfigureAwait(false);
    }

    private async Task AddRgpdPermissions(RgpdDataExportDto export)
    {
        export.PairPermissions = await DbContext.Permissions.AsNoTracking()
            .Where(p => p.UserUID == UserUID)
            .Select(p => new RgpdPairPermissionDto
            {
                OtherUID = p.OtherUserUID,
                Sticky = p.Sticky,
                IsPaused = p.IsPaused,
                DisableAnimations = p.DisableAnimations,
                DisableVFX = p.DisableVFX,
                DisableSounds = p.DisableSounds,
            })
            .ToListAsync().ConfigureAwait(false);

        export.GroupPermissions = await DbContext.GroupPairPreferredPermissions.AsNoTracking()
            .Where(p => p.UserUID == UserUID)
            .Select(p => new RgpdGroupPermissionDto
            {
                GID = p.GroupGID,
                IsPaused = p.IsPaused,
                DisableAnimations = p.DisableAnimations,
                DisableSounds = p.DisableSounds,
                DisableVFX = p.DisableVFX,
            })
            .ToListAsync().ConfigureAwait(false);

        export.DefaultPermissions = await DbContext.UserDefaultPreferredPermissions.AsNoTracking()
            .Where(p => p.UserUID == UserUID)
            .Select(p => new RgpdDefaultPermissionsDto
            {
                DisableIndividualAnimations = p.DisableIndividualAnimations,
                DisableIndividualSounds = p.DisableIndividualSounds,
                DisableIndividualVFX = p.DisableIndividualVFX,
                DisableGroupAnimations = p.DisableGroupAnimations,
                DisableGroupSounds = p.DisableGroupSounds,
                DisableGroupVFX = p.DisableGroupVFX,
                IndividualIsSticky = p.IndividualIsSticky,
            })
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task AddRgpdScenarioAccessAndReports(RgpdDataExportDto export)
    {
        // Les listes d'accès stockent les identifiants normalisés en majuscules.
        var upperUid = UserUID.ToUpperInvariant();

        export.EditableScenarioIds = await DbContext.HousingScenarioAllowedEditors.AsNoTracking()
            .Where(e => e.EditorUid == UserUID || e.EditorUid == upperUid)
            .Select(e => e.ShareId)
            .Distinct()
            .ToListAsync().ConfigureAwait(false);

        export.InvitedScenarioIds = await DbContext.HousingScenarioAllowedUsers.AsNoTracking()
            .Where(a => a.AllowedIndividualUid == UserUID || a.AllowedIndividualUid == upperUid)
            .Select(a => a.ShareId)
            .Distinct()
            .ToListAsync().ConfigureAwait(false);

        export.ProfileReportsIssued = await DbContext.UserProfileReports.AsNoTracking()
            .Where(r => r.ReportingUserUID == UserUID)
            .Select(r => new RgpdProfileReportDto
            {
                ReportDate = r.ReportDate,
                ReportReason = r.ReportReason,
            })
            .ToListAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRgpdDeleteAllData()
    {
        _logger.LogCallInfo();

        // This performs a full RGPD-compliant deletion (Art. 17 - Right to erasure)
        // It reuses the existing UserDelete flow which now cascades all user data
        var userEntry = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var secondaryUsers = await DbContext.Auth.Include(u => u.User)
            .Where(u => u.PrimaryUserUID == UserUID)
            .Select(c => c.User)
            .ToListAsync().ConfigureAwait(false);

        foreach (var user in secondaryUsers)
        {
            await DeleteUser(user).ConfigureAwait(false);
        }

        await DeleteUser(userEntry).ConfigureAwait(false);
    }
}