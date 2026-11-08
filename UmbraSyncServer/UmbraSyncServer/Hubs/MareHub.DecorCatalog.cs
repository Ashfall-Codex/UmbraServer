using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.DecorCatalog;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const int DecorCatalogMaxContentBytes = 16 * 1024 * 1024;
    private const int DecorCatalogMaxNameLength = 128;
    private const int DecorCatalogMaxDescriptionLength = 1024;
    private const int DecorCatalogMaxSearchLength = 100;
    private const int DecorCatalogMaxPreviewBase64Length = 720_000;
    private const int DecorCatalogMaxPreviewDimension = 1024;
    private const int DecorCatalogMaxEntriesPerUser = 20;
    private const int DecorCatalogMaxReasonLength = 500;

    [Authorize(Policy = "Identified")]
    public async Task<DecorCatalogListResponseDto> DecorCatalogList(DecorCatalogListRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.Sort, dto.Page));

        var uid = UserUID;
        var query = DbContext.DecorCatalogEntries.AsNoTracking();
        if (dto.OnlyMine)
            query = query.Where(e => e.OwnerUID == uid);
        if (dto.OnlyFavorites)
            query = query.Where(e => e.Favorites.Any(f => f.UserUID == uid));
        if (!string.IsNullOrWhiteSpace(dto.SearchText))
        {
            var search = Truncate(dto.SearchText.Trim(), DecorCatalogMaxSearchLength).ToLowerInvariant();
            query = query.Where(e => e.Name.ToLower().Contains(search) || e.Description.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pageSize = Math.Clamp(dto.PageSize, 1, 50);
        var page = Math.Max(dto.Page, 0);

        var projected = query.Select(e => new DecorCatalogEntryDto
        {
            Id = e.Id,
            Name = e.Name,
            Description = e.Description,
            OwnerUid = e.OwnerUID,
            OwnerAlias = e.Owner.Alias ?? string.Empty,
            IsOwner = e.OwnerUID == uid,
            CreatedUtc = e.CreatedUtc,
            UpdatedUtc = e.UpdatedUtc,
            StageFormatVersion = e.StageFormatVersion,
            FileCount = e.FileCount,
            TotalBytes = e.TotalBytes,
            DisplayCount = e.DisplayCount,
            FavoriteCount = e.Favorites.Count(),
            IsFavorite = e.Favorites.Any(f => f.UserUID == uid),
            PreviewHash = e.PreviewHash,
        });

        projected = dto.Sort switch
        {
            DecorCatalogSort.Popular => projected.OrderByDescending(e => e.DisplayCount).ThenByDescending(e => e.CreatedUtc),
            DecorCatalogSort.MostFavorited => projected.OrderByDescending(e => e.FavoriteCount).ThenByDescending(e => e.CreatedUtc),
            _ => projected.OrderByDescending(e => e.CreatedUtc),
        };

        var entries = await projected.Skip(page * pageSize).Take(pageSize).ToListAsync().ConfigureAwait(false);
        return new DecorCatalogListResponseDto
        {
            Entries = entries,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<string?> DecorCatalogGetPreview(Guid id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));

        return await DbContext.DecorCatalogEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => e.PreviewImageBase64)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<DecorCatalogPayloadDto?> DecorCatalogDownload(Guid id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));

        var entry = await DbContext.DecorCatalogEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.Content, e.AnchorX, e.AnchorY, e.AnchorZ, e.StageFormatVersion, e.OwnerUID })
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (entry == null) return null;

        if (!string.Equals(entry.OwnerUID, UserUID, StringComparison.Ordinal))
        {
            await DbContext.DecorCatalogEntries
                .Where(e => e.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.DisplayCount, e => e.DisplayCount + 1))
                .ConfigureAwait(false);
        }

        return new DecorCatalogPayloadDto
        {
            Id = id,
            Content = entry.Content,
            AnchorX = entry.AnchorX,
            AnchorY = entry.AnchorY,
            AnchorZ = entry.AnchorZ,
            StageFormatVersion = entry.StageFormatVersion,
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<DecorCatalogEntryDto?> DecorCatalogPublish(DecorCatalogPublishRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.Id));

        if (dto.Content == null || dto.Content.Length == 0 || dto.Content.Length > DecorCatalogMaxContentBytes) return null;
        var name = Truncate((dto.Name ?? string.Empty).Trim(), DecorCatalogMaxNameLength);
        if (name.Length == 0) return null;

        var entry = await DbContext.DecorCatalogEntries.SingleOrDefaultAsync(e => e.Id == dto.Id).ConfigureAwait(false);
        if (entry != null && !string.Equals(entry.OwnerUID, UserUID, StringComparison.Ordinal)) return null;

        var now = DateTime.UtcNow;
        if (entry == null)
        {
            var owned = await DbContext.DecorCatalogEntries.CountAsync(e => e.OwnerUID == UserUID).ConfigureAwait(false);
            if (owned >= DecorCatalogMaxEntriesPerUser) return null;

            entry = new DecorCatalogEntry
            {
                Id = dto.Id,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.DecorCatalogEntries.Add(entry);
        }

        entry.Name = name;
        entry.Description = Truncate((dto.Description ?? string.Empty).Trim(), DecorCatalogMaxDescriptionLength);
        entry.Content = dto.Content;
        entry.StageFormatVersion = dto.StageFormatVersion;
        entry.FileCount = Math.Max(0, dto.FileCount);
        entry.TotalBytes = Math.Max(0, dto.TotalBytes);
        entry.AnchorX = dto.AnchorX;
        entry.AnchorY = dto.AnchorY;
        entry.AnchorZ = dto.AnchorZ;
        entry.AnchorTerritory = dto.AnchorTerritory;
        entry.UpdatedUtc = now;
        await ApplyDecorPreviewAsync(entry, dto.PreviewImageBase64).ConfigureAwait(false);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return await MapDecorCatalogEntryAsync(entry.Id).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<DecorCatalogEntryDto?> DecorCatalogUpdate(DecorCatalogUpdateRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.Id));

        var entry = await DbContext.DecorCatalogEntries.SingleOrDefaultAsync(e => e.Id == dto.Id && e.OwnerUID == UserUID).ConfigureAwait(false);
        if (entry == null) return null;

        var name = Truncate((dto.Name ?? string.Empty).Trim(), DecorCatalogMaxNameLength);
        if (name.Length == 0) return null;

        entry.Name = name;
        entry.Description = Truncate((dto.Description ?? string.Empty).Trim(), DecorCatalogMaxDescriptionLength);
        entry.UpdatedUtc = DateTime.UtcNow;
        await ApplyDecorPreviewAsync(entry, dto.PreviewImageBase64).ConfigureAwait(false);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return await MapDecorCatalogEntryAsync(entry.Id).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> DecorCatalogDelete(Guid id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));

        var entry = await DbContext.DecorCatalogEntries.SingleOrDefaultAsync(e => e.Id == id && e.OwnerUID == UserUID).ConfigureAwait(false);
        if (entry == null) return false;

        DbContext.DecorCatalogEntries.Remove(entry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<int> DecorCatalogSetFavorite(Guid id, bool favorite)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id, favorite));

        if (!await DbContext.DecorCatalogEntries.AnyAsync(e => e.Id == id).ConfigureAwait(false)) return 0;

        var existing = await DbContext.DecorCatalogFavorites.SingleOrDefaultAsync(f => f.EntryId == id && f.UserUID == UserUID).ConfigureAwait(false);
        try
        {
            if (favorite && existing == null)
            {
                DbContext.DecorCatalogFavorites.Add(new DecorCatalogFavorite { EntryId = id, UserUID = UserUID, CreatedUtc = DateTime.UtcNow });
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
            }
            else if (!favorite && existing != null)
            {
                DbContext.DecorCatalogFavorites.Remove(existing);
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Favori de décor déjà enregistré ou déjà retiré");
        }

        return await DbContext.DecorCatalogFavorites.CountAsync(f => f.EntryId == id).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task DecorCatalogReport(DecorCatalogReportDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.Id));

        var owner = await DbContext.DecorCatalogEntries.AsNoTracking()
            .Where(e => e.Id == dto.Id)
            .Select(e => e.OwnerUID)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (owner == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "This decor no longer exists").ConfigureAwait(false);
            return;
        }
        if (string.Equals(owner, UserUID, StringComparison.Ordinal))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "You cannot report your own decor").ConfigureAwait(false);
            return;
        }
        if (await DbContext.DecorCatalogReports.AnyAsync(r => r.EntryId == dto.Id && r.ReportingUserUID == UserUID).ConfigureAwait(false))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "You already reported this decor and it's pending validation").ConfigureAwait(false);
            return;
        }

        DbContext.DecorCatalogReports.Add(new DecorCatalogReport
        {
            EntryId = dto.Id,
            ReportingUserUID = UserUID,
            Reason = Truncate((dto.Reason ?? string.Empty).Trim(), DecorCatalogMaxReasonLength),
            ReportDate = DateTime.UtcNow,
        });
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task ApplyDecorPreviewAsync(DecorCatalogEntry entry, string? incoming)
    {
        if (incoming == null) return;
        if (incoming.Length == 0)
        {
            entry.PreviewImageBase64 = null;
            entry.PreviewHash = null;
            return;
        }
        if (string.Equals(incoming, entry.PreviewImageBase64, StringComparison.Ordinal)) return;

        var sanitized = ProfileImageSanitizer.Sanitize(incoming, DecorCatalogMaxPreviewBase64Length, DecorCatalogMaxPreviewDimension);
        if (sanitized == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning,
                "The decor preview image was rejected (PNG or JPEG, 1024 px and 512 KiB at most). The previous image was kept.").ConfigureAwait(false);
            return;
        }

        entry.PreviewImageBase64 = sanitized;
        entry.PreviewHash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(sanitized)));
    }

    private async Task<DecorCatalogEntryDto?> MapDecorCatalogEntryAsync(Guid id)
    {
        var uid = UserUID;
        return await DbContext.DecorCatalogEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new DecorCatalogEntryDto
            {
                Id = e.Id,
                Name = e.Name,
                Description = e.Description,
                OwnerUid = e.OwnerUID,
                OwnerAlias = e.Owner.Alias ?? string.Empty,
                IsOwner = e.OwnerUID == uid,
                CreatedUtc = e.CreatedUtc,
                UpdatedUtc = e.UpdatedUtc,
                StageFormatVersion = e.StageFormatVersion,
                FileCount = e.FileCount,
                TotalBytes = e.TotalBytes,
                DisplayCount = e.DisplayCount,
                FavoriteCount = e.Favorites.Count(),
                IsFavorite = e.Favorites.Any(f => f.UserUID == uid),
                PreviewHash = e.PreviewHash,
            })
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }
}
