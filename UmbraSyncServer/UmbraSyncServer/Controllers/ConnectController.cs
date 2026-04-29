using MareSynchronosServer.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosServer.Controllers;

/// Endpoints d'intégration avec Ashfall Connect (hub d'identité fédérée).
[ApiController]
[Route("main/connect")]
public sealed class ConnectController : ControllerBase
{
    private readonly ConnectClient _connect;
    private readonly ILogger<ConnectController> _logger;

    public ConnectController(ConnectClient connect, ILogger<ConnectController> logger)
    {
        _connect = connect;
        _logger = logger;
    }

    [HttpPost("generate-link-code")]
    [Authorize(Policy = "Identified")]
    public async Task<IActionResult> GenerateLinkCode(CancellationToken ct)
    {
        if (!_connect.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "connect_not_configured" });

        var uid = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (string.IsNullOrEmpty(uid))
            return Unauthorized();

        var alias = User.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Alias, StringComparison.Ordinal))?.Value;

        try
        {
            var result = await _connect.GenerateLinkCodeAsync(uid, alias, ct);
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
}