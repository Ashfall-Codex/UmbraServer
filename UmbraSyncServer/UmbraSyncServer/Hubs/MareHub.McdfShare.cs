using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UmbraSync.API.Dto.McdfShare;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<List<McdfShareEntryDto>> McdfShareGetOwn()
    {
        _logger.LogCallInfo();

        var shares = await DbContext.McdfShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapShareEntryDto(s, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<McdfShareEntryDto>> McdfShareGetShared()
    {
        _logger.LogCallInfo();

        var userGroups = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupUserUID == UserUID)
            .Select(p => p.GroupGID.ToUpperInvariant())
            .ToListAsync().ConfigureAwait(false);

        var shares = await DbContext.McdfShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID != UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var accessible = shares.Where(s => ShareAccessibleToUser(s, userGroups) && (!s.ExpiresAtUtc.HasValue || s.ExpiresAtUtc > now)).ToList();

        return accessible.Select(s => MapShareEntryDto(s, false)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> McdfShareUpload(McdfShareUploadRequestDto dto)
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

        var share = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId)
            .ConfigureAwait(false);

        if (share != null && !string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (share == null)
        {
            share = new McdfShare
            {
                Id = dto.ShareId,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.McdfShares.Add(share);
        }

        share.Description = dto.Description ?? string.Empty;
        share.CipherData = dto.CipherData ?? Array.Empty<byte>();
        share.Nonce = dto.Nonce ?? Array.Empty<byte>();
        share.Salt = dto.Salt ?? Array.Empty<byte>();
        share.Tag = dto.Tag ?? Array.Empty<byte>();
        share.ExpiresAtUtc = dto.ExpiresAtUtc;
        share.UpdatedUtc = now;

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new McdfShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new McdfShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Notify recipients
        var recipientUids = new HashSet<string>(normalizedUsers, StringComparer.OrdinalIgnoreCase);
        foreach (var gid in normalizedGroups)
        {
            var members = await DbContext.GroupPairs
                .Where(p => p.GroupGID == gid)
                .Select(p => p.GroupUserUID)
                .ToListAsync().ConfigureAwait(false);
            foreach (var m in members) recipientUids.Add(m);
        }
        recipientUids.Remove(UserUID);

        if (recipientUids.Count > 0)
        {
            await Clients.Users(recipientUids.ToList())
                .Client_McdfShareReceived(UserUID, share.Description)
                .ConfigureAwait(false);
        }

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<McdfShareEntryDto?> McdfShareUpdate(McdfShareUpdateRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var share = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Include(s => s.Owner)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId && s.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (share == null) return null;

        share.Description = dto.Description ?? string.Empty;
        share.ExpiresAtUtc = dto.ExpiresAtUtc;
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
            share.AllowedIndividuals.Add(new McdfShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new McdfShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return MapShareEntryDto(share, true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> McdfShareDelete(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.McdfShares.SingleOrDefaultAsync(s => s.Id == shareId && s.OwnerUID == UserUID).ConfigureAwait(false);
        if (share == null) return false;

        DbContext.McdfShares.Remove(share);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<McdfSharePayloadDto?> McdfShareDownload(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == shareId)
            .ConfigureAwait(false);

        if (share == null) return null;

        var userGroups = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupUserUID == UserUID)
            .Select(p => p.GroupGID.ToUpperInvariant())
            .ToListAsync().ConfigureAwait(false);

        bool isOwner = string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal);
        if (!isOwner && (!ShareAccessibleToUser(share, userGroups) || (share.ExpiresAtUtc.HasValue && share.ExpiresAtUtc.Value <= DateTime.UtcNow)))
        {
            return null;
        }

        share.DownloadCount++;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return new McdfSharePayloadDto
        {
            ShareId = share.Id,
            Description = share.Description,
            CipherData = share.CipherData,
            Nonce = share.Nonce,
            Salt = share.Salt,
            Tag = share.Tag,
            CreatedUtc = share.CreatedUtc,
            ExpiresAtUtc = share.ExpiresAtUtc,
        };
    }

    private static string NormalizeUid(string candidate) => candidate.Trim().ToUpperInvariant();
    private static string NormalizeGroup(string candidate) => candidate.Trim().ToUpperInvariant();

    private static McdfShareEntryDto MapShareEntryDto(McdfShare share, bool isOwner)
    {
        return new McdfShareEntryDto
        {
            Id = share.Id,
            Description = share.Description,
            CreatedUtc = share.CreatedUtc,
            UpdatedUtc = share.UpdatedUtc,
            ExpiresAtUtc = share.ExpiresAtUtc,
            DownloadCount = share.DownloadCount,
            IsOwner = isOwner,
            OwnerUid = share.OwnerUID,
            OwnerAlias = share.Owner?.Alias ?? string.Empty,
            AllowedIndividuals = share.AllowedIndividuals.Select(i => i.AllowedIndividualUid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedSyncshells = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            DataSize = share.CipherData?.Length ?? 0,
        };
    }

    private bool ShareAccessibleToUser(McdfShare share, IReadOnlyCollection<string> userGroups)
    {
        if (string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal)) return true;

        bool allowedByUser = share.AllowedIndividuals.Any(i => string.Equals(i.AllowedIndividualUid, UserUID, StringComparison.OrdinalIgnoreCase));
        if (allowedByUser) return true;

        if (share.AllowedSyncshells.Count == 0) return false;
        var allowedGroups = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return userGroups.Any(g => allowedGroups.Contains(g));
    }
}
