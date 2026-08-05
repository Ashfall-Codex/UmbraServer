using UmbraSync.API.Dto.Rgpd;
using MareSynchronosServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const string EncryptedContentNotice =
        "Le contenu des partages (MCDF, agencements de logement, scénarios PNJ) est chiffré par votre client avant l'envoi. "
        + "Le serveur ne détient pas la clé de déchiffrement et ne peut donc exporter que les métadonnées de ces partages.";

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

        var rpProfiles = await DbContext.CharacterRpProfiles.AsNoTracking()
            .Where(r => r.UserUID == UserUID)
            .Select(r => new RgpdRpProfileSummaryDto
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
            })
            .ToListAsync().ConfigureAwait(false);

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

        var hasLodestone = await DbContext.LodeStoneAuth.AnyAsync(l => l.User.UID == UserUID).ConfigureAwait(false);
        var secondaryCount = await DbContext.Auth.CountAsync(a => a.PrimaryUserUID == UserUID).ConfigureAwait(false);

        return new RgpdDataExportDto
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
            HasLodestoneAuth = hasLodestone,
            SecondaryAccountCount = secondaryCount,
            EncryptedContentNotice = EncryptedContentNotice,
            ExternalServicesNotice = ExternalServicesNotice,
        };
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