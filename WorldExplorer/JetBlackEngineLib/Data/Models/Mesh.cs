using JetBlackEngineLib.Data.Animation;
using System.Windows.Media.Media3D;
using System.Windows;

namespace JetBlackEngineLib.Data.Models;

public class Mesh
{
    public readonly IList<Vector3D> Normals;
    public readonly IList<Point3D> Positions;
    public readonly IList<Point> TextureCoordinates;
    public readonly IList<int> TriangleIndices;
    public readonly IList<VertexWeight> VertexWeights;

    /// <summary>
    /// True when <see cref="TriangleIndices"/> contains each triangle twice
    /// (forward then reversed winding) — the PS2 VifDecoder's double-sided
    /// hack. False for one-winding-per-triangle decoders like Xbox. The GLTF
    /// exporter uses this to decide whether to stride past the duplicate.
    /// </summary>
    public bool WindingDuplicated;

    public Mesh(IList<Vector3D> normals, IList<Point3D> positions, IList<Point> textureCoordinates,
        IList<int> triangleIndices, IList<VertexWeight> vertexWeights)
    {
        Normals = normals;
        Positions = positions;
        TextureCoordinates = textureCoordinates;
        TriangleIndices = triangleIndices;
        VertexWeights = vertexWeights;
    }
}