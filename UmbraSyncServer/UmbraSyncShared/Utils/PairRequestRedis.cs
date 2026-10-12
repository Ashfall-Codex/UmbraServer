using StackExchange.Redis;

#nullable enable

namespace MareSynchronosShared.Utils;

/// <summary>
/// Demandes d'appairage en attente, partagées entre le hub et le relais de découverte.
/// <c>pairreq:{from}:{to}</c> existe tant que <c>from</c> attend la réponse de <c>to</c> ;
/// <c>pairreq:done:{from}:{to}</c> garde la trace d'une demande acceptée, pour valider les relais d'acceptation.
/// </summary>
public static class PairRequestRedis
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    private static string PendingKey(string fromUid, string toUid) => $"pairreq:{fromUid}:{toUid}";
    private static string DoneKey(string fromUid, string toUid) => $"pairreq:done:{fromUid}:{toUid}";

    public static Task RegisterAsync(IDatabase db, string fromUid, string toUid)
        => db.StringSetAsync(PendingKey(fromUid, toUid), "1", PendingTtl);

    public static Task<bool> IsPendingAsync(IDatabase db, string fromUid, string toUid)
        => db.KeyExistsAsync(PendingKey(fromUid, toUid));

    /// <summary>Consomme la demande en attente ; vrai si elle existait.</summary>
    public static async Task<bool> ConsumeAsync(IDatabase db, string fromUid, string toUid)
    {
        if (!await db.KeyDeleteAsync(PendingKey(fromUid, toUid)).ConfigureAwait(false)) return false;
        await db.StringSetAsync(DoneKey(fromUid, toUid), "1", PendingTtl).ConfigureAwait(false);
        return true;
    }

    /// <summary>Vrai si <paramref name="fromUid"/> a demandé <paramref name="toUid"/> récemment (en attente ou acceptée).</summary>
    public static async Task<bool> WasRequestedAsync(IDatabase db, string fromUid, string toUid)
    {
        return await db.KeyExistsAsync(PendingKey(fromUid, toUid)).ConfigureAwait(false)
            || await db.KeyExistsAsync(DoneKey(fromUid, toUid)).ConfigureAwait(false);
    }

    // Refus des demandes par UID, reposé par le client à chaque connexion : l'échéance purge les comptes abandonnés
    public static readonly TimeSpan OptOutTtl = TimeSpan.FromDays(30);

    private static string OptOutKey(string uid) => $"pairreq:optout:{uid}";

    public static Task SetRequestsAllowedAsync(IDatabase db, string uid, bool allowed)
        => allowed ? db.KeyDeleteAsync(OptOutKey(uid)) : db.StringSetAsync(OptOutKey(uid), "1", OptOutTtl);

    public static Task<bool> RefusesRequestsAsync(IDatabase db, string uid)
        => db.KeyExistsAsync(OptOutKey(uid));

    public static Task ClearBetweenAsync(IDatabase db, string uidA, string uidB)
        => db.KeyDeleteAsync(
        [
            PendingKey(uidA, uidB), PendingKey(uidB, uidA),
            DoneKey(uidA, uidB), DoneKey(uidB, uidA),
        ]);
}
