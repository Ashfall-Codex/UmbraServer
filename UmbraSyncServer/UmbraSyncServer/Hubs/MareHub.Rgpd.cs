using UmbraSync.API.Dto.Rgpd;
using MareSynchronosServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
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
            })
            .ToListAsync().ConfigureAwait(false);

        var charaDataCount = await DbContext.CharaData.CountAsync(c => c.UploaderUID == UserUID).ConfigureAwait(false);
        var mcdfShareCount = await DbContext.McdfShares.CountAsync(s => s.OwnerUID == UserUID).ConfigureAwait(false);
        var housingShareCount = await DbContext.HousingShares.CountAsync(h => h.OwnerUID == UserUID).ConfigureAwait(false);
        var uploadedFileCount = await DbContext.Files.CountAsync(f => f.UploaderUID == UserUID).ConfigureAwait(false);
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
            RpProfileCount = rpProfiles.Count,
            RpProfiles = rpProfiles,
            CharaDataCount = charaDataCount,
            McdfShareCount = mcdfShareCount,
            HousingShareCount = housingShareCount,
            UploadedFileCount = uploadedFileCount,
            HasLodestoneAuth = hasLodestone,
            SecondaryAccountCount = secondaryCount,
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
