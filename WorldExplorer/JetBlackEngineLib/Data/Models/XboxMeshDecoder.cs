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
///   +0x1C  u32   = index-buffer start offset. u16 indices live in
///                  [+0x1C, +0x18); count = (+0x18 - +0x1C) / 2.
///   +0x20  u32   = vertex count.
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
        if (data[0x13] != 0xFF) return false;
        if (DataUtil.GetLeInt(data, 0x14) != 0) return false;
        var dataOff = DataUtil.GetLeInt(data, 0x18);
        if (dataOff <= 0x20 || dataOff >= data.Length) return false;
        var vertCount = DataUtil.GetLeInt(data, 0x20);
        if (vertCount <= 0 || vertCount > 0x100000) return false;
        var idxOff = DataUtil.GetLeInt(data, 0x1C);
        if (idxOff < 0x24 || idxOff >= dataOff || ((dataOff - idxOff) & 1) != 0) return false;
        return true;
    }

    /// <summary>
    /// Decode a BoS-Xbox world-file mesh referenced via a 16-byte indirection
    /// table. Returns null if the table doesn't validate.
    /// </summary>
    /// <param name="fileData">Full world-file bytes — pointers in the table are
    /// absolute offsets into this span.</param>
    /// <param name="tableOffset">VifDataOffset from the world element; the
    /// 16-byte mesh table sits here as
    /// (vertexPtr, vertexCount, indexPtr, indexByteCount).</param>
    public static Mesh? DecodeWorldMesh(ReadOnlySpan<byte> fileData, int tableOffset, int texturePixelWidth, int texturePixelHeight)
    {
        if (tableOffset < 0 || tableOffset + 16 > fileData.Length) return null;
        var vertexPtr = DataUtil.GetLeInt(fileData, tableOffset + 0);
        var vertexCount = DataUtil.GetLeInt(fileData, tableOffset + 4);
        var indexPtr = DataUtil.GetLeInt(fileData, tableOffset + 8);
        // The +0x0C field is INDEX COUNT, not byte count. Verified against
        // the engine binary: world.xvu render path passes this value directly
        // to NV097_DRAW_INDEXED inline (sub_A9870 in default.xbe), and it
        // matches the index-count interpretation perfectly — indices fill
        // the region [iptr, iptr + 2*indexCount) which ends exactly at vptr.
        var indexCount = DataUtil.GetLeInt(fileData, tableOffset + 12);

        if (vertexCount <= 0 || vertexCount > 0x100000) return null;
        if (vertexPtr < 0 || vertexPtr + 4 + vertexCount * 16 > fileData.Length) return null;
        if (indexCount <= 0) return null;
        if (indexPtr < 0 || indexPtr + indexCount * 2 > fileData.Length) return null;

        // Vertex data starts with a u32 byte-size prefix that should equal
        // vertexCount * 16. Treat any mismatch as "this isn't the format".
        var sizePrefix = DataUtil.GetLeInt(fileData, vertexPtr);
        if (sizePrefix != vertexCount * 16) return null;

        var positions = new List<Point3D>(vertexCount);
        var normals = new List<Vector3D>(vertexCount);
        var uvs = new List<Point>(vertexCount);
        var weights = new List<VertexWeight>();

        DecodeVertexStream(fileData, vertexPtr + 4, vertexCount, 16,
            texturePixelWidth, texturePixelHeight, positions, normals, uvs);

        var triangleIndices = WalkTriangleStrip(fileData, indexPtr, indexCount, vertexCount);
        AlignWindingToNormals(triangleIndices, positions, normals);
        return new Mesh(normals, positions, uvs, triangleIndices, weights);
    }

    public static List<Mesh> Decode(ReadOnlySpan<byte> data, int texturePixelWidth, int texturePixelHeight)
    {
        var meshes = new List<Mesh>();
        if (!LooksLikeXboxMesh(data)) return meshes;

        var dataOff = DataUtil.GetLeInt(data, 0x18);
        var idxOff = DataUtil.GetLeInt(data, 0x1C);
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

        DecodeVertexStream(data, vertStreamStart, vertCount, stride,
            texturePixelWidth, texturePixelHeight, positions, normals, uvs);

        var indexCount = (dataOff - idxOff) / 2;
        var triangleIndices = WalkTriangleStrip(data, idxOff, indexCount, vertCount);
        AlignWindingToNormals(triangleIndices, positions, normals);

        meshes.Add(new Mesh(normals, positions, uvs, triangleIndices, weights));
        return meshes;
    }

    // The first 16 bytes of every Xbox vertex are pos.int16[3] / normal.int16[3] /
    // uv.int16[2]; only the trailing per-stride bytes (skinning, etc.) vary.
    private static void DecodeVertexStream(
        ReadOnlySpan<byte> data, int streamStart, int vertCount, int stride,
        int texturePixelWidth, int texturePixelHeight,
        List<Point3D> positions, List<Vector3D> normals, List<Point> uvs)
    {
        var uDiv = UvDivisor(texturePixelWidth);
        var vDiv = UvDivisor(texturePixelHeight);
        for (var i = 0; i < vertCount; i++)
        {
            var p = streamStart + i * stride;
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
    }

    // Strips often have inconsistent winding across sub-strip bridges (the
    // artists' bridges don't always preserve parity). Per-vertex normals ARE
    // consistent, so flip any triangle whose face normal opposes its average
    // vertex normal.
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

    // Single triangle strip with degenerate-triangle bridges between
    // sub-strips. Winding alternates within each sub-strip, but the
    // bridges break the global parity: counting parity in *sub-strip-local*
    // terms (i.e., reset on each degenerate) is what keeps every sub-strip's
    // first real triangle at "even" winding, matching how the artists built
    // the strips.
    private static List<int> WalkTriangleStrip(ReadOnlySpan<byte> data, int indexPtr, int indexCount, int vertexCount)
    {
        var result = new List<int>();
        var subStripPos = 0;
        for (var i = 0; i + 2 < indexCount; i++)
        {
            var off = indexPtr + i * 2;
            int a = data[off + 0] | (data[off + 1] << 8);
            int b = data[off + 2] | (data[off + 3] << 8);
            int c = data[off + 4] | (data[off + 5] << 8);
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) { subStripPos = 0; continue; }
            if (a == b || b == c || a == c) { subStripPos = 0; continue; }
            if ((subStripPos & 1) == 0)
            {
                result.Add(a); result.Add(b); result.Add(c);
            }
            else
            {
                result.Add(b); result.Add(a); result.Add(c);
            }
            subStripPos++;
        }
        return result;
    }
}
