using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UmbraSync.API.Dto.StageShare;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const int StageShareMaxCipherBytes = 16 * 1024 * 1024;
    private const int StageShareMaxNameLength = 128;
    private const int StageShareMaxDescriptionLength = 1024;

    [Authorize(Policy = "Identified")]
    public async Task StageShareUpload(StageShareUploadRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        if (dto.CipherData == null || dto.CipherData.Length == 0 || dto.CipherData.Length > StageShareMaxCipherBytes
            || dto.Nonce == null || dto.Salt == null || dto.Tag == null)
        {
            return;
        }

        var normalizedUsers = (dto.AllowedIndividuals ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = (dto.AllowedSyncshells ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var share = await DbContext.StageShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId)
            .ConfigureAwait(false);

        if (share != null && !string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (share == null)
        {
            share = new StageShare
            {
                Id = dto.ShareId,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.StageShares.Add(share);
        }

        share.StageName = Truncate(dto.StageName ?? string.Empty, StageShareMaxNameLength);
        share.Description = Truncate(dto.Description ?? string.Empty, StageShareMaxDescriptionLength);
        share.StageFormatVersion = dto.StageFormatVersion;
        share.WorldId = dto.Location.WorldId;
        share.TerritoryId = dto.Location.TerritoryId;
        share.WardId = dto.Location.WardId;
        share.DivisionId = dto.Location.DivisionId;
        share.HouseId = dto.Location.HouseId;
        share.RoomId = dto.Location.RoomId;
        share.CipherData = dto.CipherData;
        share.Nonce = dto.Nonce;
        share.Salt = dto.Salt;
        share.Tag = dto.Tag;
        share.UpdatedUtc = now;

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new StageShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new StageShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<StageSharePayloadDto?> StageShareDownload(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.StageShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == shareId)
            .ConfigureAwait(false);

        if (share == null) return null;

        bool isOwner = string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal);
        if (!isOwner && StageShareHasAccessRestrictions(share))
        {
            var userGroups = await DbContext.GroupPairs.AsNoTracking()
                .Where(p => p.GroupUserUID == UserUID)
                .Select(p => p.GroupGID.ToUpperInvariant())
                .ToListAsync().ConfigureAwait(false);

            if (!StageShareAccessibleToUser(share, userGroups))
            {
                return null;
            }
        }

        return new StageSharePayloadDto
        {
            ShareId = share.Id,
            CipherData = share.CipherData,
            Nonce = share.Nonce,
            Salt = share.Salt,
            Tag = share.Tag,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<StageShareEntryDto>> StageShareGetOwn()
    {
        _logger.LogCallInfo();

        var shares = await DbContext.StageShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapStageShareEntryDto(s, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<StageShareEntryDto>> StageShareGetForLocation(StageLocationDto location)
    {
        _logger.LogCallInfo(MareHubLogger.Args(location.WorldId, location.TerritoryId));

        long worldId = location.WorldId;
        int territoryId = location.TerritoryId;
        var shares = await DbContext.StageShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.WorldId == worldId
                && s.TerritoryId == territoryId
                && s.WardId == location.WardId
                && s.DivisionId == location.DivisionId
                && s.HouseId == location.HouseId
                && s.RoomId == location.RoomId)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        var userGroups = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupUserUID == UserUID)
            .Select(p => p.GroupGID.ToUpperInvariant())
            .ToListAsync().ConfigureAwait(false);

        var accessible = shares.Where(s =>
        {
            bool isOwner = string.Equals(s.OwnerUID, UserUID, StringComparison.Ordinal);
            if (isOwner) return true;
            if (!StageShareHasAccessRestrictions(s)) return true;
            return StageShareAccessibleToUser(s, userGroups);
        }).ToList();

        return accessible.Select(s => MapStageShareEntryDto(s, string.Equals(s.OwnerUID, UserUID, StringComparison.Ordinal))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<StageShareEntryDto?> StageShareUpdate(StageShareUpdateRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var share = await DbContext.StageShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Include(s => s.Owner)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId && s.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (share == null) return null;

        share.Description = Truncate(dto.Description ?? string.Empty, StageShareMaxDescriptionLength);
        share.UpdatedUtc = DateTime.UtcNow;

        var normalizedUsers = (dto.AllowedIndividuals ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = (dto.AllowedSyncshells ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new StageShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new StageShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return MapStageShareEntryDto(share, true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> StageShareDelete(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.StageShares.SingleOrDefaultAsync(s => s.Id == shareId && s.OwnerUID == UserUID).ConfigureAwait(false);
        if (share == null) return false;

        DbContext.StageShares.Remove(share);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static StageShareEntryDto MapStageShareEntryDto(StageShare share, bool isOwner)
    {
        return new StageShareEntryDto
        {
            Id = share.Id,
            Location = new StageLocationDto((uint)share.WorldId, (ushort)share.TerritoryId, share.WardId, share.DivisionId, share.HouseId, share.RoomId),
            Description = share.Description,
            CreatedUtc = share.CreatedUtc,
            UpdatedUtc = share.UpdatedUtc,
            IsOwner = isOwner,
            OwnerUid = share.OwnerUID,
            OwnerAlias = share.Owner?.Alias ?? string.Empty,
            AllowedIndividuals = share.AllowedIndividuals.Select(i => i.AllowedIndividualUid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedSyncshells = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            StageName = share.StageName,
            StageFormatVersion = share.StageFormatVersion,
        };
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static bool StageShareHasAccessRestrictions(StageShare share)
    {
        return share.AllowedIndividuals.Count > 0 || share.AllowedSyncshells.Count > 0;
    }

    private bool StageShareAccessibleToUser(StageShare share, IReadOnlyCollection<string> userGroups)
    {
        if (string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal)) return true;

        bool allowedByUser = share.AllowedIndividuals.Any(i => string.Equals(i.AllowedIndividualUid, UserUID, StringComparison.OrdinalIgnoreCase));
        if (allowedByUser) return true;

        if (share.AllowedSyncshells.Count == 0) return false;
        var allowedGroups = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return userGroups.Any(g => allowedGroups.Contains(g));
    }
}
