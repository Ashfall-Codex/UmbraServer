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

    public sealed record ManagedUidDto(string Uid, string? Alias, bool IsPrimary, bool IsBanned);

    /// <summary>
    /// UID dont Ashfall Connect peut gérer la clé pour le compte propriétaire de <paramref name="uid"/> :
    /// l'UID lui-même plus ses UID secondaires. Si l'UID fourni est lui-même un secondaire, on ne
    /// remonte jamais à son primaire — sinon lier un secondaire donnerait la main sur tout le compte.
    /// </summary>
    [HttpGet("managed-uids/{uid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetManagedUids(string uid, CancellationToken ct)
    {
        var auths = await ResolveManagedAuthsAsync(uid, ct);
        if (auths.Count == 0) return NotFound(new { error = "unknown_uid" });

        return Ok(auths.Select(a => new ManagedUidDto(a.UserUID, a.User?.Alias, a.PrimaryUserUID is null, a.IsBanned)));
    }

    /// <summary>
    /// Lignes Auth que Connect peut gérer pour ce compte : l'UID lui-même plus ses secondaires.
    /// Un UID secondaire ne remonte jamais vers son primaire.
    /// </summary>
    private async Task<List<MareSynchronosShared.Models.Auth>> ResolveManagedAuthsAsync(string uid, CancellationToken ct)
    {
        var self = await _db.Auth.AsNoTracking().Include(a => a.User)
            .SingleOrDefaultAsync(a => a.UserUID == uid, ct);
        if (self is null) return [];

        var result = new List<MareSynchronosShared.Models.Auth> { self };
        if (self.PrimaryUserUID is null)
        {
            var secondaries = await _db.Auth.AsNoTracking().Include(a => a.User)
                .Where(a => a.PrimaryUserUID == uid)
                .ToListAsync(ct);
            result.AddRange(secondaries);
        }
        return result;
    }

    public sealed record SyncshellDto(
        string Gid, string? Alias, string Role, string ViaUid,
        int MemberCount, int MaxUserCount, bool InvitesEnabled, bool HasPassword,
        bool AutoDetectVisible, bool IsTemporary, DateTime? ExpiresAt, bool IsPaused,
        string OwnerUid, string? OwnerAlias, string? Description, bool IsNsfw);

    /// <summary>
    /// Syncshells que ce compte administre : celles qu'il possède et celles où il est modérateur,
    /// pour son UID principal comme pour ses UID secondaires.
    /// </summary>
    [HttpGet("syncshells/{uid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetSyncshells(string uid, CancellationToken ct)
    {
        var auths = await ResolveManagedAuthsAsync(uid, ct);
        if (auths.Count == 0) return NotFound(new { error = "unknown_uid" });

        var uids = auths.Select(a => a.UserUID).ToList();

        var owned = await _db.Groups.AsNoTracking()
            .Where(g => uids.Contains(g.OwnerUID))
            .ToListAsync(ct);

        var moderated = await _db.GroupPairs.AsNoTracking()
            .Include(gp => gp.Group)
            .Where(gp => uids.Contains(gp.GroupUserUID) && gp.IsModerator)
            .Select(gp => new { gp.Group, gp.GroupUserUID })
            .ToListAsync(ct);

        // Une syncshell possédée reste "propriétaire" même si l'UID y est aussi marqué modérateur.
        var entries = new Dictionary<string, (MareSynchronosShared.Models.Group group, string role, string viaUid)>(StringComparer.Ordinal);
        foreach (var g in owned)
            entries[g.GID] = (g, "owner", g.OwnerUID);
        foreach (var m in moderated)
        {
            if (m.Group is null || entries.ContainsKey(m.Group.GID)) continue;
            entries[m.Group.GID] = (m.Group, "moderator", m.GroupUserUID);
        }

        if (entries.Count == 0) return Ok(Array.Empty<SyncshellDto>());

        var gids = entries.Keys.ToList();

        var memberCounts = await _db.GroupPairs.AsNoTracking()
            .Where(gp => gids.Contains(gp.GroupGID))
            .GroupBy(gp => gp.GroupGID)
            .Select(grp => new { Gid = grp.Key, Count = grp.Count() })
            .ToDictionaryAsync(x => x.Gid, x => x.Count, StringComparer.Ordinal, ct);

        var profiles = await _db.GroupProfiles.AsNoTracking()
            .Where(p => gids.Contains(p.GroupGID))
            .ToDictionaryAsync(p => p.GroupGID, StringComparer.Ordinal, ct);

        var ownerUids = entries.Values.Select(e => e.group.OwnerUID).Distinct(StringComparer.Ordinal).ToList();
        var ownerAliases = await _db.Users.AsNoTracking()
            .Where(u => ownerUids.Contains(u.UID))
            .ToDictionaryAsync(u => u.UID, u => u.Alias, StringComparer.Ordinal, ct);

        var result = entries.Values.Select(e => new SyncshellDto(
            e.group.GID,
            string.IsNullOrWhiteSpace(e.group.Alias) ? null : e.group.Alias,
            e.role,
            e.viaUid,
            memberCounts.TryGetValue(e.group.GID, out var count) ? count : 0,
            e.group.MaxUserCount,
            e.group.InvitesEnabled,
            !string.IsNullOrEmpty(e.group.HashedPassword) && !e.group.PasswordTemporarilyDisabled,
            e.group.AutoDetectVisible,
            e.group.IsTemporary,
            e.group.ExpiresAt,
            e.group.IsPaused,
            e.group.OwnerUID,
            ownerAliases.TryGetValue(e.group.OwnerUID, out var alias) ? alias : null,
            profiles.TryGetValue(e.group.GID, out var profile) ? profile.Description : null,
            profiles.TryGetValue(e.group.GID, out var p2) && p2.IsNSFW))
            .OrderByDescending(s => s.Role == "owner")
            .ThenBy(s => s.Alias ?? s.Gid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(result);
    }

    public sealed record RegenerateKeyRequest(string? OwnerUid, string? TargetUid);
    public sealed record RegenerateKeyResponse(string Uid, string SecretKey);

    /// <summary>
    /// Régénère la clé secrète d'un UID et invalide l'ancienne. La clé en clair n'est jamais
    /// stockée : seul son SHA-256 va en base, et elle n'est renvoyée qu'ici, une seule fois.
    /// Le client encore connecté avec l'ancienne clé est éjecté immédiatement.
    /// </summary>
    [HttpPost("regenerate-key")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> RegenerateKey([FromBody] RegenerateKeyRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.OwnerUid) || string.IsNullOrWhiteSpace(body.TargetUid))
            return BadRequest(new { error = "invalid_request" });

        var target = await _db.Auth.Include(a => a.User)
            .SingleOrDefaultAsync(a => a.UserUID == body.TargetUid, ct);
        if (target is null) return NotFound(new { error = "unknown_uid" });

        // Double contrôle d'appartenance côté serveur : Connect a déjà vérifié que OwnerUid est
        // bien lié au compte, on vérifie ici que TargetUid dépend bien de OwnerUid.
        var ownsTarget = string.Equals(target.UserUID, body.OwnerUid, StringComparison.Ordinal)
            || string.Equals(target.PrimaryUserUID, body.OwnerUid, StringComparison.Ordinal);
        if (!ownsTarget)
        {
            _logger.LogWarning("Régénération de clé refusée : {owner} ne possède pas {target}", body.OwnerUid, body.TargetUid);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "not_owner" });
        }

        if (target.IsBanned) return StatusCode(StatusCodes.Status403Forbidden, new { error = "banned" });

        var secretKey = MareSynchronosShared.Utils.StringUtils.Sha256String(
            MareSynchronosShared.Utils.StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());

        var newAuth = new MareSynchronosShared.Models.Auth
        {
            HashedKey = MareSynchronosShared.Utils.StringUtils.Sha256String(secretKey),
            UserUID = target.UserUID,
            PrimaryUserUID = target.PrimaryUserUID,
            IsBanned = target.IsBanned,
        };

        // HashedKey est la clé primaire : l'ancienne ligne doit partir avant l'insertion.
        _db.Auth.Remove(target);
        await _db.SaveChangesAsync(ct);
        await _db.Auth.AddAsync(newAuth, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Clé régénérée depuis Connect pour {uid}", target.UserUID);

        try
        {
            await _hub.Clients.User(target.UserUID).Client_ForceDisconnect(
                "Votre clé secrète a été régénérée depuis Ashfall Connect.");
        }
        catch (Exception ex)
        {
            // Best effort : la clé est déjà changée, une éjection ratée ne doit pas faire échouer l'appel.
            _logger.LogWarning(ex, "Éjection du client échouée après régénération pour {uid}", target.UserUID);
        }

        return Ok(new RegenerateKeyResponse(target.UserUID, secretKey));
    }

    public sealed record McdfShareDto(
        Guid Id, string Description, DateTime CreatedUtc, DateTime? UpdatedUtc, DateTime? ExpiresAtUtc,
        int DownloadCount, long DataSize, string OwnerUid, string? OwnerAlias,
        IEnumerable<string> AllowedIndividuals, IEnumerable<string> AllowedSyncshells);

    /// <summary>
    /// MCDF déposés par ce compte (UID secondaires compris). Le contenu chiffré n'est jamais
    /// exposé : Connect ne manipule que les métadonnées.
    /// </summary>
    [HttpGet("mcdf-shares/{uid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> GetMcdfShares(string uid, CancellationToken ct)
    {
        var auths = await ResolveManagedAuthsAsync(uid, ct);
        if (auths.Count == 0) return NotFound(new { error = "unknown_uid" });

        var uids = auths.Select(a => a.UserUID).ToList();

        var shares = await _db.McdfShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => uids.Contains(s.OwnerUID))
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(ct);

        return Ok(shares.Select(s => new McdfShareDto(
            s.Id, s.Description, s.CreatedUtc, s.UpdatedUtc, s.ExpiresAtUtc,
            s.DownloadCount, s.CipherData.LongLength, s.OwnerUID, s.Owner?.Alias,
            s.AllowedIndividuals.Select(i => i.AllowedIndividualUid).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            s.AllowedSyncshells.Select(g => g.AllowedGroupGid).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))));
    }

    public sealed record McdfShareUpdateRequest(
        string? OwnerUid, string? Description, DateTime? ExpiresAtUtc,
        List<string>? AllowedIndividuals, List<string>? AllowedSyncshells);

    private const int MaxMcdfDescriptionLength = 200;

    [HttpPut("mcdf-shares/{shareId:guid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> UpdateMcdfShare(Guid shareId, [FromBody] McdfShareUpdateRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.OwnerUid))
            return BadRequest(new { error = "invalid_request" });

        var (share, error) = await ResolveOwnedShareAsync(shareId, body.OwnerUid, ct);
        if (error is not null) return error;

        var description = (body.Description ?? string.Empty).Trim();
        if (description.Length > MaxMcdfDescriptionLength)
            description = description[..MaxMcdfDescriptionLength];

        share!.Description = description;
        share.ExpiresAtUtc = body.ExpiresAtUtc;
        share.UpdatedUtc = DateTime.UtcNow;

        // Mêmes normalisations que le hub (UID et GID en majuscules, dédoublonnés) pour que les
        // droits d'accès posés depuis le site et depuis le plugin se comparent à l'identique.
        var individuals = (body.AllowedIndividuals ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var syncshells = (body.AllowedSyncshells ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        share.AllowedIndividuals.Clear();
        foreach (var individualUid in individuals)
            share.AllowedIndividuals.Add(new MareSynchronosShared.Models.McdfShareAllowedUser { ShareId = share.Id, AllowedIndividualUid = individualUid });

        share.AllowedSyncshells.Clear();
        foreach (var gid in syncshells)
            share.AllowedSyncshells.Add(new MareSynchronosShared.Models.McdfShareAllowedGroup { ShareId = share.Id, AllowedGroupGid = gid });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("MCDF {shareId} mis à jour depuis Connect", shareId);
        return NoContent();
    }

    public sealed record McdfShareDeleteRequest(string? OwnerUid);

    [HttpDelete("mcdf-shares/{shareId:guid}")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> DeleteMcdfShare(Guid shareId, [FromQuery] string? ownerUid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerUid))
            return BadRequest(new { error = "invalid_request" });

        var (share, error) = await ResolveOwnedShareAsync(shareId, ownerUid, ct);
        if (error is not null) return error;

        _db.McdfShares.Remove(share!);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("MCDF {shareId} supprimé depuis Connect", shareId);
        return NoContent();
    }

    /// <summary>
    /// Charge un MCDF en vérifiant qu'il appartient bien à <paramref name="ownerUid"/> ou à l'un
    /// de ses UID secondaires. Connect a déjà validé que ownerUid est lié au compte appelant.
    /// </summary>
    private async Task<(MareSynchronosShared.Models.McdfShare? share, IActionResult? error)> ResolveOwnedShareAsync(
        Guid shareId, string ownerUid, CancellationToken ct)
    {
        var share = await _db.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == shareId, ct);
        if (share is null) return (null, NotFound(new { error = "unknown_share" }));

        var auths = await ResolveManagedAuthsAsync(ownerUid, ct);
        if (!auths.Any(a => string.Equals(a.UserUID, share.OwnerUID, StringComparison.Ordinal)))
        {
            _logger.LogWarning("Accès MCDF refusé : {owner} ne possède pas {shareId}", ownerUid, shareId);
            return (null, StatusCode(StatusCodes.Status403Forbidden, new { error = "not_owner" }));
        }

        return (share, null);
    }

    public sealed record BroadcastRequest(string? Message, string? Severity);

    private const int MaxBroadcastLength = 800;

    /// <summary>
    /// Annonce diffusée dans le chat de tous les joueurs connectés (redémarrage, maintenance).
    /// Le contrôle d'accès nominatif est fait côté Ashfall Connect ; ici on ne fait confiance
    /// qu'au service token Connect (policy ConnectIncoming).
    /// </summary>
    [HttpPost("broadcast")]
    [Authorize(Policy = "ConnectIncoming")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "empty_message" });

        if (!Enum.TryParse<UmbraSync.API.Data.Enum.MessageSeverity>(body.Severity ?? nameof(UmbraSync.API.Data.Enum.MessageSeverity.Warning), ignoreCase: true, out var severity))
            severity = UmbraSync.API.Data.Enum.MessageSeverity.Warning;

        var message = body.Message.Trim();
        if (message.Length > MaxBroadcastLength)
            message = message[..MaxBroadcastLength];

        _logger.LogInformation("Broadcast Connect ({severity}) : {message}", severity, message);

        await _hub.Clients.All.Client_ReceiveBroadcast(new UmbraSync.API.Dto.BroadcastMessageDto
        {
            Severity = severity,
            Message = message,
        });

        return Ok(new { ok = true });
    }

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