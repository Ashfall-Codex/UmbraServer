using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.User;
using ApiAffiliationStatus = UmbraSync.API.Dto.Establishment.EstablishmentAffiliationStatus;
using ModelAffiliationStatus = MareSynchronosShared.Models.EstablishmentAffiliationStatus;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private const int AffiliationRequestMaxPerHour = 20;
    private const int ProfileEntryDescriptionLength = 300;

    [Authorize(Policy = "Identified")]
    public async Task<EstablishmentAffiliationRequestResult> EstablishmentAffiliationRequest(EstablishmentAffiliationRequestDto request)
    {
        _logger.LogCallInfo(MareHubLogger.Args(request.EstablishmentId, request.RpProfileId));

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.Database, "estaffil", UserUID, AffiliationRequestMaxPerHour, TimeSpan.FromHours(1)).ConfigureAwait(false))
            return EstablishmentAffiliationRequestResult.RateLimited;

        // La fiche doit appartenir à l'appelant : on n'affilie jamais le personnage d'un autre joueur.
        var profile = await DbContext.CharacterRpProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == request.RpProfileId && p.UserUID == UserUID).ConfigureAwait(false);
        var establishment = await DbContext.Establishments.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == request.EstablishmentId).ConfigureAwait(false);
        if (profile == null || establishment == null) return EstablishmentAffiliationRequestResult.NotFound;

        // Pour ses propres établissements (ses autres persos), l'affiliation est acceptée d'office.
        bool isOwner = string.Equals(establishment.OwnerUID, UserUID, StringComparison.Ordinal);
        // Un blocage ne se trahit pas : même réponse qu'un établissement introuvable.
        if (!isOwner && (!establishment.IsPublic
                         || await UserBlockQueries.IsBlockedEitherWayAsync(DbContext, UserUID, establishment.OwnerUID).ConfigureAwait(false)))
            return EstablishmentAffiliationRequestResult.NotFound;

        if (establishment.ManagerRpProfileId == profile.Id
            || await DbContext.EstablishmentAffiliations.AnyAsync(a => a.EstablishmentId == establishment.Id && a.RpProfileId == profile.Id).ConfigureAwait(false))
            return EstablishmentAffiliationRequestResult.AlreadyExists;

        if (await CountProfileEstablishmentsAsync(profile.Id).ConfigureAwait(false) >= EstablishmentAffiliationLimits.MaxDisplayedPerCharacter)
            return EstablishmentAffiliationRequestResult.LimitReached;

        if (!isOwner && await DbContext.EstablishmentAffiliations
                .CountAsync(a => a.RpProfileId == profile.Id && a.Status == ModelAffiliationStatus.Pending).ConfigureAwait(false)
            >= EstablishmentAffiliationLimits.MaxPendingPerCharacter)
            return EstablishmentAffiliationRequestResult.PendingLimitReached;

        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message) || isOwner) message = null;
        else if (message.Length > EstablishmentAffiliationLimits.MaxMessageLength) message = message[..EstablishmentAffiliationLimits.MaxMessageLength];

        var now = DateTime.UtcNow;
        var affiliation = new EstablishmentAffiliation
        {
            EstablishmentId = establishment.Id,
            RpProfileId = profile.Id,
            UserUID = UserUID,
            Status = isOwner ? ModelAffiliationStatus.Accepted : ModelAffiliationStatus.Pending,
            Message = message,
            RequestedUtc = now,
            RespondedUtc = isOwner ? now : null
        };
        DbContext.EstablishmentAffiliations.Add(affiliation);
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Deux demandes simultanées : l'index unique a gardé la première.
            return EstablishmentAffiliationRequestResult.AlreadyExists;
        }

        _logger.LogCallInfo(MareHubLogger.Args(affiliation.Id, isOwner ? "AutoAccepted" : "Requested"));
        if (isOwner) return EstablishmentAffiliationRequestResult.Accepted;

        affiliation.Establishment = establishment;
        affiliation.RpProfile = profile;
        affiliation.User = await DbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.User(establishment.OwnerUID).Client_EstablishmentAffiliationRequested(affiliation.ToEstablishmentAffiliationDto()).ConfigureAwait(false);
        return EstablishmentAffiliationRequestResult.Requested;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentAffiliationRespond(Guid affiliationId, bool accept)
    {
        _logger.LogCallInfo(MareHubLogger.Args(affiliationId, accept));

        var affiliation = await DbContext.EstablishmentAffiliations
            .Include(a => a.Establishment)
            .Include(a => a.RpProfile)
            .SingleOrDefaultAsync(a => a.Id == affiliationId).ConfigureAwait(false);
        if (affiliation == null || affiliation.Status != ModelAffiliationStatus.Pending
            || !string.Equals(affiliation.Establishment.OwnerUID, UserUID, StringComparison.Ordinal))
            return false;

        if (accept)
        {
            // La limite a pu être atteinte depuis la demande : rien n'est modifié, le propriétaire peut refuser.
            if (await CountProfileEstablishmentsAsync(affiliation.RpProfileId).ConfigureAwait(false) >= EstablishmentAffiliationLimits.MaxDisplayedPerCharacter)
                return false;
            affiliation.Status = ModelAffiliationStatus.Accepted;
            affiliation.RespondedUtc = DateTime.UtcNow;
        }
        else
        {
            DbContext.EstablishmentAffiliations.Remove(affiliation);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var dto = affiliation.ToEstablishmentAffiliationDto() with
        {
            Status = accept ? ApiAffiliationStatus.Accepted : ApiAffiliationStatus.Refused
        };
        await Clients.User(affiliation.UserUID).Client_EstablishmentAffiliationResponded(dto).ConfigureAwait(false);
        return true;
    }

    // Le membre annule sa demande ou quitte l'établissement ; le propriétaire peut aussi retirer un membre.
    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentAffiliationRemove(Guid affiliationId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(affiliationId));

        var affiliation = await DbContext.EstablishmentAffiliations
            .Include(a => a.Establishment)
            .SingleOrDefaultAsync(a => a.Id == affiliationId).ConfigureAwait(false);
        if (affiliation == null) return false;
        if (!string.Equals(affiliation.UserUID, UserUID, StringComparison.Ordinal)
            && !string.Equals(affiliation.Establishment.OwnerUID, UserUID, StringComparison.Ordinal))
            return false;

        DbContext.EstablishmentAffiliations.Remove(affiliation);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> EstablishmentAffiliationSetVisibility(Guid affiliationId, bool showOnProfile)
    {
        _logger.LogCallInfo(MareHubLogger.Args(affiliationId, showOnProfile));

        var affiliation = await DbContext.EstablishmentAffiliations
            .SingleOrDefaultAsync(a => a.Id == affiliationId && a.UserUID == UserUID).ConfigureAwait(false);
        if (affiliation == null) return false;

        affiliation.ShowOnProfile = showOnProfile;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    // Demandes et membres d'un établissement : réservé au propriétaire.
    [Authorize(Policy = "Identified")]
    public async Task<List<EstablishmentAffiliationDto>> EstablishmentAffiliationGetForEstablishment(Guid establishmentId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(establishmentId));

        var isOwner = await DbContext.Establishments.AsNoTracking()
            .AnyAsync(e => e.Id == establishmentId && e.OwnerUID == UserUID).ConfigureAwait(false);
        if (!isOwner) return [];

        return await DbContext.EstablishmentAffiliations.AsNoTracking()
            .Where(a => a.EstablishmentId == establishmentId)
            .OrderBy(a => a.Status)
            .ThenBy(a => a.RequestedUtc)
            .Select(AffiliationProjection(includePicture: true))
            .ToListAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<EstablishmentAffiliationDto>> EstablishmentAffiliationGetMine()
    {
        _logger.LogCallInfo();

        return await DbContext.EstablishmentAffiliations.AsNoTracking()
            .Where(a => a.UserUID == UserUID)
            .OrderBy(a => a.RequestedUtc)
            .Select(AffiliationProjection(includePicture: false))
            .ToListAsync().ConfigureAwait(false);
    }

    // Projection SQL : seules les colonnes utiles sont lues, jamais la bannière RP de chaque fiche.
    private static Expression<Func<EstablishmentAffiliation, EstablishmentAffiliationDto>> AffiliationProjection(bool includePicture)
        => a => new EstablishmentAffiliationDto
        {
            Id = a.Id,
            EstablishmentId = a.EstablishmentId,
            EstablishmentName = a.Establishment.Name,
            UserUID = a.UserUID,
            UserAlias = a.User.Alias,
            RpProfileId = a.RpProfileId,
            CharacterName = a.RpProfile.CharacterName,
            WorldId = a.RpProfile.WorldId,
            RpFirstName = a.RpProfile.RpFirstName,
            RpLastName = a.RpProfile.RpLastName,
            RpProfilePictureBase64 = includePicture ? a.RpProfile.RpProfilePictureBase64 : null,
            Status = (ApiAffiliationStatus)(int)a.Status,
            ShowOnProfile = a.ShowOnProfile,
            Message = a.Message,
            RequestedUtc = a.RequestedUtc
        };

    // Établissements affichés en bas d'une fiche RP, avec les mêmes règles de visibilité que la fiche elle-même.
    [Authorize(Policy = "Identified")]
    public async Task<List<EstablishmentProfileEntryDto>> EstablishmentGetForCharacter(UserDto user)
    {
        _logger.LogCallInfo(MareHubLogger.Args(user));

        if (string.IsNullOrEmpty(user.CharacterName) || !user.WorldId.HasValue) return [];

        var profile = await DbContext.CharacterRpProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == user.User.UID && p.CharacterName == user.CharacterName && p.WorldId == user.WorldId)
            .ConfigureAwait(false);
        if (profile == null) return [];

        var isSelf = string.Equals(user.User.UID, UserUID, StringComparison.Ordinal);
        if (!isSelf)
        {
            var isPaired = (await GetAllPairedUnpausedUsers().ConfigureAwait(false)).Contains(user.User.UID, StringComparer.Ordinal);
            var isDirectPaired = profile.Visibility == RpProfileVisibility.PairsOnly && isPaired
                                 && await IsDirectPairedAsync(user.User.UID).ConfigureAwait(false);
            if (!CanViewRpProfile(profile.Visibility, isSelf, isPaired, isDirectPaired)) return [];
        }

        // Sans gérant désigné, l'établissement reste sur tous les persos du propriétaire, comme avant les affiliations.
        var managed = await DbContext.Establishments.AsNoTracking()
            .Where(e => e.ShowManagerOnProfile
                        && (e.ManagerRpProfileId == profile.Id || (e.ManagerRpProfileId == null && e.OwnerUID == profile.UserUID))
                        && (e.IsPublic || e.OwnerUID == UserUID))
            .OrderBy(e => e.Name)
            .Take(EstablishmentAffiliationLimits.MaxDisplayedPerCharacter)
            .Select(e => new EstablishmentProfileEntryDto
            {
                Id = e.Id,
                Name = e.Name,
                Category = (int)e.Category,
                Description = e.Description != null && e.Description.Length > ProfileEntryDescriptionLength
                    ? e.Description.Substring(0, ProfileEntryDescriptionLength)
                    : e.Description,
                LogoImageBase64 = e.LogoImageBase64,
                IsManager = true
            })
            .ToListAsync().ConfigureAwait(false);

        var affiliated = await DbContext.EstablishmentAffiliations.AsNoTracking()
            .Where(a => a.RpProfileId == profile.Id && a.Status == ModelAffiliationStatus.Accepted && a.ShowOnProfile
                        && (a.Establishment.IsPublic || a.Establishment.OwnerUID == UserUID))
            .OrderBy(a => a.RespondedUtc)
            .Take(EstablishmentAffiliationLimits.MaxDisplayedPerCharacter)
            .Select(a => new EstablishmentProfileEntryDto
            {
                Id = a.Establishment.Id,
                Name = a.Establishment.Name,
                Category = (int)a.Establishment.Category,
                Description = a.Establishment.Description != null && a.Establishment.Description.Length > ProfileEntryDescriptionLength
                    ? a.Establishment.Description.Substring(0, ProfileEntryDescriptionLength)
                    : a.Establishment.Description,
                LogoImageBase64 = a.Establishment.LogoImageBase64,
                IsManager = false
            })
            .ToListAsync().ConfigureAwait(false);

        return managed.Concat(affiliated)
            .DistinctBy(e => e.Id)
            .Take(EstablishmentAffiliationLimits.MaxDisplayedPerCharacter)
            .ToList();
    }

    // Établissements qu'une fiche gère ou auxquels elle est affiliée (acceptés) : c'est ce que plafonne la limite.
    private async Task<int> CountProfileEstablishmentsAsync(int rpProfileId)
    {
        var managed = await DbContext.Establishments.CountAsync(e => e.ManagerRpProfileId == rpProfileId).ConfigureAwait(false);
        var accepted = await DbContext.EstablishmentAffiliations
            .CountAsync(a => a.RpProfileId == rpProfileId && a.Status == ModelAffiliationStatus.Accepted).ConfigureAwait(false);
        return managed + accepted;
    }
}
