using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingScenario;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task HousingScenarioUpload(HousingScenarioUploadRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var normalizedUsers = dto.AllowedIndividuals
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = dto.AllowedSyncshells
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var share = await DbContext.HousingScenarios
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
            share = new HousingScenario
            {
                Id = dto.ShareId,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.HousingScenarios.Add(share);
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

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new HousingScenarioAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new HousingScenarioAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<HousingScenarioPayloadDto?> HousingScenarioDownload(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.HousingScenarios
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == shareId)
            .ConfigureAwait(false);

        if (share == null) return null;

        bool isOwner = string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal);
        if (!isOwner && HousingScenarioHasAccessRestrictions(share))
        {
            var userGroups = await DbContext.GroupPairs.AsNoTracking()
                .Where(p => p.GroupUserUID == UserUID)
                .Select(p => p.GroupGID.ToUpperInvariant())
                .ToListAsync().ConfigureAwait(false);

            if (!HousingScenarioAccessibleToUser(share, userGroups))
            {
                return null;
            }
        }

        return new HousingScenarioPayloadDto
        {
            ShareId = share.Id,
            CipherData = share.CipherData,
            Nonce = share.Nonce,
            Salt = share.Salt,
            Tag = share.Tag,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<HousingScenarioEntryDto>> HousingScenarioGetOwn()
    {
        _logger.LogCallInfo();

        var shares = await DbContext.HousingScenarios.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapHousingScenarioEntryDto(s, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<HousingScenarioEntryDto>> HousingScenarioGetForLocation(LocationInfo location)
    {
        _logger.LogCallInfo(MareHubLogger.Args(location.ServerId, location.TerritoryId, location.WardId, location.HouseId));

        var shares = await DbContext.HousingScenarios.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.ServerId == location.ServerId
                && s.TerritoryId == location.TerritoryId
                && s.DivisionId == location.DivisionId
                && s.WardId == location.WardId
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
            if (!HousingScenarioHasAccessRestrictions(s)) return true;
            return HousingScenarioAccessibleToUser(s, userGroups);
        }).ToList();

        return accessible.Select(s => MapHousingScenarioEntryDto(s, string.Equals(s.OwnerUID, UserUID, StringComparison.Ordinal))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<HousingScenarioEntryDto?> HousingScenarioUpdate(HousingScenarioUpdateRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var share = await DbContext.HousingScenarios
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Include(s => s.Owner)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId && s.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (share == null) return null;

        share.Description = dto.Description ?? string.Empty;
        share.UpdatedUtc = DateTime.UtcNow;

        var normalizedUsers = dto.AllowedIndividuals
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = dto.AllowedSyncshells
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new HousingScenarioAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new HousingScenarioAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return MapHousingScenarioEntryDto(share, true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> HousingScenarioDelete(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.HousingScenarios.SingleOrDefaultAsync(s => s.Id == shareId && s.OwnerUID == UserUID).ConfigureAwait(false);
        if (share == null) return false;

        DbContext.HousingScenarios.Remove(share);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static HousingScenarioEntryDto MapHousingScenarioEntryDto(HousingScenario share, bool isOwner)
    {
        return new HousingScenarioEntryDto
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
            AllowedIndividuals = share.AllowedIndividuals.Select(i => i.AllowedIndividualUid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedSyncshells = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static bool HousingScenarioHasAccessRestrictions(HousingScenario share)
    {
        return share.AllowedIndividuals.Count > 0 || share.AllowedSyncshells.Count > 0;
    }

    private bool HousingScenarioAccessibleToUser(HousingScenario share, IReadOnlyCollection<string> userGroups)
    {
        if (string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal)) return true;

        bool allowedByUser = share.AllowedIndividuals.Any(i => string.Equals(i.AllowedIndividualUid, UserUID, StringComparison.OrdinalIgnoreCase));
        if (allowedByUser) return true;

        if (share.AllowedSyncshells.Count == 0) return false;
        var allowedGroups = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return userGroups.Any(g => allowedGroups.Contains(g));
    }
}