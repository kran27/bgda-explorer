using System.Windows.Media.Imaging;

namespace JetBlackEngineLib.Data.World;

public class WorldFileV1Decoder : WorldFileDecoder
{
    // BoS reuses the BGDA1 file *header* layout but the per-element struct
    // is bigger (80 bytes vs 56). See WorldFileV1BoSDecoder for the BoS path.
    private static readonly EngineVersion[] StaticSupportedVersions =
        {EngineVersion.DarkAlliance};

    public override IReadOnlyList<EngineVersion> SupportedVersions => StaticSupportedVersions;

    protected override IEnumerable<WorldElement> ReadElements(ReadOnlySpan<byte> data, WorldFileHeader header)
    {
        return IterateElements<WorldV1Element>(data, header, WorldV1Element.Size,
            (rawEl, idx) => BuildV1Element(idx, rawEl.VifDataOffset, rawEl.VifLength,
                rawEl.Bounds1, rawEl.Bounds2, rawEl.TextureNum, rawEl.TexCellXY,
                rawEl.Pos, rawEl.Flags, rawEl.SinAlpha));
    }

    protected override WriteableBitmap? GetElementTexture(WorldElementDataInfo dataInfo, WorldTexFile texFile,
        WorldData worldData)
    {
        return texFile.GetBitmapBGDA(dataInfo, worldData);
    }
}
