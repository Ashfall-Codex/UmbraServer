using Blake3;

namespace MareSynchronosShared.Utils;

public enum FileHashAlgorithm
{
    Sha1,
    Blake3,
}

public static class FileHash
{
    public const int Sha1Length = 40;
    public const int Blake3Length = 64;

    public static bool TryDetect(ReadOnlySpan<char> hash, out FileHashAlgorithm algorithm)
    {
        algorithm = hash.Length == Blake3Length ? FileHashAlgorithm.Blake3 : FileHashAlgorithm.Sha1;
        return (hash.Length == Sha1Length || hash.Length == Blake3Length) && IsHex(hash);
    }

    // Hashs que le serveur accepte aujourd'hui : routes de fichiers, stockage, nettoyage, données poussées.
    // Reste SHA-1 tant que les clients n'envoient pas BLAKE3, et c'est le seul endroit à élargir à la bascule.
    // Côté hub, accepter 64 caractères avant l'heure relaierait des hashs que les anciens clients ne savent
    // pas télécharger.
    public static bool IsAccepted(ReadOnlySpan<char> hash)
        => TryDetect(hash, out var algorithm) && algorithm == FileHashAlgorithm.Sha1;

    public static bool IsBlake3(ReadOnlySpan<char> hash)
        => TryDetect(hash, out var algorithm) && algorithm == FileHashAlgorithm.Blake3;

    public static string ComputeBlake3(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        using var hasher = Hasher.New();
        hasher.Update(data);
        hasher.Finalize(hash);
        return Convert.ToHexString(hash);
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }
}
