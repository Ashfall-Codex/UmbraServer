using System.Collections.Concurrent;

namespace MareSynchronosAuthService.Services.Discovery;

public sealed class InMemoryPresenceStore : IDiscoveryPresenceStore
{
    private readonly ConcurrentDictionary<string, (string Uid, DateTimeOffset ExpiresAt, string? DisplayName, bool AllowRequests)> _presence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string TargetUid, string RequesterUid, DateTimeOffset ExpiresAt)> _tokens = new(StringComparer.Ordinal);
    private readonly TimeSpan _presenceTtl;
    private readonly TimeSpan _tokenTtl;
    private readonly Timer _cleanupTimer;

    public InMemoryPresenceStore(TimeSpan presenceTtl, TimeSpan tokenTtl)
    {
        _presenceTtl = presenceTtl;
        _tokenTtl = tokenTtl;
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _presence.ToArray())
        {
            if (kv.Value.ExpiresAt <= now) _presence.TryRemove(kv.Key, out _);
        }
        foreach (var kv in _tokens.ToArray())
        {
            if (kv.Value.ExpiresAt <= now) _tokens.TryRemove(kv.Key, out _);
        }
    }

    public void Publish(string uid, IEnumerable<string> hashes, string? displayName = null, bool allowRequests = true)
    {
        var exp = DateTimeOffset.UtcNow.Add(_presenceTtl);
        foreach (var h in hashes.Distinct(StringComparer.Ordinal))
        {
            _presence[h] = (uid, exp, displayName, allowRequests);
        }
    }

    public void Unpublish(string uid)
    {
        // Remove all presence hashes owned by this uid
        foreach (var kv in _presence.ToArray())
        {
            if (string.Equals(kv.Value.Uid, uid, StringComparison.Ordinal))
            {
                _presence.TryRemove(kv.Key, out _);
            }
        }
    }

    // Seule la publication du joueur lui-même prolonge sa présence : interroger quelqu'un ne le garde pas découvrable.
    public (bool Found, string? Token, string TargetUid, string? DisplayName) TryMatchAndIssueToken(string requesterUid, string hash)
    {
        if (!_presence.TryGetValue(hash, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return (false, null, string.Empty, null);

        if (string.Equals(entry.Uid, requesterUid, StringComparison.Ordinal))
            return (false, null, string.Empty, null);

        // Visible but requests disabled → no token
        if (!entry.AllowRequests)
            return (true, null, entry.Uid, entry.DisplayName);

        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = (entry.Uid, requesterUid, DateTimeOffset.UtcNow.Add(_tokenTtl));
        return (true, token, entry.Uid, entry.DisplayName);
    }

    public bool ConsumeToken(string token, string requesterUid, out string targetUid)
    {
        targetUid = string.Empty;
        // TryRemove d'abord : deux requêtes simultanées ne peuvent pas utiliser le même jeton
        if (!_tokens.TryRemove(token, out var info)) return false;
        if (info.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        if (!string.Equals(info.RequesterUid, requesterUid, StringComparison.Ordinal)) return false;

        targetUid = info.TargetUid;
        return true;
    }

    public string? GetPublishedDisplayName(string uid)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _presence)
        {
            if (string.Equals(kv.Value.Uid, uid, StringComparison.Ordinal) && kv.Value.ExpiresAt > now && !string.IsNullOrWhiteSpace(kv.Value.DisplayName))
                return kv.Value.DisplayName;
        }

        return null;
    }
}

