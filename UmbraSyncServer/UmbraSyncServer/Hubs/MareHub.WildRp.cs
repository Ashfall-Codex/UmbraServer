using UmbraSync.API.Dto.WildRp;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<WildRpAnnouncementDto?> WildRpAnnounce(WildRpAnnounceRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.WorldId, request.TerritoryId));

        if (request.WorldId == 0 || request.TerritoryId == 0)
            return null;

        var message = request.Message?.Trim();
        if (message != null && message.Length > 200)
            message = message[..200];

        var existing = await DbContext.WildRpAnnouncements
            .FirstOrDefaultAsync(a => a.UserUID == UserUID)
            .ConfigureAwait(false);

        if (existing != null)
            DbContext.WildRpAnnouncements.Remove(existing);

        var now = DateTime.UtcNow;
        var announcement = new WildRpAnnouncement
        {
            UserUID = UserUID,
            WorldId = request.WorldId,
            TerritoryId = request.TerritoryId,
            Message = message,
            RpProfileId = request.RpProfileId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(2)
        };

        DbContext.WildRpAnnouncements.Add(announcement);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var result = await DbContext.WildRpAnnouncements
            .Include(a => a.Owner)
            .Include(a => a.RpProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == announcement.Id)
            .ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(announcement.Id, "Announced"));
        return result?.ToWildRpAnnouncementDto();
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> WildRpWithdraw()
    {
        _logger.LogCallInfo();

        var existing = await DbContext.WildRpAnnouncements
            .FirstOrDefaultAsync(a => a.UserUID == UserUID)
            .ConfigureAwait(false);

        if (existing == null) return false;

        DbContext.WildRpAnnouncements.Remove(existing);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args("Withdrawn"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<WildRpListResponseDto> WildRpList(WildRpListRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.WorldId, request.Page));

        var now = DateTime.UtcNow;

        var expiredCount = await DbContext.WildRpAnnouncements
            .Where(a => a.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);

        if (expiredCount > 0)
            _logger.LogCallInfo(MareHubLogger.Args($"Purged {expiredCount} expired"));

        var query = DbContext.WildRpAnnouncements
            .Include(a => a.Owner)
            .Include(a => a.RpProfile)
            .AsNoTracking()
            .Where(a => a.ExpiresAtUtc > now);

        if (request.WorldId.HasValue && request.WorldId.Value > 0)
            query = query.Where(a => a.WorldId == request.WorldId.Value);

        var totalCount = await query.CountAsync().ConfigureAwait(false);

        var pageSize = Math.Clamp(request.PageSize, 1, 50);
        var page = Math.Max(request.Page, 0);

        var announcements = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync()
            .ConfigureAwait(false);

        return new WildRpListResponseDto
        {
            Announcements = announcements.Select(a => a.ToWildRpAnnouncementDto()).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<WildRpAnnouncementDto?> WildRpGetOwn()
    {
        _logger.LogCallInfo();

        var now = DateTime.UtcNow;
        var announcement = await DbContext.WildRpAnnouncements
            .Include(a => a.Owner)
            .Include(a => a.RpProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserUID == UserUID && a.ExpiresAtUtc > now)
            .ConfigureAwait(false);

        return announcement?.ToWildRpAnnouncementDto();
    }
}
