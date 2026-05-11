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
    public byte A;
    public byte B;
    public byte G;
    public byte R;

    /// <summary>
    /// When true, every decoded pixel is forced to alpha 0xFF. Toggled by the
    /// "Force opaque textures" setting; applies to both PS2 and Xbox paths.
    /// </summary>
    public static bool ForceOpaque;

    public int Argb()
    {
        // PS2 palette alpha is 0..0x80 where 0x80 is the engine's "fully
        // transparent" sentinel (used as a colorkey on HUD cutouts) and
        // 0x00..0x7F is the visible opacity ramp. Expand 0..0x7F into the
        // standard 0..0xFF range so semi-transparent pixels look right.
        byte alpha;
        if (ForceOpaque) alpha = 0xFF;
        else if (A == 0x80) alpha = 0;
        else alpha = (byte)Math.Min(A * 2, 0xFF);
        return (alpha << 24) |
               ((R << 16) & 0xFF0000) |
               ((G << 8) & 0xFF00) |
               (B & 0xFF);
    }

    /// <summary>
    /// For palettes whose alpha is already in 0..0xFF (Xbox).
    /// </summary>
    public int ArgbDirect()
    {
        var alpha = ForceOpaque ? (byte)0xFF : A;
        return (alpha << 24) |
               ((R << 16) & 0xFF0000) |
               ((G << 8) & 0xFF00) |
               (B & 0xFF);
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