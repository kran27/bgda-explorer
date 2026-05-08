using JetBlackEngineLib.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace JetBlackEngineLib.Data.World;

public abstract class WorldFileDecoder : ISupportsSpecificEngineVersions
{
    private readonly Dictionary<int, Model> _modelCache = new();
        
    public abstract IReadOnlyList<EngineVersion> SupportedVersions { get; }

    public WorldData Decode(ReadOnlySpan<byte> data, WorldTexFile? texFile)
    {
        _modelCache.Clear();
        WorldData worldData = new();
            
        var headerSpan = MemoryMarshal.Cast<byte, WorldFileHeader>(data);
        var header = headerSpan[0];
        var texX0 = header.Texll % 100;
        var texY0 = header.Texll / 100;
        var texX1 = header.Texur % 100;
        var texY1 = header.Texur / 100;

        worldData.TextureChunkOffsets =
            ReadTextureChunkOffsets(data, header.WorldTexOffsetsOffset, texX0, texY0, texX1 + 1, texY1);

        foreach (var element in ReadElements(data, header))
        {
            PopulateElementRelatedData(element, data, worldData, texFile);
            worldData.WorldElements.Add(element);
        }

        return worldData;
    }
        
    protected abstract IEnumerable<WorldElement> ReadElements(ReadOnlySpan<byte> data, WorldFileHeader header);
        
    protected abstract WriteableBitmap? GetElementTexture(WorldElementDataInfo dataInfo, WorldTexFile texFile,
        WorldData worldData);

    protected abstract int[,] ReadTextureChunkOffsets(ReadOnlySpan<byte> data, int offset, int x1, int y1, int x2,
        int y2);

    protected List<WorldElement> IterateElements<T>(ReadOnlySpan<byte> data, WorldFileHeader header,
        int elementSize, Func<T, int, WorldElement?> elementParseFunc) where T : struct
    {
        List<WorldElement> elements = new(header.NumberOfElements);
        for (var elementIdx = 0; elementIdx < header.NumberOfElements; ++elementIdx)
        {
            var rawElSpan = MemoryMarshal.Cast<byte, T>(
                data.Slice(header.ElementArrayStart + (elementSize * elementIdx), elementSize)
            );
            var rawEl = rawElSpan[0];
            var element = elementParseFunc(rawEl, elementIdx);
            if (element == null) continue;
            elements.Add(element);
        }

        return elements;
    }

    private void PopulateElementRelatedData(WorldElement element, ReadOnlySpan<byte> data,
        WorldData worldData, WorldTexFile? texFile)
    {
        if (element.DataInfo == null)
        {
            return;
        }

        if (texFile != null)
        {
            // A single corrupt entry shouldn't abort the whole world load —
            // we'd lose the rest of the level. Per-element textures can come
            // and go independently, so isolate failures and keep going.
            try
            {
                element.Texture = GetElementTexture(element.DataInfo, texFile, worldData);
                if (element.Texture != null)
                {
                    element.DataInfo.TextureWidth = element.Texture.PixelWidth;
                    element.DataInfo.TextureHeight = element.Texture.PixelHeight;
                }
            }
            catch (Exception)
            {
                element.Texture = null;
            }
        }

        var uvW = element.DataInfo.TextureWidth > 0 ? element.DataInfo.TextureWidth : 256;
        var uvH = element.DataInfo.TextureHeight > 0 ? element.DataInfo.TextureHeight : 256;

        // BoS-Xbox world meshes are referenced indirectly: VifDataOffset points
        // to a 16-byte (vertexPtr, vertexCount, indexPtr, indexByteCount) table
        // pointing into the file. Try that first; on failure we fall through
        // to the PS2 VIF path. DecodeWorldMesh validates the table (size
        // prefix, in-file pointers, even index byte count) so PS2 data won't
        // accidentally be decoded as Xbox.
        if (!_modelCache.TryGetValue(element.DataInfo.VifDataOffset, out var cached))
        {
            var xboxMesh = XboxMeshDecoder.DecodeWorldMesh(data, element.DataInfo.VifDataOffset, uvW, uvH);
            if (xboxMesh != null)
            {
                cached = new Model(new[] { xboxMesh });
                _modelCache.Add(element.DataInfo.VifDataOffset, cached);
                element.Model = cached;
                return;
            }
        }
        else
        {
            element.Model = cached;
            return;
        }

        var fullSliceLen = element.DataInfo.VifDataLength * 0x10;
        if (fullSliceLen <= 0) return;

        var nRegs = data[element.DataInfo.VifDataOffset + 0x10];
        var vifStartOffset = (nRegs + 2) * 0x10;
        var absoluteVifStartOffset = element.DataInfo.VifDataOffset + vifStartOffset;
        var vifDataLength = fullSliceLen - vifStartOffset;

        if (vifDataLength > 0)
        {
            // VifDecoder divides UV coords by (textureDim * 16). With dim 0 we
            // get NaN UVs and WPF silently drops the geometry — this is what
            // makes a BoS level "have 425 elements but render nothing" when
            // the texture atlas isn't loaded yet. Fall back to 256 so UVs are
            // finite; the result paints with a checkerboard fallback brush
            // until a real texture is wired up.
            element.Model = GetElementModel(
                NullLogger.Instance,
                absoluteVifStartOffset,
                data.Slice(absoluteVifStartOffset, vifDataLength),
                uvW,
                uvH
            );
        }
    }

    private Model GetElementModel(ILogger log, int startOffset, ReadOnlySpan<byte> data, int texWidth, int texHeight)
    {
        if (!_modelCache.TryGetValue(startOffset, out var model))
        {
            model = DecodeModel(log, data, texWidth, texHeight);
            _modelCache.Add(startOffset, model);
        }
        return model;
    }

    private Model DecodeModel(ILogger log, ReadOnlySpan<byte> data, int texWidth, int texHeight)
    {
        return new Model(new[]
        {
            VifDecoder.DecodeMesh(
                log,
                data,
                texWidth,
                texHeight)
        });
    }
}