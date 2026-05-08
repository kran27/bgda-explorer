using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JetBlackEngineLib.Data.Textures;

/// <summary>
/// Decodes Xbox-platform BoS .tex entries.
///
///   +0x00  u16  width
///   +0x02  u16  height
///   +0x04  byte format flag 1 (0x14 / 0x44)
///   +0x05  byte format flag 2 (0x40 / 0xC0 / 0xD0)
///   +0x06..+0x37  zeros
///   +0x38..+0x437  256-entry RGBA palette (1024 bytes)
///   +0x438..end    W×H bytes of 8-bit palette indices
/// </summary>
public static class XboxTexDecoder
{
    public const int HeaderSize = 0x38;
    public const int PaletteSize = 1024;

    public static bool LooksLikeXboxTex(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + PaletteSize + 4) return false;
        var w = BinaryPrimitivesLE.ReadU16(data, 0);
        var h = BinaryPrimitivesLE.ReadU16(data, 2);
        if (w < 4 || w > 4096 || h < 4 || h > 4096) return false;
        // PS2 TEX marker: 0x80 at +0x10. Reject so we don't claim those.
        if (BinaryPrimitivesLE.ReadU32(data, 0x10) == 0x80) return false;
        // Header u32 at +0x08 is 0x38 across every shipped Xbox TEX.
        if (BinaryPrimitivesLE.ReadU32(data, 0x08) != 0x38) return false;
        return data.Length >= HeaderSize + w * h + PaletteSize;
    }

    public static WriteableBitmap? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + PaletteSize) return null;
        var w = BinaryPrimitivesLE.ReadU16(data, 0);
        var h = BinaryPrimitivesLE.ReadU16(data, 2);
        if (w == 0 || h == 0) return null;
        if (HeaderSize + w * h + PaletteSize > data.Length) return null;

        var palette = new PalEntry[256];
        for (var i = 0; i < 256; i++)
        {
            var p = HeaderSize + i * 4;
            palette[i] = new PalEntry
            {
                R = data[p + 0],
                G = data[p + 1],
                B = data[p + 2],
                A = data[p + 3],
            };
        }

        WriteableBitmap image = new(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.Lock();
        var stride = image.BackBufferStride;
        var pixOff = HeaderSize + PaletteSize;
        for (var y = 0; y < h; y++)
        {
            var row = image.BackBuffer + y * stride;
            for (var x = 0; x < w; x++)
            {
                var idx = data[pixOff + y * w + x];
                Marshal.WriteInt32(row + x * 4, palette[idx].Argb());
            }
        }
        image.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        image.Unlock();
        return image;
    }
}

internal static class BinaryPrimitivesLE
{
    public static int ReadU16(ReadOnlySpan<byte> data, int offset)
        => data[offset] | (data[offset + 1] << 8);

    public static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
