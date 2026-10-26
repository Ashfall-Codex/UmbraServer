using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace MareSynchronosServer.Utils;

public static class ProfileImageSanitizer
{
    private static readonly DecoderOptions DecoderOptions = new()
    {
        Configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule()),
    };

    // Renvoie l'image à enregistrer, sans ses métadonnées, ou null si elle est refusée.
    public static string Sanitize(string base64, int maxBase64Length, int maxDimension)
    {
        if (base64.Length > maxBase64Length) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var info = Image.Identify(DecoderOptions, bytes);
            if (info == null || info.Width <= 0 || info.Width > maxDimension || info.Height <= 0 || info.Height > maxDimension) return null;

            var stripped = StripMetadata(bytes, info);
            return ReferenceEquals(stripped, bytes) ? base64 : Convert.ToBase64String(stripped);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Pour une image déjà enregistrée : renvoie le tableau d'origine si rien n'est à retirer.
    public static byte[] StripMetadata(byte[] bytes)
    {
        try
        {
            return StripMetadata(bytes, Image.Identify(DecoderOptions, bytes));
        }
        catch (Exception)
        {
            return bytes;
        }
    }

    private static byte[] StripMetadata(byte[] bytes, ImageInfo original)
    {
        var stripped = ImageMetadataStripper.Strip(bytes);
        if (ReferenceEquals(stripped, bytes)) return bytes;
        try
        {
            var info = Image.Identify(DecoderOptions, stripped);
            return info.Width == original.Width && info.Height == original.Height ? stripped : bytes;
        }
        catch (Exception)
        {
            return bytes;
        }
    }
}
