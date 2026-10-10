using StackExchange.Redis;

#nullable enable

namespace MareSynchronosShared.Utils;

/// <summary>
/// Limite de débit par UID à fenêtre fixe (compteur Redis), avec un anti-rafale optionnel.
/// Partagée entre toutes les instances qui utilisent le même Redis.
/// </summary>
public static class RedisRateLimiter
{
    /// <returns>Vrai si l'action est autorisée.</returns>
    public static async Task<bool> TryAcquireAsync(IDatabase db, string scope, string uid, int maxPerWindow, TimeSpan window, TimeSpan? burst = null)
    {
        if (burst is { } burstWindow
            && !await db.StringSetAsync($"rl:{scope}:burst:{uid}", "1", burstWindow, When.NotExists).ConfigureAwait(false))
        {
            return false;
        }

        var key = $"rl:{scope}:{uid}";
        var count = await db.StringIncrementAsync(key).ConfigureAwait(false);
        // Pose (ou repose, si un EXPIRE a été perdu) l'échéance de la fenêtre
        if (count == 1 || await db.KeyTimeToLiveAsync(key).ConfigureAwait(false) == null)
            await db.KeyExpireAsync(key, window).ConfigureAwait(false);

        return count <= maxPerWindow;
    }
}
