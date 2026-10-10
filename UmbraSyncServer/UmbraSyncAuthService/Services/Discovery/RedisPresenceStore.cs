using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MareSynchronosAuthService.Services.Discovery;

public sealed class RedisPresenceStore : IDiscoveryPresenceStore
{
    private readonly ILogger<RedisPresenceStore> _logger;
    private readonly IDatabase _db;
    private readonly TimeSpan _presenceTtl;
    private readonly TimeSpan _tokenTtl;
    private readonly JsonSerializerOptions _jsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RedisPresenceStore(ILogger<RedisPresenceStore> logger, IConnectionMultiplexer mux, TimeSpan presenceTtl, TimeSpan tokenTtl)
    {
        _logger = logger;
        _db = mux.GetDatabase();
        _presenceTtl = presenceTtl;
        _tokenTtl = tokenTtl;
    }

    public void Dispose() { }

    private static string KeyForHash(string hash) => $"nd:hash:{hash}";
    private static string KeyForToken(string token) => $"nd:token:{token}";
    private static string KeyForUidSet(string uid) => $"nd:uid:{uid}";

    public void Publish(string uid, IEnumerable<string> hashes, string? displayName = null, bool allowRequests = true)
    {
        var entries = hashes.Distinct(StringComparer.Ordinal).ToArray();
        if (entries.Length == 0) return;
        var batch = _db.CreateBatch();
        foreach (var h in entries)
        {
            var key = KeyForHash(h);
            var payload = JsonSerializer.Serialize(new Presence(uid, displayName, allowRequests), _jsonOpts);
            batch.StringSetAsync(key, payload, _presenceTtl);
            // Index this hash under the publisher uid for fast unpublish
            batch.SetAddAsync(KeyForUidSet(uid), h);
            batch.KeyExpireAsync(KeyForUidSet(uid), _presenceTtl);
        }
        batch.Execute();
        _logger.LogDebug("RedisPresenceStore: published {count} hashes", entries.Length);
    }

    public void Unpublish(string uid)
    {
        try
        {
            var setKey = KeyForUidSet(uid);
            var members = _db.SetMembers(setKey);
            if (members is { Length: > 0 })
            {
                var batch = _db.CreateBatch();
                foreach (var m in members)
                {
                    var hash = (string)m;
                    var key = KeyForHash(hash);
                    // Defensive: only delete if the hash is still owned by this uid
                    var val = _db.StringGet(key);
                    if (val.HasValue)
                    {
                        try
                        {
                            var p = JsonSerializer.Deserialize<Presence>(val.ToString());
                            if (p != null && string.Equals(p.Uid, uid, StringComparison.Ordinal))
                            {
                                batch.KeyDeleteAsync(key);
                            }
                        }
                        catch { /* ignore corrupted */ }
                    }
                }
                // Remove the uid index set itself
                batch.KeyDeleteAsync(setKey);
                batch.Execute();
            }
            else
            {
                // No index set: best-effort, just delete the set key in case it exists
                _db.KeyDelete(setKey);
            }
            _logger.LogDebug("RedisPresenceStore: unpublished all hashes for uid {uid}", uid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RedisPresenceStore: Unpublish failed for uid {uid}", uid);
        }
    }

    public (bool Found, string? Token, string TargetUid, string? DisplayName) TryMatchAndIssueToken(string requesterUid, string hash)
    {
        var key = KeyForHash(hash);
        var val = _db.StringGet(key);
        if (!val.HasValue) return (false, null, string.Empty, null);
        try
        {
            var p = JsonSerializer.Deserialize<Presence>(val.ToString());
            if (p == null || string.IsNullOrEmpty(p.Uid)) return (false, null, string.Empty, null);
            if (string.Equals(p.Uid, requesterUid, StringComparison.Ordinal)) return (false, null, string.Empty, null);

            // Pas de rafraîchissement ici : seule la publication du joueur lui-même prolonge sa présence

            // Visible but requests disabled → return without token
            if (!p.AllowRequests)
            {
                return (true, null, p.Uid, p.DisplayName);
            }

            var token = Guid.NewGuid().ToString("N");
            _db.StringSet(KeyForToken(token), JsonSerializer.Serialize(new TokenInfo(p.Uid, requesterUid), _jsonOpts), _tokenTtl);
            return (true, token, p.Uid, p.DisplayName);
        }
        catch
        {
            return (false, null, string.Empty, null);
        }
    }

    public bool ConsumeToken(string token, string requesterUid, out string targetUid)
    {
        targetUid = string.Empty;
        var key = KeyForToken(token);
        var val = _db.StringGet(key);
        if (!val.HasValue) return false;
        // Seul l'appel qui supprime la clé utilise le jeton : deux requêtes simultanées ne peuvent pas le rejouer
        if (!_db.KeyDelete(key)) return false;

        try
        {
            var info = JsonSerializer.Deserialize<TokenInfo>(val.ToString());
            if (info == null || string.IsNullOrEmpty(info.TargetUid)) return false;
            if (!string.Equals(info.RequesterUid, requesterUid, StringComparison.Ordinal)) return false;
            targetUid = info.TargetUid;
            return true;
        }
        catch (JsonException)
        {
            // Jeton émis avant la mise à jour (valeur = UID brut, sans demandeur) : refusé, le client en redemandera un
            return false;
        }
    }

    public string? GetPublishedDisplayName(string uid)
    {
        try
        {
            foreach (var member in _db.SetMembers(KeyForUidSet(uid)))
            {
                var val = _db.StringGet(KeyForHash((string)member!));
                if (!val.HasValue) continue;
                var p = JsonSerializer.Deserialize<Presence>(val.ToString());
                if (p != null && string.Equals(p.Uid, uid, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.DisplayName))
                    return p.DisplayName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RedisPresenceStore: display name lookup failed for uid {uid}", uid);
        }

        return null;
    }

    private sealed record TokenInfo(string TargetUid, string RequesterUid);

    private sealed record Presence(string Uid, string? DisplayName, bool AllowRequests);
}
