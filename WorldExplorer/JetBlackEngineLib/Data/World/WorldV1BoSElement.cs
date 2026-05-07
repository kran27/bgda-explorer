using JetBlackEngineLib.Core;
using System.Runtime.InteropServices;

namespace JetBlackEngineLib.Data.World;

/// <summary>
/// BoS variant of <see cref="WorldV1Element"/>. Same field set, but the
/// struct grew from 56 bytes (0x38) to 80 bytes (0x50): a 4-byte pad after
/// VifLength, after Bounds1, after Bounds2, plus a 14-byte trailing region.
/// Field offsets identified empirically by reading element[0..N] of every
/// shipped level's world file: VifDataOffset/VifLength at +0x00/+0x08
/// resolve to in-file offsets; Bounds floats at +0x10 / +0x20 give plausible
/// world-space coordinates; +0x30 / +0x34 / +0x36 hold TextureNum /
/// TexCellXY / Pos as in BGDA.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WorldV1BoSElement
{
    public const int Size = 0x50;

    public int VifDataOffset;     // +0x00
    public int Tex2;              // +0x04
    public int VifLength;         // +0x08
    public int Pad0C;             // +0x0C
    public Vector3F Bounds1;      // +0x10
    public int Pad1C;             // +0x1C
    public Vector3F Bounds2;      // +0x20
    public int Pad2C;             // +0x2C
    public int TextureNum;        // +0x30
    public short TexCellXY;       // +0x34
    public Vector3Short Pos;      // +0x36
    public int Flags;             // +0x3C
    public short SinAlpha;        // +0x40
    // 14 trailing bytes reserved/unknown
    public short Pad42, Pad44, Pad46, Pad48, Pad4A, Pad4C, Pad4E;
}
