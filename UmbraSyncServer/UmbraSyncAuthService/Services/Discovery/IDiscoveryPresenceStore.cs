using System.Collections.Concurrent;

namespace MareSynchronosAuthService.Services.Discovery;

public interface IDiscoveryPresenceStore : IDisposable
{
    void Publish(string uid, IEnumerable<string> hashes, string? displayName = null, bool allowRequests = true);
    void Unpublish(string uid);
    (bool Found, string? Token, string TargetUid, string? DisplayName) TryMatchAndIssueToken(string requesterUid, string hash);
    /// <summary>Valide et consomme le jeton : usage unique, réservé à l'UID qui l'a obtenu.</summary>
    bool ConsumeToken(string token, string requesterUid, out string targetUid);
    /// <summary>Nom publié par <paramref name="uid"/> avec sa présence, s'il en a une.</summary>
    string? GetPublishedDisplayName(string uid);
}

