using System.Buffers.Binary;

namespace MareSynchronosServer.Utils;

public static class ImageMetadataStripper
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> IccProfileId => "ICC_PROFILE\0"u8;

    public static byte[] Strip(byte[] image)
    {
        if (image.AsSpan().StartsWith(PngSignature)) return StripPng(image) ?? image;
        if (image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8) return StripJpeg(image) ?? image;
        return image;
    }

    // tEXt, zTXt, iTXt : texte libre (auteur, logiciel, commentaires) ; eXIf : EXIF ; tIME : date de modification.
    private static bool IsPngMetadataChunk(ReadOnlySpan<byte> type)
        => type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)
           || type.SequenceEqual("eXIf"u8) || type.SequenceEqual("tIME"u8);

    private static byte[] StripPng(byte[] png)
    {
        using var output = new MemoryStream(png.Length);
        output.Write(png, 0, PngSignature.Length);
        var pos = PngSignature.Length;
        var removed = false;

        while (pos + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            if (length > (uint)(png.Length - pos - 12)) return null;

            var chunkSize = 12 + (int)length;
            var type = png.AsSpan(pos + 4, 4);
            var isEnd = type.SequenceEqual("IEND"u8);
            if (IsPngMetadataChunk(type)) removed = true;
            else output.Write(png, pos, chunkSize);
            pos += chunkSize;

            if (isEnd)
            {
                // Rien de légitime après IEND : des données ajoutées en fin de fichier partent aussi.
                if (pos < png.Length) removed = true;
                return removed ? output.ToArray() : png;
            }
        }

        return null;
    }
    
    private static bool IsJpegMetadataSegment(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE1 or (>= 0xE3 and <= 0xED) or 0xEF or 0xFE => true,
        0xE2 => !payload.StartsWith(IccProfileId),
        _ => false,
    };

    private static byte[] StripJpeg(byte[] jpeg)
    {
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var pos = 2;
        var removed = false;

        while (pos < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return null;
            while (pos < jpeg.Length && jpeg[pos] == 0xFF) pos++;
            if (pos >= jpeg.Length) return null;
            var marker = jpeg[pos++];

            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                // Après la fin de l'image : images secondaires (MPF), miniatures, données ajoutées. Retirées aussi.
                if (pos < jpeg.Length) removed = true;
                return removed ? output.ToArray() : jpeg;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write([0xFF, marker]);
                continue;
            }

            if (pos + 2 > jpeg.Length) return null;
            var length = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (length < 2 || pos + length > jpeg.Length) return null;

            if (IsJpegMetadataSegment(marker, jpeg.AsSpan(pos + 2, length - 2))) removed = true;
            else output.Write(jpeg, pos - 2, length + 2);
            pos += length;

            if (marker == 0xDA)
            {
                var dataStart = pos;
                pos = SkipEntropyCodedData(jpeg, pos);
                if (pos < 0) return null;
                output.Write(jpeg, dataStart, pos - dataStart);
            }
        }

        return null;
    }


    private static int SkipEntropyCodedData(byte[] jpeg, int pos)
    {
        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            var next = jpeg[pos + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7) pos += 2;
            else if (next == 0xFF) pos++;
            else return pos;
        }

        return -1;
    }
}
