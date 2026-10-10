using MareSynchronosShared.Metrics;

namespace MareSynchronosServer.Services;

// Cache des destinataires autorisés par émetteur, pour éviter de recalculer les paires à chaque push.
// Un utilisateur peut avoir plusieurs connexions (reconnexion avant la fin de l'ancienne) : l'entrée est
// comptée par référence et n'est libérée qu'à la dernière déconnexion.
// Toute perte de droit (retrait de paire, pause, exclusion de syncshell) doit appeler InvalidateUsers,
// sinon l'ancien destinataire continue de recevoir les données jusqu'à expiration du cache.
public class OnlineSyncedPairCacheService
{
    private readonly Dictionary<string, PairCacheEntry> _userCaches = new(StringComparer.Ordinal);
    private readonly Lock _cachesLock = new();
    private readonly ILogger<OnlineSyncedPairCacheService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMetrics _mareMetrics;

    public OnlineSyncedPairCacheService(
        ILogger<OnlineSyncedPairCacheService> logger,
        ILoggerFactory loggerFactory,
        MareMetrics mareMetrics)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _mareMetrics = mareMetrics;
    }

    public Task InitPlayer(string userUid)
    {
        lock (_cachesLock)
        {
            var entry = GetOrCreateEntry(userUid);
            entry.RefCount++;
        }

        return Task.CompletedTask;
    }

    public Task DisposePlayer(string userUid)
    {
        lock (_cachesLock)
        {
            if (!_userCaches.TryGetValue(userUid, out var entry)) return Task.CompletedTask;

            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                RemoveEntry(userUid, entry);
            }
        }

        return Task.CompletedTask;
    }

    // Suppression immédiate, quel que soit le nombre de connexions (suppression de compte).
    public Task RemovePlayer(string userUid)
    {
        lock (_cachesLock)
        {
            if (_userCaches.TryGetValue(userUid, out var entry))
            {
                RemoveEntry(userUid, entry);
            }
        }

        return Task.CompletedTask;
    }

    // Vide le cache des utilisateurs donnés : leur prochain push recalcule les destinataires depuis la base.
    // À appeler avec les deux côtés d'une relation qui perd un droit (A et B, ou le membre exclu et toute la syncshell).
    public void InvalidateUsers(IEnumerable<string> userUids)
    {
        int invalidated = 0;
        lock (_cachesLock)
        {
            foreach (var uid in userUids.Distinct(StringComparer.Ordinal))
            {
                if (_userCaches.TryGetValue(uid, out var entry))
                {
                    entry.Cache.Clear();
                    invalidated++;
                }
            }
        }

        if (invalidated > 0)
            _logger.LogDebug("PairCache:Invalidate count={Count}", invalidated);
    }

    public Task<bool> AreAllPlayersCached(string senderUid, List<string> recipientUids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PairCache cache;
        lock (_cachesLock)
        {
            cache = GetOrCreateEntry(senderUid).Cache;
        }

        return Task.FromResult(cache.AreAllPlayersCached(recipientUids));
    }

    public Task CachePlayers(string senderUid, List<string> validPairUids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PairCache cache;
        lock (_cachesLock)
        {
            cache = GetOrCreateEntry(senderUid).Cache;
        }

        cache.CachePlayers(validPairUids);
        return Task.CompletedTask;
    }

    public int GetCachedUserCount()
    {
        lock (_cachesLock)
        {
            return _userCaches.Count;
        }
    }

    // Appelé sous _cachesLock. Une entrée créée par un push sans InitPlayer préalable a RefCount = 0
    // et sera libérée par le prochain DisposePlayer.
    private PairCacheEntry GetOrCreateEntry(string userUid)
    {
        if (!_userCaches.TryGetValue(userUid, out var entry))
        {
            _logger.LogDebug("PairCache:Init {UserUid}", userUid);
            entry = new PairCacheEntry(new PairCache(_loggerFactory.CreateLogger<PairCache>(), userUid, _mareMetrics));
            _userCaches[userUid] = entry;
            _mareMetrics.IncGauge(MetricsAPI.GaugePairCacheUsers);
        }

        return entry;
    }

    // Appelé sous _cachesLock.
    private void RemoveEntry(string userUid, PairCacheEntry entry)
    {
        _userCaches.Remove(userUid);
        _logger.LogDebug("PairCache:Dispose {UserUid}", userUid);
        entry.Cache.Clear();
        _mareMetrics.DecGauge(MetricsAPI.GaugePairCacheUsers);
    }

    private sealed class PairCacheEntry(PairCache cache)
    {
        public PairCache Cache { get; } = cache;
        public int RefCount { get; set; }
    }

    private sealed class PairCache
    {
        private readonly ILogger<PairCache> _logger;
        private readonly string _ownerUid;
        private readonly MareMetrics _metrics;
        private readonly Dictionary<string, DateTime> _cachedPairs = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(60);

        public PairCache(ILogger<PairCache> logger, string ownerUid, MareMetrics metrics)
        {
            _logger = logger;
            _ownerUid = ownerUid;
            _metrics = metrics;
        }

        public bool AreAllPlayersCached(List<string> uids)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var allCached = uids.TrueForAll(uid =>
                    _cachedPairs.TryGetValue(uid, out var expiry) && expiry > now);

                _logger.LogDebug("PairCache:Check {Owner} recipients={Count} cached={Cached}",
                    _ownerUid, uids.Count, allCached);

                if (allCached)
                    _metrics.IncCounter(MetricsAPI.CounterPairCacheHit);
                else
                    _metrics.IncCounter(MetricsAPI.CounterPairCacheMiss);

                return allCached;
            }
        }

        public void CachePlayers(List<string> uids)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var expiry = now.Add(CacheDuration);
                var newEntries = 0;

                foreach (var uid in uids)
                {
                    if (!_cachedPairs.ContainsKey(uid))
                        newEntries++;

                    _cachedPairs[uid] = expiry;
                }

                _logger.LogDebug("PairCache:Update {Owner} total={Total} new={New}",
                    _ownerUid, uids.Count, newEntries);

                _metrics.IncGauge(MetricsAPI.GaugePairCacheEntries, newEntries);

                var expiredKeys = _cachedPairs
                    .Where(kvp => kvp.Value < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (expiredKeys.Count > 0)
                {
                    foreach (var key in expiredKeys)
                    {
                        _cachedPairs.Remove(key);
                    }
                    _metrics.DecGauge(MetricsAPI.GaugePairCacheEntries, expiredKeys.Count);

                    _logger.LogDebug("PairCache:Cleanup {Owner} removed={Removed}",
                        _ownerUid, expiredKeys.Count);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _metrics.DecGauge(MetricsAPI.GaugePairCacheEntries, _cachedPairs.Count);
                _cachedPairs.Clear();
            }
        }
    }
}
