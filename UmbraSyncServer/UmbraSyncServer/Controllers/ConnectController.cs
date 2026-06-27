using MareSynchronosServer.Services;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.User;
using UmbraSync.API.SignalR;

namespace MareSynchronosServer.Controllers;

/// Endpoints d'intégration avec Ashfall Connect (hub d'identité fédérée).
[ApiController]
[Route("main/connect")]
public sealed class ConnectController : ControllerBase
{
    private readonly ConnectClient _connect;
    private readonly MareDbContext _db;
    private readonly ILogger<ConnectController> _logger;
    private readonly IHubContext<Hubs.MareHub, IMareHub> _hub;

    public ConnectController(ConnectClient connect, MareDbContext db, ILogger<ConnectController> logger,
        IHubContext<Hubs.MareHub, IMareHub> hub)
    {
        _connect = connect;
        _db = db;
        _logger = logger;
        _hub = hub;
    }

    /// <summary>
    /// Notifie en temps réel le joueur (et ses paires) qu'un profil RP a été modifié depuis
    /// Ashfall Connect, via le callback standard Client_UserUpdateProfile : les clients en jeu
    /// invalident leur cache de profil et rechargeront la version à jour au prochain affichage.
    /// Best effort : un échec de notification ne fait jamais échouer l'écriture du profil.
    /// </summary>
    private async Task NotifyProfileUpdatedAsync(string uid, CancellationToken ct)
    {
        try
        {
            // Paires directes (ceux qui ont ce joueur dans leur liste). L'invalidation de cache
            // étant inoffensive, on ne filtre ni les pauses ni les permissions.
            var directPairs = await _db.ClientPairs.AsNoTracking()
                .Where(p => p.OtherUserUID == uid)
                .Select(p => p.UserUID)
                .ToListAsync(ct);

            // Membres des syncshells du joueur.
            var groupIds = await _db.GroupPairs.AsNoTracking()
                .Where(gp => gp.GroupUserUID == uid)
                .Select(gp => gp.GroupGID)
                .ToListAsync(ct);
            var groupMembers = groupIds.Count == 0
                ? new List<string>()
                : await _db.GroupPairs.AsNoTracking()
                    .Where(gp => groupIds.Contains(gp.GroupGID) && gp.GroupUserUID != uid)
                    .Select(gp => gp.GroupUserUID)
                    .ToListAsync(ct);

            var dto = new UserDto(new UserData(uid));
            var targets = directPairs.Concat(groupMembers).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count > 0)
            {
                await _hub.Clients.Users(targets).Client_UserUpdateProfile(dto);
            }
            await _hub.Clients.User(uid).Client_UserUpdateProfile(dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification de mise à jour de profil Connect échouée pour {uid}", uid);
        }
    }

    [HttpGet("link-status/{code}")]
    [Authorize(Policy = "Authenticated")]
    public async Task<IActionResult> LinkStatus(string code, CancellationToken ct)
    {
        if (!_connect.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "connect_not_configured" });

        var status = await _connect.GetLinkCodeStatusAsync(code, ct);
        if (status is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "connect_unreachable" });

        return Ok(status);
    }
    
    // Utilisé par le plugin pour afficher "Compte lié à X" dans Settings au lieu du bouton "Générer".
    [HttpGet("my-status")]
    [Authorize(Policy = "Authenticated")]
    public async Task<IActionResult> MyStatus(CancellationToken ct)
    {
        if (!_connect.IsConfigured)
            return Ok(new { connectEnabled = false });

        var uid = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (string.IsNullOrEmpty(uid)) return Unauthorized();

        var verif = await _connect.GetVerificationAsync(uid, ct);
        if (verif is null)
            return Ok(new { connectEnabled = true, linked = false });

        return Ok(new { connectEnabled = true, linked = verif.Verified, level = verif.Level, since = verif.Since });
    }

    public sealed record GenerateLinkCodeRequest(List<CharacterDto>? Characters);
    public sealed record CharacterDto(string Name, string World);
    public sealed record SyncCharactersRequest(List<CharacterDto>? Characters);

    private const int MaxCharacters = 50;
    private const int MaxFieldLength = 64;

    [HttpPost("generate-link-code")]
    [Authorize(Policy = "Authenticated")]
    public async Task<IActionResult> GenerateLinkCode([FromBody] GenerateLinkCodeRequest? body, CancellationToken ct)
    {
        if (!_connect.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "connect_not_configured" });

        var uid = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (string.IsNullOrEmpty(uid))
            return Unauthorized();

        var alias = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Alias, StringComparison.Ordinal))?.Value;

        // GARDE-FOU SÉCURITÉ : on accepte uniquement Name + World, on rejette tout reste
        var characters = body?.Characters?
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Take(MaxCharacters)
            .Select(c => new ConnectClient.CharacterInfo(
                Truncate(c.Name, MaxFieldLength),
                Truncate(c.World ?? string.Empty, MaxFieldLength)))
            .ToList();

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        try
        {
            var result = await _connect.GenerateLinkCodeAsync(uid, alias, characters, ct);
            return Ok(new { code = result.Code, expiresAt = result.ExpiresAt });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Connect indisponible lors de la génération de code");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "connect_unreachable" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur inattendue lors de la génération du code Connect");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "internal_error" });
        }
    }
    
    [HttpPost("sync-characters")]
    [Authorize(Policy = "Authenticated")]
    public async Task<IActionResult> SyncCharacters([FromBody] SyncCharactersRequest? body, CancellationToken ct)
    {
        if (!_connect.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "connect_not_configured" });

        var uid = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (string.IsNullOrEmpty(uid)) return Unauthorized();

        var alias = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Alias, StringComparison.Ordinal))?.Value;

        // Mêmes garde-fous que GenerateLinkCode : whitelist Name + World, troncature, plafond.
        var characters = body?.Characters?
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Take(MaxCharacters)
            .Select(c => new ConnectClient.CharacterInfo(
                Truncate(c.Name, MaxFieldLength),
                Truncate(c.World ?? string.Empty, MaxFieldLength)))
            .ToList();

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        try
        {
            var pushed = await _connect.PushMetadataAsync(uid, alias, characters, ct);
            return pushed ? Ok(new { synced = true }) : NotFound(new { synced = false });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du push de metadata vers Connect (sync-characters)");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "connect_unreachable" });
        }
    }

    public sealed record RpAvatarDto(string CharacterName, uint WorldId, string? AvatarBase64);

    [HttpGet("rp-avatars/{uid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetRpAvatars(string uid, CancellationToken ct)
    {
        var avatars = await _db.CharacterRpProfiles
            .AsNoTracking()
            .Where(p => p.UserUID == uid && p.RpProfilePictureBase64 != null)
            .Select(p => new RpAvatarDto(p.CharacterName, p.WorldId, p.RpProfilePictureBase64))
            .ToListAsync(ct);
        return Ok(avatars);
    }

    public sealed record RpProfileDto(
        string CharacterName,
        uint WorldId,
        string? RpFirstName,
        string? RpLastName,
        string? RpTitle,
        string? RpAge,
        string? RpRace,
        string? RpEthnicity,
        string? RpHeight,
        string? RpBuild,
        string? RpResidence,
        string? RpOccupation,
        string? RpAffiliation,
        string? RpAlignment,
        string? RpAdditionalInfo,
        string? RpCustomFields,
        string? RpNameColor,
        bool IsRpNSFW,
        byte RpLevel);

    [HttpGet("rp-profile/{uid}/{worldId:int}/{characterName}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetRpProfile(string uid, uint worldId, string characterName, CancellationToken ct)
    {
        var profile = await _db.CharacterRpProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == uid && p.CharacterName == characterName && p.WorldId == worldId, ct);

        if (profile is null)
            return Ok(new RpProfileDto(characterName, worldId, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, 0));

        return Ok(new RpProfileDto(
            profile.CharacterName, profile.WorldId,
            profile.RpFirstName, profile.RpLastName, profile.RpTitle,
            profile.RpAge, profile.RpRace, profile.RpEthnicity,
            profile.RpHeight, profile.RpBuild, profile.RpResidence,
            profile.RpOccupation, profile.RpAffiliation, profile.RpAlignment,
            profile.RpAdditionalInfo, profile.RpCustomFields, profile.RpNameColor,
            profile.IsRpNSFW, profile.RpLevel));
    }

    [HttpPut("rp-profile/{uid}/{worldId:int}/{characterName}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> PutRpProfile(string uid, uint worldId, string characterName,
        [FromBody] RpProfileDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(characterName) || worldId == 0)
            return BadRequest(new { error = "invalid_key" });
        
        var profile = await _db.CharacterRpProfiles
            .SingleOrDefaultAsync(p => p.UserUID == uid && p.CharacterName == characterName && p.WorldId == worldId, ct);

        if (profile is null)
        {
            profile = new CharacterRpProfileData
            {
                UserUID = uid,
                CharacterName = characterName,
                WorldId = worldId,
            };
            _db.CharacterRpProfiles.Add(profile);
        }

        // Bornes serveur — Connect a déjà ses propres limites côté UI mais on les répète ici
        // pour ne pas faire confiance au client. Doublé par rapport aux UI maxlength pour la
        // marge (UI=240 → DB=480 etc.) au cas où on rallonge un jour les champs côté UI.
        profile.RpFirstName = TrimOrNull(body.RpFirstName, 480);
        profile.RpLastName = TrimOrNull(body.RpLastName, 480);
        profile.RpTitle = TrimOrNull(body.RpTitle, 480);
        profile.RpAge = TrimOrNull(body.RpAge, 480);
        profile.RpRace = TrimOrNull(body.RpRace, 480);
        profile.RpEthnicity = TrimOrNull(body.RpEthnicity, 480);
        profile.RpHeight = TrimOrNull(body.RpHeight, 480);
        profile.RpBuild = TrimOrNull(body.RpBuild, 480);
        profile.RpResidence = TrimOrNull(body.RpResidence, 480);
        profile.RpOccupation = TrimOrNull(body.RpOccupation, 480);
        profile.RpAffiliation = TrimOrNull(body.RpAffiliation, 480);
        profile.RpAlignment = TrimOrNull(body.RpAlignment, 480);
        profile.RpAdditionalInfo = TrimOrNull(body.RpAdditionalInfo, 2000);
        profile.RpCustomFields = NormalizeCustomFields(body.RpCustomFields);
        profile.RpNameColor = TrimOrNull(body.RpNameColor, 32);
        profile.IsRpNSFW = body.IsRpNSFW;
        profile.RpLevel = body.RpLevel;

        await _db.SaveChangesAsync(ct);
        await NotifyProfileUpdatedAsync(uid, ct);
        return NoContent();

        static string? TrimOrNull(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length <= max ? t : t[..max];
        }
    }
    

    public sealed record EnrichedProfileResponse(string? Json, string? Visibility);
    public sealed record EnrichedProfileRequest(string? Json, string? Visibility);

    private static readonly string[] AllowedVisibilities = ["private", "pairs", "syncshells", "public"];

    [HttpGet("rp-enriched/{uid}/{worldId:int}/{characterName}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetEnrichedProfile(string uid, uint worldId, string characterName, CancellationToken ct)
    {
        var profile = await _db.CharacterRpProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserUID == uid && p.CharacterName == characterName && p.WorldId == worldId, ct);
        return Ok(new EnrichedProfileResponse(profile?.EnrichedProfileJson, profile?.EnrichedProfileVisibility ?? "private"));
    }

    [HttpPut("rp-enriched/{uid}/{worldId:int}/{characterName}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> PutEnrichedProfile(string uid, uint worldId, string characterName,
        [FromBody] EnrichedProfileRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(characterName) || worldId == 0)
            return BadRequest(new { error = "invalid_key" });

        var visibility = body.Visibility ?? "private";
        if (Array.IndexOf(AllowedVisibilities, visibility) < 0)
            return BadRequest(new { error = "invalid_visibility" });

        var profile = await _db.CharacterRpProfiles
            .SingleOrDefaultAsync(p => p.UserUID == uid && p.CharacterName == characterName && p.WorldId == worldId, ct);

        if (profile is null)
        {
            profile = new CharacterRpProfileData
            {
                UserUID = uid,
                CharacterName = characterName,
                WorldId = worldId,
            };
            _db.CharacterRpProfiles.Add(profile);
        }

        profile.EnrichedProfileJson = string.IsNullOrWhiteSpace(body.Json) ? null : body.Json;
        profile.EnrichedProfileVisibility = visibility;
        await _db.SaveChangesAsync(ct);
        await NotifyProfileUpdatedAsync(uid, ct);
        return NoContent();
    }

    private static string? NormalizeCustomFields(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var items = new List<object>();
            int idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = TryStr(el, "Name") ?? TryStr(el, "name") ?? "";
                var value = TryStr(el, "Value") ?? TryStr(el, "value") ?? "";
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value)) continue;
                items.Add(new { Name = name.Trim(), Value = value.Trim(), Order = idx++ });
            }
            if (items.Count == 0) return null;
            return System.Text.Json.JsonSerializer.Serialize(items);
        }
        catch { return null; }

        static string? TryStr(System.Text.Json.JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
    }
}