using System.Text.Json.Serialization;
using MareSynchronosAuthService.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace MareSynchronosAuthService.Controllers;

[Authorize]
[ApiController]
[Route("discovery")]
public class DiscoveryController : Controller
{
    public const string RelayHttpClientName = "discovery-relay";

    // Le client interroge toutes les 2 s par lots de 100 hashes (politique publiée par le well-known) :
    // 60 requêtes/min laissent passer deux lots par cycle en lieu bondé, pas une énumération massive.
    private const int MaxHashesPerQuery = 100;
    private const int QueryMaxPerMinute = 60;
    private const int RequestMaxPerMinute = 10;
    private static readonly TimeSpan RequestBurst = TimeSpan.FromSeconds(3);
    private const int AcceptMaxPerMinute = 20;

    private readonly DiscoveryWellKnownProvider _provider;
    private readonly DiscoveryPresenceService _presence;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ServerTokenGenerator _serverTokenGenerator;
    private readonly ILogger<DiscoveryController> _logger;

    public DiscoveryController(DiscoveryWellKnownProvider provider, DiscoveryPresenceService presence, IConfiguration configuration,
        IHttpClientFactory httpClientFactory, IConnectionMultiplexer redis, ServerTokenGenerator serverTokenGenerator, ILogger<DiscoveryController> logger)
    {
        _provider = provider;
        _presence = presence;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _serverTokenGenerator = serverTokenGenerator;
        _logger = logger;
    }

    private string CallerUid => User?.Claims?.FirstOrDefault(c => c.Type == MareClaimTypes.Uid)?.Value ?? string.Empty;

    private IActionResult TooManyRequests() => StatusCode(StatusCodes.Status429TooManyRequests, new { code = "RATE_LIMITED" });

    public sealed class QueryRequest
    {
        [JsonPropertyName("hashes")] public string[] Hashes { get; set; } = Array.Empty<string>();
        [JsonPropertyName("salt")] public string SaltB64 { get; set; } = string.Empty;
    }

    public sealed class QueryResponseEntry
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("uid")] public string Uid { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }

    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] QueryRequest req)
    {
        if (req == null) return BadRequest();
        if (_provider.IsExpired(req.SaltB64))
        {
            return BadRequest(new { code = "DISCOVERY_SALT_EXPIRED" });
        }

        var uid = CallerUid;
        if (string.IsNullOrEmpty(uid) || req.Hashes == null || req.Hashes.Length == 0)
            return Json(Array.Empty<QueryResponseEntry>());

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.GetDatabase(), "ndquery", uid, QueryMaxPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            return TooManyRequests();

        List<QueryResponseEntry> matches = new();
        foreach (var h in req.Hashes.Distinct(StringComparer.Ordinal).Take(MaxHashesPerQuery))
        {
            var (found, token, targetUid, displayName) = _presence.TryMatchAndIssueToken(uid, h);
            if (found)
            {
                matches.Add(new QueryResponseEntry { Hash = h, Token = token, Uid = targetUid, DisplayName = displayName });
            }
        }

        return Json(matches);
    }

    // Le client envoie aussi « targetUid » et « displayName » : ils sont ignorés. La cible vient du jeton,
    // le nom affiché est résolu ici, jamais pris tel que déclaré par l'émetteur.
    public sealed class RequestDto
    {
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestPair([FromBody] RequestDto req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrEmpty(req.Token)) return BadRequest(new { code = "TOKEN_REQUIRED" });

        var fromUid = CallerUid;
        if (string.IsNullOrEmpty(fromUid)) return Unauthorized();

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.GetDatabase(), "ndrequest", fromUid, RequestMaxPerMinute, TimeSpan.FromMinutes(1), RequestBurst).ConfigureAwait(false))
            return TooManyRequests();

        if (!_presence.ConsumeToken(req.Token, fromUid, out var targetUid))
            return BadRequest(new { code = "INVALID_TOKEN" });

        var fromAlias = ResolveDisplayName(fromUid);
        return await RelayAsync("/main/discovery/notifyRequest", new { targetUid, fromUid, fromAlias }, ct).ConfigureAwait(false);
    }

    public sealed class AcceptNotifyDto
    {
        [JsonPropertyName("targetUid")] public string TargetUid { get; set; } = string.Empty;
    }

    // Relais d'acceptation des anciens clients : validé côté hub (il faut une vraie demande de la cible).
    [HttpPost("acceptNotify")]
    public async Task<IActionResult> AcceptNotify([FromBody] AcceptNotifyDto req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrEmpty(req.TargetUid)) return BadRequest();

        var fromUid = CallerUid;
        if (string.IsNullOrEmpty(fromUid)) return Unauthorized();

        if (!await RedisRateLimiter.TryAcquireAsync(_redis.GetDatabase(), "ndaccept", fromUid, AcceptMaxPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            return TooManyRequests();

        return await RelayAsync("/main/discovery/notifyAccept", new { targetUid = req.TargetUid, fromUid }, ct).ConfigureAwait(false);
    }

    // Nom publié avec la présence, sinon alias, sinon UID
    private string ResolveDisplayName(string uid)
    {
        var published = _presence.GetPublishedDisplayName(uid);
        if (!string.IsNullOrWhiteSpace(published)) return published;

        var alias = User?.Claims?.FirstOrDefault(c => c.Type == MareClaimTypes.Alias)?.Value;
        return string.IsNullOrWhiteSpace(alias) ? uid : alias;
    }

    private async Task<IActionResult> RelayAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient(RelayHttpClientName);
            // Préférer l'URL du main configurée ; sinon l'hôte entrant (nginx)
            var configuredBase = _configuration.GetValue<string>("NearbyDiscovery:MainBaseUrl");
            var baseUrl = string.IsNullOrWhiteSpace(configuredBase) ? $"{Request.Scheme}://{Request.Host.Value}" : configuredBase;
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), path))
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _serverTokenGenerator.Token);

            using var resp = await http.SendAsync(message, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return Accepted();

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) return Forbid();

            _logger.LogWarning("Discovery relay {path} failed: {code} {reason}", path, (int)resp.StatusCode, resp.ReasonPhrase);
            return StatusCode(StatusCodes.Status502BadGateway, new { code = "RELAY_FAILED" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery relay {path} failed", path);
            return StatusCode(StatusCodes.Status502BadGateway, new { code = "RELAY_FAILED" });
        }
    }

    public sealed class PublishRequest
    {
        [JsonPropertyName("hashes")] public string[] Hashes { get; set; } = Array.Empty<string>();
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("salt")] public string SaltB64 { get; set; } = string.Empty;
        [JsonPropertyName("allowRequests")] public bool AllowRequests { get; set; } = true;
    }

    [HttpPost("disable")]
    public IActionResult Disable()
    {
        var uid = CallerUid;
        if (string.IsNullOrEmpty(uid)) return Accepted();
        _presence.Unpublish(uid);
        return Accepted();
    }

    [HttpPost("publish")]
    public IActionResult Publish([FromBody] PublishRequest req)
    {
        if (_provider.IsExpired(req.SaltB64))
        {
            return BadRequest(new { code = "DISCOVERY_SALT_EXPIRED" });
        }
        var uid = CallerUid;
        if (string.IsNullOrEmpty(uid) || req?.Hashes == null || req.Hashes.Length == 0)
            return Accepted();

        _presence.Publish(uid, req.Hashes, req.DisplayName, req.AllowRequests);
        return Accepted();
    }
}
