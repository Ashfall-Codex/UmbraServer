using System;
using System.Collections.Generic;
using MareSynchronosShared.Models;

namespace MareSynchronosShared.Utils;

public static class Bc7TextureClassifier
{
    // Retourne null si aucun GamePath n'est une texture (.tex/.atex) → pas de conversion à prévoir.
    public static Bc7TextureRole? Classify(IReadOnlyCollection<string> gamePaths)
    {
        if (gamePaths == null) return null;

        bool isTexture = false;
        var role = Bc7TextureRole.Unknown;
        foreach (var gp in gamePaths)
        {
            if (string.IsNullOrEmpty(gp)) continue;
            if (!gp.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                && !gp.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)) continue;

            isTexture = true;
            var r = RoleFromPath(gp);
            if (r == Bc7TextureRole.Normal) return Bc7TextureRole.Normal; // priorité protection
            if (role == Bc7TextureRole.Unknown) role = r;
        }

        return isTexture ? role : null;
    }

    private static Bc7TextureRole RoleFromPath(string gamePath)
    {
        var name = gamePath.ToLowerInvariant();
        if (name.Contains("_n.", StringComparison.Ordinal) || name.Contains("_norm", StringComparison.Ordinal) || name.Contains("normal", StringComparison.Ordinal))
            return Bc7TextureRole.Normal;
        if (name.Contains("_d.", StringComparison.Ordinal) || name.Contains("_diff", StringComparison.Ordinal) || name.Contains("_base", StringComparison.Ordinal) || name.Contains("basecolor", StringComparison.Ordinal))
            return Bc7TextureRole.Diffuse;
        if (name.Contains("_m.", StringComparison.Ordinal) || name.Contains("_mask", StringComparison.Ordinal) || name.Contains("_mult", StringComparison.Ordinal) || name.Contains("_s.", StringComparison.Ordinal) || name.Contains("_spec", StringComparison.Ordinal))
            return Bc7TextureRole.Mask;
        return Bc7TextureRole.Other;
    }
}
