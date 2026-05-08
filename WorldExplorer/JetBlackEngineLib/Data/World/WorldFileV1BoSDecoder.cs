using System.Windows.Media.Imaging;

namespace JetBlackEngineLib.Data.World;

/// <summary>
/// BoS-specific world decoder. Same global header layout as BGDA1 but the
/// per-element struct is <see cref="WorldV1BoSElement"/> at 80 bytes
/// instead of BGDA's 56-byte <see cref="WorldV1Element"/>, plus the field
/// positions inside the element shifted past +0x08 too.
/// </summary>
public class WorldFileV1BoSDecoder : WorldFileDecoder
{
    private static readonly EngineVersion[] StaticSupportedVersions =
        {EngineVersion.BrotherhoodOfSteel};

    public override IReadOnlyList<EngineVersion> SupportedVersions => StaticSupportedVersions;

    protected override IEnumerable<WorldElement> ReadElements(ReadOnlySpan<byte> data, WorldFileHeader header)
    {
        return IterateElements<WorldV1BoSElement>(data, header, WorldV1BoSElement.Size,
            (rawEl, idx) => BuildV1Element(idx, rawEl.VifDataOffset, rawEl.VifLength,
                rawEl.Bounds1, rawEl.Bounds2, rawEl.TextureNum, rawEl.TexCellXY,
                rawEl.Pos, rawEl.Flags, rawEl.SinAlpha));
    }

    protected override WriteableBitmap? GetElementTexture(WorldElementDataInfo dataInfo, WorldTexFile texFile,
        WorldData worldData)
    {
        // BoS levels (both PS2 and Xbox) use PS2-style DCT chunk archives for
        // the level texture atlas. The descriptor format differs between PS2
        // and Xbox BoS (data-offset field at +0x10 vs +0x08); WorldTexFile.Decode
        // handles both.
        return texFile.GetBitmapBGDA(dataInfo, worldData);
    }
}
