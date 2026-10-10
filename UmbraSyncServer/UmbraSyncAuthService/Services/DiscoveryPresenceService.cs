using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MareSynchronosAuthService.Services.Discovery;

namespace MareSynchronosAuthService.Services;

public class DiscoveryPresenceService : IHostedService, IDisposable
{
    private readonly ILogger<DiscoveryPresenceService> _logger;
    private readonly IDiscoveryPresenceStore _store;
    private readonly TimeSpan _presenceTtl = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _tokenTtl = TimeSpan.FromMinutes(2);

    public DiscoveryPresenceService(ILogger<DiscoveryPresenceService> logger, IDiscoveryPresenceStore store)
    {
        _logger = logger;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Publish(string uid, IEnumerable<string> hashes, string? displayName = null, bool allowRequests = true)
    {
        _store.Publish(uid, hashes, displayName, allowRequests);
        _logger.LogDebug("Discovery presence published for {uid} with {n} hashes", uid, hashes.Count());
    }

    public void Unpublish(string uid)
    {
        _store.Unpublish(uid);
        _logger.LogDebug("Discovery presence unpublished for {uid}", uid);
    }

    public (bool Found, string? Token, string TargetUid, string? DisplayName) TryMatchAndIssueToken(string requesterUid, string hash)
    {
        var res = _store.TryMatchAndIssueToken(requesterUid, hash);
        return (res.Found, res.Token, res.TargetUid, res.DisplayName);
    }

    public bool ConsumeToken(string token, string requesterUid, out string targetUid)
    {
        return _store.ConsumeToken(token, requesterUid, out targetUid);
    }

    public string? GetPublishedDisplayName(string uid)
    {
        return _store.GetPublishedDisplayName(uid);
    }

    public void Dispose()
    {
        (_store as IDisposable)?.Dispose();
    }
}
