using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using StackExchange.Redis;

namespace MareSynchronosAuthService.Services;

public sealed class DiscoveryWellKnownProvider : BackgroundService
{
    private const string CurrentKey = "discovery:salt:current";
    private const string PreviousKey = "discovery:salt:previous";
    private const int SaltSizeBytes = 32;
    private const int MaxSaltB64Length = 128;
    private const int MaxSaltBytes = 96;
    private const int RefreshSec = 86400; // 24h
    private const int DefaultGraceHours = 24;
    private const int MaxGraceHours = 24 * 30;
    private const int DefaultSaltTtlDays = 180;
    private const int MaxSaltTtlDays = 3650;
    private const int MaxSyncAttempts = 3;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<DiscoveryWellKnownProvider> _logger;
    private readonly IConfigurationService<AuthServiceConfiguration> _configuration;
    private readonly IServiceProvider _services;
    private readonly Lock _lock = new();
    private readonly TimeSpan _gracePeriod;
    private readonly TimeSpan _saltTtl;
    private SaltEntry? _current;
    private SaltEntry? _previous;
    private bool _persistencePending;
    private long _rejectedCount;

    public DiscoveryWellKnownProvider(ILogger<DiscoveryWellKnownProvider> logger,
        IConfigurationService<AuthServiceConfiguration> configuration,
        IConfiguration appConfiguration,
        IServiceProvider services)
    {
        _logger = logger;
        _configuration = configuration;
        _services = services;
        _gracePeriod = TimeSpan.FromHours(ReadPositiveInt(appConfiguration, "NearbyDiscovery:SaltGraceHours", DefaultGraceHours, MaxGraceHours));
        _saltTtl = TimeSpan.FromDays(ReadPositiveInt(appConfiguration, "NearbyDiscovery:SaltTtlDays", DefaultSaltTtlDays, MaxSaltTtlDays));
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        var (current, _) = Snapshot();
        _logger.LogInformation("DiscoveryWellKnownProvider started. Salt expires at {exp} (grace {graceHours}h, ttl {ttlDays}d)",
            current?.ExpiresAt, _gracePeriod.TotalHours, _saltTtl.TotalDays);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // arrêt demandé
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            FlushRejectionLog();
            await SynchronizeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Discovery salt: échec inattendu du cycle de vérification, nouvelle tentative dans {minutes} min", TickInterval.TotalMinutes);
        }
    }

    private void FlushRejectionLog()
    {
        var rejected = Interlocked.Exchange(ref _rejectedCount, 0);
        if (rejected > 0)
        {
            _logger.LogWarning("{count} requêtes discovery rejetées pour sel expiré sur les {minutes} dernières minutes", rejected, TickInterval.TotalMinutes);
        }
    }

    private async Task SynchronizeAsync(CancellationToken ct)
    {
        try
        {
            var multiplexer = await Task.Run(() => _services.GetRequiredService<IConnectionMultiplexer>(), ct).ConfigureAwait(false);
            await SyncWithRedisAsync(multiplexer.GetDatabase(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleRedisFailure(ex);
        }
    }

    private async Task SyncWithRedisAsync(IDatabase db, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxSyncAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var values = await db.StringGetAsync(new RedisKey[] { CurrentKey, PreviousKey }).WaitAsync(ct).ConfigureAwait(false);
            string? currentRaw = values[0].IsNull ? null : (string?)values[0];
            string? previousRaw = values[1].IsNull ? null : (string?)values[1];

            if (currentRaw is null)
            {
                await CreateInitialSaltAsync(db, now, ct).ConfigureAwait(false);
                continue;
            }

            var current = ParseStored(currentRaw);
            if (current is null || now >= current.ExpiresAt)
            {
                await RotateInRedisAsync(db, currentRaw, current, now, ct).ConfigureAwait(false);
                continue;
            }

            var previous = ParseStored(previousRaw);
            ApplyState(current, previous is not null && now < previous.ExpiresAt ? previous : null, now);
            return;
        }

        throw new InvalidOperationException("Impossible de stabiliser le sel de discovery dans Redis après plusieurs tentatives");
    }

    private async Task CreateInitialSaltAsync(IDatabase db, DateTimeOffset now, CancellationToken ct)
    {
        _logger.LogWarning("Discovery salt absent de Redis, nouveau sel généré : les clients connectés vont recevoir DISCOVERY_SALT_EXPIRED");
        await db.StringSetAsync(CurrentKey, Serialize(NewEntry(now)), expiry: null, when: When.NotExists).WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RotateInRedisAsync(IDatabase db, string observedRaw, SaltEntry? observed, DateTimeOffset now, CancellationToken ct)
    {
        var next = NewEntry(now);
        var transaction = db.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(CurrentKey, observedRaw));
        SaltEntry? previous = null;
        if (observed is not null)
        {
            previous = new SaltEntry(observed.Salt, now + _gracePeriod);
            _ = transaction.StringSetAsync(PreviousKey, Serialize(previous), _gracePeriod);
        }
        _ = transaction.StringSetAsync(CurrentKey, Serialize(next));

        if (!await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Discovery salt: la rotation a déjà été effectuée par une autre instance");
            return;
        }

        if (previous is null)
        {
            _logger.LogWarning("Discovery salt invalide dans Redis, remplacé par un nouveau sel expirant le {exp}", next.ExpiresAt);
            return;
        }

        _logger.LogInformation("Rotation du sel de discovery : l'ancien sel reste accepté jusqu'au {previousExp}, le nouveau sel expire le {currentExp}",
            previous.ExpiresAt, next.ExpiresAt);
    }

    private void ApplyState(SaltEntry current, SaltEntry? previous, DateTimeOffset now)
    {
        bool recovered;
        lock (_lock)
        {
            recovered = _persistencePending;
            if (recovered && previous is null && _current is not null && !_current.Salt.AsSpan().SequenceEqual(current.Salt))
            {
                // sel servi aux clients pendant la panne Redis : on l'accepte encore le temps de la grâce
                previous = new SaltEntry(_current.Salt, now + _gracePeriod);
            }
            _current = current;
            _previous = previous;
            _persistencePending = false;
        }

        if (recovered)
        {
            _logger.LogInformation("Redis de nouveau joignable : sel de discovery synchronisé");
        }
    }

    private void HandleRedisFailure(Exception ex)
    {
        var now = DateTimeOffset.UtcNow;
        string situation;
        lock (_lock)
        {
            _persistencePending = true;
            if (_current is null)
            {
                _current = NewEntry(now);
                situation = "sel généré en mémoire uniquement";
            }
            else if (now >= _current.ExpiresAt)
            {
                _previous = new SaltEntry(_current.Salt, now + _gracePeriod);
                _current = NewEntry(now);
                situation = "rotation effectuée en mémoire uniquement";
            }
            else
            {
                situation = "sel en mémoire conservé";
            }
        }

        _logger.LogError(ex, "Redis injoignable pour le sel de discovery ({situation}), nouvelle tentative de persistance dans {minutes} min", situation, TickInterval.TotalMinutes);
    }

    private (SaltEntry? Current, SaltEntry? Previous) Snapshot()
    {
        lock (_lock)
        {
            return (_current, _previous);
        }
    }

    private SaltEntry NewEntry(DateTimeOffset now) => new(RandomNumberGenerator.GetBytes(SaltSizeBytes), now + _saltTtl);

    private static string Serialize(SaltEntry entry) =>
        JsonSerializer.Serialize(new StoredSalt(Convert.ToBase64String(entry.Salt), entry.ExpiresAt));

    private static SaltEntry? ParseStored(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSalt>(raw);
            if (stored is null) return null;
            Span<byte> buffer = stackalloc byte[MaxSaltBytes];
            return TryDecodeSalt(stored.SaltB64, buffer, out var length)
                ? new SaltEntry(buffer[..length].ToArray(), stored.ExpiresAt)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryDecodeSalt(string? saltB64, Span<byte> buffer, out int length)
    {
        length = 0;
        if (string.IsNullOrEmpty(saltB64) || saltB64.Length > MaxSaltB64Length) return false;
        if (!Convert.TryFromBase64String(saltB64, buffer, out length)) return false;
        return length > 0;
    }

    private int ReadPositiveInt(IConfiguration configuration, string key, int defaultValue, int maxValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= maxValue)
            return value;

        _logger.LogWarning("Valeur invalide '{value}' pour {key} (entier entre 1 et {max} attendu), valeur par défaut {default} utilisée", raw, key, maxValue, defaultValue);
        return defaultValue;
    }

    public bool IsExpired(string? providedSaltB64)
    {
        var accepted = IsAccepted(providedSaltB64);
        if (!accepted) Interlocked.Increment(ref _rejectedCount);
        return !accepted;
    }

    private bool IsAccepted(string? providedSaltB64)
    {
        Span<byte> buffer = stackalloc byte[MaxSaltBytes];
        if (!TryDecodeSalt(providedSaltB64, buffer, out var length)) return false;
        var provided = buffer[..length];

        var now = DateTimeOffset.UtcNow;
        var (current, previous) = Snapshot();

        if (current is not null && now <= current.ExpiresAt && CryptographicOperations.FixedTimeEquals(provided, current.Salt))
            return true;

        return previous is not null && now <= previous.ExpiresAt && CryptographicOperations.FixedTimeEquals(provided, previous.Salt);
    }

    public string GetWellKnownJson(string scheme, string host)
    {
        var isHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
        var wsScheme = isHttps ? "wss" : "ws";
        var httpScheme = isHttps ? "https" : "http";

        var (current, _) = Snapshot();
        var salt = current?.Salt ?? [];
        var exp = current?.ExpiresAt ?? default;

        var fallbackHubUrl = _configuration.GetValueOrDefault(nameof(AuthServiceConfiguration.FallbackHubUrl), string.Empty);

        var root = new WellKnownRoot
        {
            ApiUrl = $"{wsScheme}://{host}",
            HubUrl = $"{wsScheme}://{host}/mare",
            FallbackHubUrl = string.IsNullOrWhiteSpace(fallbackHubUrl) ? null : fallbackHubUrl,
            Features = new() { NearbyDiscovery = true },
            NearbyDiscovery = new()
            {
                Enabled = true,
                HashAlgo = "sha256",
                SaltB64 = Convert.ToBase64String(salt),
                SaltExpiresAt = exp,
                RefreshSec = RefreshSec,
                GraceSec = (int)_gracePeriod.TotalSeconds,
                Endpoints = new()
                {
                    Publish = $"{httpScheme}://{host}/discovery/publish",
                    Query = $"{httpScheme}://{host}/discovery/query",
                    Request = $"{httpScheme}://{host}/discovery/request",
                    Accept = $"{httpScheme}://{host}/discovery/acceptNotify"
                },
                Policies = new()
                {
                    MaxQueryBatch = 100,
                    MinQueryIntervalMs = 2000,
                    RateLimitPerMin = 30,
                    TokenTtlSec = 120
                }
            }
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    private sealed record SaltEntry(byte[] Salt, DateTimeOffset ExpiresAt);

    private sealed record StoredSalt(
        [property: JsonPropertyName("saltB64")] string SaltB64,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

    private sealed class WellKnownRoot
    {
        [JsonPropertyName("api_url")] public string ApiUrl { get; set; } = string.Empty;
        [JsonPropertyName("hub_url")] public string HubUrl { get; set; } = string.Empty;
        [JsonPropertyName("fallback_hub_url")] public string? FallbackHubUrl { get; set; }
        [JsonPropertyName("skip_negotiation")] public bool SkipNegotiation { get; set; } = false;
        [JsonPropertyName("transports")] public string[] Transports { get; set; } = new[] { "websockets", "serversentevents", "longpolling" };
        [JsonPropertyName("features")] public Features Features { get; set; } = new();
        [JsonPropertyName("nearby_discovery")] public Nearby NearbyDiscovery { get; set; } = new();
    }

    private sealed class Features
    {
        [JsonPropertyName("nearby_discovery")] public bool NearbyDiscovery { get; set; }
    }

    private sealed class Nearby
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("hash_algo")] public string HashAlgo { get; set; } = "sha256";
        [JsonPropertyName("salt_b64")] public string SaltB64 { get; set; } = string.Empty;
        [JsonPropertyName("salt_expires_at")] public DateTimeOffset SaltExpiresAt { get; set; }
        [JsonPropertyName("refresh_sec")] public int RefreshSec { get; set; }
        [JsonPropertyName("grace_sec")] public int GraceSec { get; set; }
        [JsonPropertyName("endpoints")] public Endpoints Endpoints { get; set; } = new();
        [JsonPropertyName("policies")] public Policies Policies { get; set; } = new();
    }

    private sealed class Endpoints
    {
        [JsonPropertyName("publish")] public string? Publish { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
        [JsonPropertyName("request")] public string? Request { get; set; }
        [JsonPropertyName("accept")] public string? Accept { get; set; }
    }

    private sealed class Policies
    {
        [JsonPropertyName("max_query_batch")] public int MaxQueryBatch { get; set; }
        [JsonPropertyName("min_query_interval_ms")] public int MinQueryIntervalMs { get; set; }
        [JsonPropertyName("rate_limit_per_min")] public int RateLimitPerMin { get; set; }
        [JsonPropertyName("token_ttl_sec")] public int TokenTtlSec { get; set; }
    }
}
