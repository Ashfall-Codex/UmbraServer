using UmbraSync.API.SignalR;
using MareSynchronosShared.Data;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;
using System.Text.Json.Serialization;

namespace MareSynchronosServer.Controllers;

[Route("/main/discovery")]
[Authorize(Policy = "Internal")]
public class DiscoveryNotifyController : Controller
{
    private readonly ILogger<DiscoveryNotifyController> _logger;
    private readonly IHubContext<Hubs.MareHub, IMareHub> _hub;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly IRedisDatabase _redis;

    public DiscoveryNotifyController(ILogger<DiscoveryNotifyController> logger, IHubContext<Hubs.MareHub, IMareHub> hub,
        IDbContextFactory<MareDbContext> dbContextFactory, IRedisDatabase redis)
    {
        _logger = logger;
        _hub = hub;
        _dbContextFactory = dbContextFactory;
        _redis = redis;
    }

    public sealed class NotifyRequestDto
    {
        [JsonPropertyName("targetUid")] public string TargetUid { get; set; } = string.Empty;
        [JsonPropertyName("fromUid")] public string FromUid { get; set; } = string.Empty;
        // Nom résolu par le service d'authentification (présence publiée, alias ou UID), jamais celui déclaré par le client
        [JsonPropertyName("fromAlias")] public string? FromAlias { get; set; }
    }

    [HttpPost("notifyRequest")]
    public async Task<IActionResult> NotifyRequest([FromBody] NotifyRequestDto dto, CancellationToken ct)
    {
        if (dto == null || string.IsNullOrEmpty(dto.TargetUid) || string.IsNullOrEmpty(dto.FromUid)) return BadRequest();
        if (string.Equals(dto.TargetUid, dto.FromUid, StringComparison.Ordinal)) return BadRequest();

        await using (var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            // Bloqué dans un sens ou dans l'autre : on n'informe pas le demandeur, la demande n'est simplement pas livrée
            if (await UserBlockQueries.IsBlockedEitherWayAsync(db, dto.FromUid, dto.TargetUid, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Discovery notify request from {from} to {target} dropped (blocked)", dto.FromUid, dto.TargetUid);
                return Accepted();
            }
        }

        await PairRequestRedis.RegisterAsync(_redis.Database, dto.FromUid, dto.TargetUid).ConfigureAwait(false);

        _logger.LogInformation("Discovery notify request to {target} from {from}", dto.TargetUid, dto.FromUid);
        // Plus de message texte « Nearby Request » : le callback typé suffit, y compris pour les anciens clients
        await _hub.Clients.User(dto.TargetUid)
            .Client_ReceivePairRequest(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(dto.FromUid, dto.FromAlias))).ConfigureAwait(false);
        return Accepted();
    }

    public sealed class NotifyAcceptDto
    {
        [JsonPropertyName("targetUid")] public string TargetUid { get; set; } = string.Empty;
        [JsonPropertyName("fromUid")] public string FromUid { get; set; } = string.Empty;
        [JsonPropertyName("fromAlias")] public string? FromAlias { get; set; }
    }

    /// <summary>
    /// Relais d'acceptation des anciens clients. L'acceptation est désormais notifiée par le hub lui-même
    /// (Client_PairRequestAccepted dans UserAddPair) : on ne fait plus que valider, pour qu'un client ne puisse
    /// pas pousser un faux « accepté » à n'importe qui.
    /// </summary>
    [HttpPost("notifyAccept")]
    public async Task<IActionResult> NotifyAccept([FromBody] NotifyAcceptDto dto, CancellationToken ct)
    {
        if (dto == null || string.IsNullOrEmpty(dto.TargetUid) || string.IsNullOrEmpty(dto.FromUid)) return BadRequest();

        // Valide seulement si la cible avait réellement demandé l'émetteur (demande en attente ou acceptée récemment),
        // ou si elle l'a déjà ajouté (paire par UID)
        bool legit = await PairRequestRedis.WasRequestedAsync(_redis.Database, dto.TargetUid, dto.FromUid).ConfigureAwait(false);
        if (!legit)
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            legit = await db.ClientPairs.AsNoTracking()
                .AnyAsync(p => p.UserUID == dto.TargetUid && p.OtherUserUID == dto.FromUid, ct).ConfigureAwait(false);
        }

        if (!legit)
        {
            _logger.LogWarning("Discovery notify accept from {from} to {target} rejected: no matching request", dto.FromUid, dto.TargetUid);
            return Forbid();
        }

        return Accepted();
    }
}
