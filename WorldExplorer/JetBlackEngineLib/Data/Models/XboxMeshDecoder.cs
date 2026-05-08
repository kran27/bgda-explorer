using System.Windows;
using System.Windows.Media.Media3D;
using JetBlackEngineLib.Data.Animation;

namespace JetBlackEngineLib.Data.Models;

/// <summary>
/// Decodes Xbox-platform BoS mesh entries.
///
/// Header layout (verified empirically):
///   +0x10  byte  = submesh count
///   +0x12  byte  = LOD count
///   +0x13  byte  = 0xFF sentinel
///   +0x14  u32   = zero pad
///   +0x18  u32   = data-section offset. The u32 at that offset holds the
///                  vertex-stream byte size; vertices follow immediately after.
///   +0x1C  u32   = role unclear — varies (0x100, 0x380, 0xE80, 0x1280, …)
///                  but vertex stride is 32 bytes regardless. Not used here.
///   +0x20  u32   = vertex count.
///
/// Index buffer: lives directly in front of the vertex stream — u16 entries
/// running from somewhere in the header region up to the byte before the
/// vertex-stream-size u32 at +0x18. We locate its start by scanning backwards
/// for the boundary where u16 values stop being valid vertex indices.
///
/// Topology: single triangle strip with **degenerate-triangle restart bridges**
/// (a == b or b == c marks a discardable triangle that bridges two sub-strips).
/// Standard old-school PS2/Xbox technique.
///
/// Vertex stream (stride varies — 32 bytes for the common non-skinned format,
/// 38 bytes for at least one skinned variant; derived from file size since
/// the only fields we use sit at fixed offsets in the first 16 bytes):
///   +0..5    int16[3]  position           (divided by 16, mirroring the PS2 convention)
///   +6..11   int16[3]  normal             (normalized by 32767)
///   +12..15  int16[2]  texcoord (s, t)    (encoded as pixel * scale, where
///                                          scale = largest pow2 with
///                                          dim*scale ≤ 32768. So u_norm =
///                                          u_raw / (width * uScale).)
///   +16..end ?         varies — for the 32-byte form, includes a per-vertex
///                       float and a constant 1.0 bone-weight slot. For 38-byte
///                       skinned vertices, presumably contains bone indices /
///                       weights. Not interpreted here.
///
/// The vertex-shader register convention (from disassembling world.xvu / skin.xvu):
///   v0 = position, v2 = normal, v9 = texcoord. v1 carries skinning data when present.
/// </summary>
public static class XboxMeshDecoder
{
    public static bool LooksLikeXboxMesh(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40) return false;
        // Discriminators against PS2 VIF: 0xFF sentinel at +0x13, zero pad at
        // +0x14..+0x17, in-file data offset at +0x18, sane vertex count at +0x20.
        // (Don't gate on +0x1C: it varies — 0x100 / 0x380 / 0x1280 etc. — across
        //  formats. Knowing that value is needed to decode, but not to detect.)
        if (data[0x13] != 0xFF) return false;
        if (DataUtil.GetLeInt(data, 0x14) != 0) return false;
        var dataOff = DataUtil.GetLeInt(data, 0x18);
        if (dataOff <= 0x20 || dataOff >= data.Length) return false;
        var vertCount = DataUtil.GetLeInt(data, 0x20);
        if (vertCount <= 0 || vertCount > 0x100000) return false;
        return true;
    }

    public static List<Mesh> Decode(ReadOnlySpan<byte> data, int texturePixelWidth, int texturePixelHeight)
    {
        var meshes = new List<Mesh>();
        if (!LooksLikeXboxMesh(data)) return meshes;

        var dataOff = DataUtil.GetLeInt(data, 0x18);
        var vertCount = DataUtil.GetLeInt(data, 0x20);

        // The u32 at dataOff is the vertex-stream byte size; vertices follow
        // immediately. Stride varies — 32 bytes for non-skinned, 38 for at
        // least one skinned variant. Derive it from the file size; the first
        // 16 bytes (pos+normal+UV) we care about are at the same offsets.
        var vertStreamStart = dataOff + 4;
        var streamBytes = data.Length - vertStreamStart;
        if (vertCount == 0 || streamBytes <= 0 || streamBytes % vertCount != 0) return meshes;
        var stride = streamBytes / vertCount;
        if (stride < 16) return meshes;

        var positions = new List<Point3D>(vertCount);
        var normals = new List<Vector3D>(vertCount);
        var uvs = new List<Point>(vertCount);
        var weights = new List<VertexWeight>();

        var uDiv = UvDivisor(texturePixelWidth);
        var vDiv = UvDivisor(texturePixelHeight);

        for (var i = 0; i < vertCount; i++)
        {
            var p = vertStreamStart + i * stride;
            var px = (short)(data[p + 0] | (data[p + 1] << 8));
            var py = (short)(data[p + 2] | (data[p + 3] << 8));
            var pz = (short)(data[p + 4] | (data[p + 5] << 8));
            var nx = (short)(data[p + 6] | (data[p + 7] << 8));
            var ny = (short)(data[p + 8] | (data[p + 9] << 8));
            var nz = (short)(data[p + 10] | (data[p + 11] << 8));
            var u  = (short)(data[p + 12] | (data[p + 13] << 8));
            var v  = (short)(data[p + 14] | (data[p + 15] << 8));

            positions.Add(new Point3D(px / 16.0, py / 16.0, pz / 16.0));
            normals.Add(new Vector3D(nx / 32767.0, ny / 32767.0, nz / 32767.0));
            uvs.Add(new Point(u / uDiv, v / vDiv));
        }

        var triangleIndices = ExtractStripIndices(data, vertCount, dataOff);
        // Strips often have inconsistent winding across sub-strip bridges
        // (artists' bridges don't always preserve parity). Per-vertex normals
        // ARE consistent and trustworthy, so use them to fix winding: any
        // triangle whose face normal opposes its average vertex normal gets
        // its winding swapped.
        AlignWindingToNormals(triangleIndices, positions, normals);

        meshes.Add(new Mesh(normals, positions, uvs, triangleIndices, weights));
        return meshes;
    }

    private static void AlignWindingToNormals(List<int> tris, IList<Point3D> positions, IList<Vector3D> normals)
    {
        for (var t = 0; t + 2 < tris.Count; t += 3)
        {
            var ia = tris[t]; var ib = tris[t + 1]; var ic = tris[t + 2];
            var pa = positions[ia]; var pb = positions[ib]; var pc = positions[ic];
            var faceNormal = Vector3D.CrossProduct(pb - pa, pc - pa);
            var avgNormal = (normals[ia] + normals[ib] + normals[ic]);
            if (Vector3D.DotProduct(faceNormal, avgNormal) < 0)
            {
                tris[t] = ib;
                tris[t + 1] = ia;
            }
        }
    }

    /// <summary>
    /// UVs are stored as <c>pixel * scale</c>, where <c>scale</c> is the
    /// largest power of 2 such that <c>dim * scale &lt;= 32768</c>. This is
    /// per axis: a 320×256 texture has uDiv=20480 and vDiv=32768, so the same
    /// raw int16 means very different normalized UVs depending on dimension.
    /// </summary>
    private static double UvDivisor(int dim)
    {
        if (dim <= 0) return 32768.0;
        var scale = 1;
        while (scale * 2 * dim <= 32768) scale <<= 1;
        return scale * (double)dim;
    }

    private static List<int> ExtractStripIndices(ReadOnlySpan<byte> data, int vertCount, int indexEndExclusive)
    {
        // Index buffer sits directly in front of the vertex stream and ends
        // at indexEndExclusive (= dataOff). Find its start by walking u16
        // entries backwards while they're valid (< vertCount) AND we don't
        // see a run of consecutive zero u16s, which marks the zero-padded
        // header region above the index buffer.
        var idxStart = indexEndExclusive;
        var lastNonZero = indexEndExclusive;
        var zeroRun = 0;
        const int zeroRunStop = 4;
        while (idxStart - 2 >= 0)
        {
            int v = data[idxStart - 2] | (data[idxStart - 1] << 8);
            if (v >= vertCount) break;
            if (v == 0)
            {
                zeroRun++;
                if (zeroRun >= zeroRunStop)
                {
                    idxStart = lastNonZero;
                    break;
                }
            }
            else
            {
                zeroRun = 0;
                lastNonZero = idxStart - 2;
            }
            idxStart -= 2;
        }

        var result = new List<int>();
        var stripLen = (indexEndExclusive - idxStart) / 2;
        if (stripLen < 3) return result;

        // Single triangle strip with degenerate-triangle bridges between
        // sub-strips. Winding alternates within each sub-strip, but the
        // bridges break the global parity: counting parity in *sub-strip-
        // local* terms (i.e., reset on each degenerate) is what keeps every
        // sub-strip's first real triangle at "even" winding, matching how
        // the artists built the strips.
        var subStripPos = 0;
        for (var i = 0; i + 2 < stripLen; i++)
        {
            var off = idxStart + i * 2;
            int a = data[off + 0] | (data[off + 1] << 8);
            int b = data[off + 2] | (data[off + 3] << 8);
            int c = data[off + 4] | (data[off + 5] << 8);
            if (a == b || b == c || a == c)
            {
                subStripPos = 0;
                continue;
            }
            if ((subStripPos & 1) == 0)
            {
                result.Add(a);
                result.Add(b);
                result.Add(c);
            }
            else
            {
                result.Add(b);
                result.Add(a);
                result.Add(c);
            }
            subStripPos++;
        }
        return result;
    }
}
