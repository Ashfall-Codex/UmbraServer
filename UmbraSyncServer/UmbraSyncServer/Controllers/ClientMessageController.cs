using UmbraSync.API.Dto;
using UmbraSync.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Controllers;

[Route("/msgc")]
[Authorize(Policy = "Internal")]
public class ClientMessageController : Controller
{
    private ILogger<ClientMessageController> _logger;
    private IHubContext<MareHub, IMareHub> _hubContext;

    public ClientMessageController(ILogger<ClientMessageController> logger, IHubContext<MareHub, IMareHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    [Route("sendMessage")]
    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] ClientMessage msg)
    {
        bool hasUid = !string.IsNullOrEmpty(msg.UID);

        if (!hasUid)
        {
            _logger.LogInformation("Sending Message of severity {severity} to all online users ({length} chars)", msg.Severity, msg.Message?.Length ?? 0);
            await _hubContext.Clients.All.Client_ReceiveServerMessage(msg.Severity, msg.Message).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Sending Message of severity {severity} to user {uid} ({length} chars)", msg.Severity, msg.UID, msg.Message?.Length ?? 0);
            await _hubContext.Clients.User(msg.UID).Client_ReceiveServerMessage(msg.Severity, msg.Message).ConfigureAwait(false);
        }

        return Empty;
    }

    /// <summary>
    /// Annonce diffusée à tous les clients connectés, écrite directement dans le chat du jeu.
    /// Réservée aux annonces d'exploitation (redémarrage, maintenance) : le contrôle d'accès
    /// est fait en amont par le bot Discord, cet endpoint reste sur la policy "Internal".
    /// </summary>
    [Route("broadcast")]
    [HttpPost]
    public async Task<IActionResult> Broadcast([FromBody] ClientMessage msg)
    {
        if (msg == null || string.IsNullOrWhiteSpace(msg.Message))
            return BadRequest("Message is required");

        var message = msg.Message.Trim();
        if (message.Length > MaxBroadcastLength)
            message = message[..MaxBroadcastLength];

        _logger.LogInformation("Broadcasting message of severity {severity} to all online users ({length} chars)", msg.Severity, message.Length);

        await _hubContext.Clients.All.Client_ReceiveBroadcast(new BroadcastMessageDto
        {
            Severity = msg.Severity,
            Message = message,
        }).ConfigureAwait(false);

        return Empty;
    }

    private const int MaxBroadcastLength = 800;
}
