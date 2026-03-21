using UmbraSync.API.Dto.Establishment;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Numerics;
using ModelEstablishmentCategory = MareSynchronosShared.Models.EstablishmentCategory;
using ModelEstablishmentLocationType = MareSynchronosShared.Models.EstablishmentLocationType;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentDto?> EstablishmentCreate(EstablishmentCreateRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.Name, request.Category));

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
            return null;

        if (request.Location == null)
            return null;

        var now = DateTime.UtcNow;
        var establishment = new Establishment
        {
            OwnerUID = UserUID,
            Name = request.Name.Trim(),
            Description = request.Description,
            Category = (ModelEstablishmentCategory)request.Category,
            Languages = request.Languages ?? ["FR"],
            Tags = request.Tags ?? [],
            FactionTag = request.FactionTag,
            Schedule = request.Schedule,
            IsPublic = request.IsPublic,
            LogoImageBase64 = request.LogoImageBase64,
            BannerImageBase64 = request.BannerImageBase64,
            ManagerUID = request.ManagerUID ?? UserUID,
            ShowManagerOnProfile = true,
            CreatedUtc = now,
            UpdatedUtc = now,
            LocationType = (ModelEstablishmentLocationType)request.Location.LocationType,
            TerritoryId = request.Location.TerritoryId,
            ServerId = request.Location.ServerId,
            WardId = request.Location.WardId,
            PlotId = request.Location.PlotId,
            DivisionId = request.Location.DivisionId,
            IsApartment = request.Location.IsApartment,
            RoomId = request.Location.RoomId,
            X = request.Location.X,
            Y = request.Location.Y,
            Z = request.Location.Z,
            Radius = request.Location.Radius
        };

        DbContext.Establishments.Add(establishment);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(establishment.Id, "Created"));
        return establishment.ToEstablishmentDto(UserUID);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentUpdate(EstablishmentUpdateRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.Id));

        var establishment = await DbContext.Establishments
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (establishment == null) return false;

        if (!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 100)
            establishment.Name = request.Name.Trim();

        establishment.Description = request.Description;
        establishment.Category = (ModelEstablishmentCategory)request.Category;
        establishment.Languages = request.Languages ?? establishment.Languages;
        establishment.Tags = request.Tags ?? establishment.Tags;
        establishment.FactionTag = request.FactionTag;
        establishment.Schedule = request.Schedule;
        establishment.IsPublic = request.IsPublic;
        establishment.LogoImageBase64 = request.LogoImageBase64;
        establishment.BannerImageBase64 = request.BannerImageBase64;
        establishment.ManagerUID = request.ManagerUID;
        establishment.ShowManagerOnProfile = request.ShowManagerOnProfile;
        establishment.UpdatedUtc = DateTime.UtcNow;

        if (request.Location != null)
        {
            establishment.LocationType = (ModelEstablishmentLocationType)request.Location.LocationType;
            establishment.TerritoryId = request.Location.TerritoryId;
            establishment.ServerId = request.Location.ServerId;
            establishment.WardId = request.Location.WardId;
            establishment.PlotId = request.Location.PlotId;
            establishment.DivisionId = request.Location.DivisionId;
            establishment.IsApartment = request.Location.IsApartment;
            establishment.RoomId = request.Location.RoomId;
            establishment.X = request.Location.X;
            establishment.Y = request.Location.Y;
            establishment.Z = request.Location.Z;
            establishment.Radius = request.Location.Radius;
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        _logger.LogCallInfo(MareHubLogger.Args(request.Id, "Updated"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentDelete(Guid id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));

        var establishment = await DbContext.Establishments
            .FirstOrDefaultAsync(e => e.Id == id && e.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (establishment == null) return false;

        DbContext.Establishments.Remove(establishment);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        _logger.LogCallInfo(MareHubLogger.Args(id, "Deleted"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentDto?> EstablishmentGetById(Guid id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));

        var establishment = await DbContext.Establishments
            .Include(e => e.Owner)
            .Include(e => e.Events)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id)
            .ConfigureAwait(false);

        if (establishment == null) return null;
        if (!establishment.IsPublic && establishment.OwnerUID != UserUID) return null;

        return establishment.ToEstablishmentDto(UserUID);
    }

    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentListResponseDto> EstablishmentList(EstablishmentListRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.SearchText, request.Category, request.Page));

        var query = DbContext.Establishments
            .Include(e => e.Owner)
            .Include(e => e.Events)
            .AsNoTracking()
            .Where(e => e.IsPublic);

        if (request.Category.HasValue)
            query = query.Where(e => (int)e.Category == request.Category.Value);

        if (request.Languages != null && request.Languages.Length > 0)
            query = query.Where(e => e.Languages.Any(l => request.Languages.Contains(l)));

        if (request.Tags != null && request.Tags.Length > 0)
            query = query.Where(e => e.Tags.Any(t => request.Tags.Contains(t)));

        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            var search = request.SearchText.Trim().ToLowerInvariant();
            query = query.Where(e => e.Name.ToLower().Contains(search)
                                  || (e.Description != null && e.Description.ToLower().Contains(search)));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);

        var pageSize = Math.Clamp(request.PageSize, 1, 50);
        var page = Math.Max(request.Page, 0);

        var establishments = await query
            .OrderByDescending(e => e.UpdatedUtc)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync()
            .ConfigureAwait(false);

        return new EstablishmentListResponseDto
        {
            Establishments = establishments.Select(e => e.ToEstablishmentDto(UserUID)).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentNearbyResponseDto> EstablishmentGetNearby(EstablishmentNearbyRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.TerritoryId, request.X, request.Z));

        var query = DbContext.Establishments
            .Include(e => e.Owner)
            .Include(e => e.Events)
            .AsNoTracking()
            .Where(e => e.IsPublic && e.TerritoryId == request.TerritoryId);

        if (request.ServerId.HasValue)
            query = query.Where(e => e.ServerId == request.ServerId);
        if (request.WardId.HasValue)
            query = query.Where(e => e.WardId == request.WardId);
        if (request.DivisionId.HasValue)
            query = query.Where(e => e.DivisionId == request.DivisionId);

        var candidates = await query.ToListAsync().ConfigureAwait(false);

        var playerPos = new Vector2(request.X, request.Z);
        var searchRadius = Math.Min(request.Radius, 500f);

        var nearby = candidates
            .Where(e =>
            {
                if (e.LocationType == ModelEstablishmentLocationType.Zone && e.X.HasValue && e.Z.HasValue)
                {
                    var dist = Vector2.Distance(playerPos, new Vector2(e.X.Value, e.Z.Value));
                    return dist <= searchRadius;
                }
                // Housing matches are included if territory/ward/division match
                if (e.LocationType == ModelEstablishmentLocationType.Housing)
                    return true;
                return false;
            })
            .OrderBy(e =>
            {
                if (e.X.HasValue && e.Z.HasValue)
                    return Vector2.Distance(playerPos, new Vector2(e.X.Value, e.Z.Value));
                return 0f;
            })
            .Take(20)
            .ToList();

        return new EstablishmentNearbyResponseDto
        {
            Establishments = nearby.Select(e => e.ToEstablishmentDto(UserUID)).ToList()
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<EstablishmentDto>> EstablishmentGetByOwner()
    {
        _logger.LogCallInfo();

        var establishments = await DbContext.Establishments
            .Include(e => e.Owner)
            .Include(e => e.Events)
            .AsNoTracking()
            .Where(e => e.OwnerUID == UserUID)
            .OrderByDescending(e => e.UpdatedUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        return establishments.Select(e => e.ToEstablishmentDto(UserUID)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentEventDto?> EstablishmentEventUpsert(EstablishmentEventUpsertRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.EstablishmentId, request.Id, request.Title));

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 100)
            return null;

        var establishment = await DbContext.Establishments
            .FirstOrDefaultAsync(e => e.Id == request.EstablishmentId && e.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (establishment == null) return null;

        EstablishmentEvent evt;

        if (request.Id.HasValue && request.Id.Value != Guid.Empty)
        {
            evt = await DbContext.EstablishmentEvents
                .FirstOrDefaultAsync(e => e.Id == request.Id.Value && e.EstablishmentId == request.EstablishmentId)
                .ConfigureAwait(false);

            if (evt == null) return null;

            evt.Title = request.Title.Trim();
            evt.Description = request.Description;
            evt.StartsAtUtc = request.StartsAtUtc;
            evt.EndsAtUtc = request.EndsAtUtc;
            evt.Recurrence = request.Recurrence;
        }
        else
        {
            evt = new EstablishmentEvent
            {
                EstablishmentId = request.EstablishmentId,
                Title = request.Title.Trim(),
                Description = request.Description,
                StartsAtUtc = request.StartsAtUtc,
                EndsAtUtc = request.EndsAtUtc,
                Recurrence = request.Recurrence,
                CreatedUtc = DateTime.UtcNow
            };
            DbContext.EstablishmentEvents.Add(evt);
        }

        establishment.UpdatedUtc = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(evt.Id, "EventUpserted"));
        return evt.ToEstablishmentEventDto();
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentEventDelete(Guid eventId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(eventId));

        var evt = await DbContext.EstablishmentEvents
            .Include(e => e.Establishment)
            .FirstOrDefaultAsync(e => e.Id == eventId)
            .ConfigureAwait(false);

        if (evt == null) return false;
        if (evt.Establishment.OwnerUID != UserUID) return false;

        DbContext.EstablishmentEvents.Remove(evt);
        evt.Establishment.UpdatedUtc = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(eventId, "EventDeleted"));
        return true;
    }
}
