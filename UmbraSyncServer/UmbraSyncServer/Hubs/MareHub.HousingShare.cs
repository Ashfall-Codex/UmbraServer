using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingShare;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task HousingShareUpload(HousingShareUploadRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var share = await DbContext.HousingShares
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId)
            .ConfigureAwait(false);

        if (share != null && !string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (share == null)
        {
            share = new HousingShare
            {
                Id = dto.ShareId,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.HousingShares.Add(share);
        }

        share.Description = dto.Description ?? string.Empty;
        share.ServerId = dto.Location.ServerId;
        share.MapId = dto.Location.MapId;
        share.TerritoryId = dto.Location.TerritoryId;
        share.DivisionId = dto.Location.DivisionId;
        share.WardId = dto.Location.WardId;
        share.HouseId = dto.Location.HouseId;
        share.RoomId = dto.Location.RoomId;
        share.CipherData = dto.CipherData ?? Array.Empty<byte>();
        share.Nonce = dto.Nonce ?? Array.Empty<byte>();
        share.Salt = dto.Salt ?? Array.Empty<byte>();
        share.Tag = dto.Tag ?? Array.Empty<byte>();
        share.UpdatedUtc = now;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<HousingSharePayloadDto?> HousingShareDownload(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.HousingShares.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == shareId)
            .ConfigureAwait(false);

        if (share == null) return null;

        return new HousingSharePayloadDto
        {
            ShareId = share.Id,
            CipherData = share.CipherData,
            Nonce = share.Nonce,
            Salt = share.Salt,
            Tag = share.Tag,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<HousingShareEntryDto>> HousingShareGetOwn()
    {
        _logger.LogCallInfo();

        var shares = await DbContext.HousingShares.AsNoTracking()
            .Include(s => s.Owner)
            .Where(s => s.OwnerUID == UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapHousingShareEntryDto(s, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<HousingShareEntryDto>> HousingShareGetForLocation(LocationInfo location)
    {
        _logger.LogCallInfo(MareHubLogger.Args(location.ServerId, location.TerritoryId, location.WardId, location.HouseId));

        var shares = await DbContext.HousingShares.AsNoTracking()
            .Include(s => s.Owner)
            .Where(s => s.ServerId == location.ServerId
                && s.TerritoryId == location.TerritoryId
                && s.DivisionId == location.DivisionId
                && s.WardId == location.WardId
                && s.HouseId == location.HouseId)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapHousingShareEntryDto(s, string.Equals(s.OwnerUID, UserUID, StringComparison.Ordinal))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> HousingShareDelete(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.HousingShares.SingleOrDefaultAsync(s => s.Id == shareId && s.OwnerUID == UserUID).ConfigureAwait(false);
        if (share == null) return false;

        DbContext.HousingShares.Remove(share);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static HousingShareEntryDto MapHousingShareEntryDto(HousingShare share, bool isOwner)
    {
        return new HousingShareEntryDto
        {
            Id = share.Id,
            Location = new LocationInfo
            {
                ServerId = share.ServerId,
                MapId = share.MapId,
                TerritoryId = share.TerritoryId,
                DivisionId = share.DivisionId,
                WardId = share.WardId,
                HouseId = share.HouseId,
                RoomId = share.RoomId,
            },
            Description = share.Description,
            CreatedUtc = share.CreatedUtc,
            UpdatedUtc = share.UpdatedUtc,
            IsOwner = isOwner,
            OwnerUid = share.OwnerUID,
            OwnerAlias = share.Owner?.Alias ?? string.Empty,
        };
    }
}
