using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosShared.Utils;

public static class UserBlockQueries
{
    /// <summary>Vrai si l'un des deux utilisateurs a bloqué l'autre.</summary>
    public static Task<bool> IsBlockedEitherWayAsync(MareDbContext db, string uidA, string uidB, CancellationToken ct = default)
        => db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.UserUID == uidA && b.BlockedUserUID == uidB) || (b.UserUID == uidB && b.BlockedUserUID == uidA), ct);
}
