using System.Buffers.Binary;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;

namespace MareSynchronosStaticFilesServer.Utils;

public enum TexTranscodeResult
{
    Converted,
    SkippedAlreadyCompressed,
    SkippedUnsupportedFormat,
    SkippedTooSmall,
    Failed,
}

// Transcodage d'une texture FFXIV .tex (format non-compressé 32bpp) vers BC7.
// Conteneur .tex : header 80 octets, puis les mips concaténés.
//   @0  uint   Attribute
//   @4  uint   Format (code FFXIV)
//   @8  ushort Width
//   @10 ushort Height
//   @12 ushort Depth
//   @14 byte   MipCount (bits 0..6) + flag (bit 0x80)
//   @16 uint[3]  LodOffset   (offsets relatifs au début des données)
//   @28 uint[13] OffsetToSurface (offsets absolus fichier de chaque mip)
// Référence lecture/écriture : UmbraClient PlayerPerformanceService.ShrinkTextures.


public static class TexTranscoder
{
    private const int HeaderSize = 80;

    private const uint FormatBc7 = 0x6432;
    private const uint FormatA8R8G8B8 = 0x1450;
    private const uint FormatX8R8G8B8 = 0x1451;

    public static TexTranscodeResult TryTranscodeToBc7(byte[] tex, out byte[] result, int maxThreads = 0)
    {
        result = [];
        if (tex.Length < HeaderSize) return TexTranscodeResult.Failed;

        uint format = BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(4));
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(8));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(10));

        // On ne convertit que le 32bpp non-compressé (A8R8G8B8 / X8R8G8B8).
        // Tout ce qui est déjà block-compressé est laissé tel quel (pas de double-perte).
        
        if (IsBlockCompressed(format)) return TexTranscodeResult.SkippedAlreadyCompressed;
        if (format != FormatA8R8G8B8 && format != FormatX8R8G8B8) return TexTranscodeResult.SkippedUnsupportedFormat;
        if (width < 4 || height < 4) return TexTranscodeResult.SkippedTooSmall;

        int pixelBytes = width * height * 4;
        if (tex.Length < HeaderSize + pixelBytes) return TexTranscodeResult.Failed; // header ment sur les dimensions

        // FFXIV A8R8G8B8 est stocké en ordre B,G,R,A.
        
        var pixels = new ColorRgba32[width * height];
        var src = tex.AsSpan(HeaderSize, pixelBytes);
        bool ignoreAlpha = format == FormatX8R8G8B8;
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 4;
            byte b = src[o];
            byte g = src[o + 1];
            byte r = src[o + 2];
            byte a = ignoreAlpha ? (byte)255 : src[o + 3];
            pixels[i] = new ColorRgba32(r, g, b, a);
        }

        byte[][] mips;
        try
        {
            var encoder = new BcEncoder();
            encoder.OutputOptions.GenerateMipMaps = true;
            encoder.OutputOptions.Quality = CompressionQuality.Balanced;
            encoder.OutputOptions.Format = CompressionFormat.Bc7;
            
            // BCnEncoder parallélise en interne (défaut = tous les cœurs). On plafonne pour
            // laisser du CPU au service de fichiers en prod.
            
            if (maxThreads > 0) encoder.Options.TaskCount = maxThreads;
            var input = new ReadOnlyMemory2D<ColorRgba32>(pixels, height, width);
            mips = encoder.EncodeToRawBytes(input);
        }
        catch
        {
            return TexTranscodeResult.Failed;
        }

        if (mips.Length == 0) return TexTranscodeResult.Failed;

        result = BuildTex(tex, width, height, mips);
        return TexTranscodeResult.Converted;
    }

    private static byte[] BuildTex(byte[] source, ushort width, ushort height, byte[][] mips)
    {
        int total = HeaderSize;
        foreach (var m in mips) total += m.Length;
        var outTex = new byte[total];

        Array.Copy(source, 0, outTex, 0, HeaderSize);
        var span = outTex.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], FormatBc7);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], height);

        // MipCount @14 : conserver le flag 0x80 de la source, écrire le nb de mips généré (<= 13).
        outTex[14] = (byte)((source[14] & 0x80) | (mips.Length & 0x7F));
        outTex[15] = 0;

        // LodOffset[3] @16 : offset (relatif au début des données) du 1er mip de chaque LoD (mips 0/1/2, clampé).
        int lod1 = mips.Length > 1 ? mips[0].Length : 0;
        int lod2 = mips.Length > 2 ? mips[0].Length + mips[1].Length : lod1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)lod1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)lod2);

        // OffsetToSurface[13] @28 : offset absolu fichier de chaque mip (0 pour les slots inutilisés).
        int fileOffset = HeaderSize;
        for (int i = 0; i < 13; i++)
        {
            uint val = i < mips.Length ? (uint)fileOffset : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(span[(28 + i * 4)..], val);
            if (i < mips.Length) fileOffset += mips[i].Length;
        }

        // Données mip concaténées.
        int pos = HeaderSize;
        foreach (var m in mips)
        {
            m.CopyTo(outTex, pos);
            pos += m.Length;
        }

        return outTex;
    }

    private static bool IsBlockCompressed(uint format) => format switch
    {
        0x3420 => true, // DXT1
        0x3430 => true, // DXT3
        0x3431 => true, // DXT5
        0x6120 => true, // BC4
        0x6230 => true, // BC5
        0x6432 => true, // BC7
        _ => false,
    };
}
