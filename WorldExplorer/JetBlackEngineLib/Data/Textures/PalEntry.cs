/*  Copyright (C) 2012 Ian Brown

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

namespace JetBlackEngineLib.Data.Textures;

public class PalEntry
{
    private const int TransparentThreshold = 0x80;
    public byte A;
    public byte B;
    public byte G;
    public byte R;

    public int Argb()
    {
        // Binary alpha: A == 0 is the colorkey (transparent), everything
        // else renders fully opaque. This is wrong for character textures
        // whose cutouts use low-but-non-zero alpha values (those render as
        // their RGB color — typically black — rather than being discarded),
        // but bumping the threshold higher misclassifies world/level
        // palettes whose alphas span 0x01..0x80 uniformly: "transparent
        // characters" → "invisible levels". A proper fix needs per-texture
        // alpha-mode dispatch keyed on the TEX header marker byte
        // (0xC2/0xC4 vs 0xC6) — not yet implemented.
        var convertedAlpha = (byte)(A == 0 ? 0 : 0xFF);
        var argb = (convertedAlpha << 24) |
                   ((R << 16) & 0xFF0000) |
                   ((G << 8) & 0xFF00) |
                   (B & 0xFF);
        return argb;
    }

    public static PalEntry[] ReadPalette(ReadOnlySpan<byte> fileData, int palW, int palH)
    {
        var numEntries = palW * palH;
        var palette = new PalEntry[numEntries];
        for (var i = 0; i < numEntries; ++i)
        {
            PalEntry pe = new()
            {
                R = fileData[i * 4],
                G = fileData[(i * 4) + 1],
                B = fileData[(i * 4) + 2],
                A = fileData[(i * 4) + 3]
            };

            palette[i] = pe;
        }

        return palette;
    }

    public static PalEntry[] ReadPalette(byte[] fileData, int startOffset, int palW, int palH)
    {
        var numEntries = palW * palH;
        var palette = new PalEntry[numEntries];
        for (var i = 0; i < numEntries; ++i)
        {
            PalEntry pe = new()
            {
                R = fileData[startOffset + (i * 4)],
                G = fileData[startOffset + (i * 4) + 1],
                B = fileData[startOffset + (i * 4) + 2],
                A = fileData[startOffset + (i * 4) + 3]
            };

            palette[i] = pe;
        }

        return palette;
    }

    public static PalEntry[] UnswizzlePalette(PalEntry[] palette)
    {
        if (palette.Length == 256)
        {
            var unswizzled = new PalEntry[palette.Length];

            var j = 0;
            for (var i = 0; i < 256; i += 32, j += 32)
            {
                Copy(unswizzled, i, palette, j, 8);
                Copy(unswizzled, i + 16, palette, j + 8, 8);
                Copy(unswizzled, i + 8, palette, j + 16, 8);
                Copy(unswizzled, i + 24, palette, j + 24, 8);
            }

            return unswizzled;
        }

        return palette;
    }

    private static void Copy(PalEntry[] unswizzled, int i, PalEntry[] swizzled, int j, int num)
    {
        for (var x = 0; x < num; ++x)
        {
            unswizzled[i + x] = swizzled[j + x];
        }
    }
}