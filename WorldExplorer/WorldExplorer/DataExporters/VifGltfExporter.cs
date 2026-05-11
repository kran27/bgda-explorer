using JetBlackEngineLib.Data.Animation;
using JetBlackEngineLib.Data.Models;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using VERTEX = SharpGLTF.Geometry.VertexBuilder<SharpGLTF.Geometry.VertexTypes.VertexPosition,
    SharpGLTF.Geometry.VertexTypes.VertexTexture1, SharpGLTF.Geometry.VertexTypes.VertexJoints4>;

namespace WorldExplorer.DataExporters;

public class VifGltfExporter : IVifExporter
{
    public void SaveToFile(string savePath, Model model, WriteableBitmap? texture, AnimData? pose, int frame,
        double scale)
    {
        SavePartsToFile(savePath, new[] { (model, (BitmapSource?)texture) }, pose, frame, scale);
    }

    public void SavePartsToFile(string savePath, IList<(Model vif, BitmapSource? texture)> parts,
        AnimData? pose, int frame, double scale)
    {
        var dir = Path.GetDirectoryName(savePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(savePath);

        var bakePose = frame >= 0 && pose != null;

        MeshBuilder<VertexPosition, VertexTexture1, VertexJoints4> meshB = new("mesh");

        for (var partIdx = 0; partIdx < parts.Count; partIdx++)
        {
            var (partModel, partTex) = parts[partIdx];

            // Per-part PNG so composite entities (body+hair etc.) keep their
            // own textures instead of all sharing one. Single-part exports get
            // the historical bare "<name>.png" filename.
            var pngName = parts.Count == 1 ? name + ".png" : $"{name}_part{partIdx}.png";
            var pngPath = Path.Combine(dir, pngName);

            if (partTex != null)
            {
                using FileStream stream = new(pngPath, FileMode.Create);
                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(partTex));
                encoder.Save(stream);
            }

            var material = new MaterialBuilder()
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader();
            if (partTex != null)
            {
                material.WithChannelImage(KnownChannel.BaseColor, pngPath);
            }

            foreach (var mesh in partModel.MeshList)
            {
                var prim = meshB.UsePrimitive(material);
                EmitMesh(prim, mesh, pose, frame, scale, bakePose);
            }
        }

        // create a scene
        var modelRoot = ModelRoot.CreateModel();
        var scene = modelRoot.UseScene("Default");

        var skelet = scene.CreateNode("Skeleton");
        Node[] joints;
        if (bakePose)
        {
            joints = new[] { skelet.CreateNode("Joint 0").WithLocalTranslation(new Vector3(0, 0, 0)) };
        }
        else
        {
            var maxJointIndex = 0;
            foreach (var (partModel, _) in parts)
            {
                foreach (var mesh in partModel.MeshList)
                {
                    foreach (var vw in mesh.VertexWeights)
                    {
                        if (vw.bone1 > maxJointIndex) maxJointIndex = vw.bone1;
                        if (vw.bone2 != 255 && vw.bone2 > maxJointIndex) maxJointIndex = vw.bone2;
                        if (vw.bone3 != 255 && vw.bone3 > maxJointIndex) maxJointIndex = vw.bone3;
                        if (vw.bone4 != 255 && vw.bone4 > maxJointIndex) maxJointIndex = vw.bone4;
                    }
                }
            }
            var jointCount = pose != null ? pose.SkeletonDef.Length : maxJointIndex + 1;
            if (jointCount < maxJointIndex + 1) jointCount = maxJointIndex + 1;

            joints = new Node[jointCount];
            for (var j = 0; j < jointCount; j++)
            {
                Node parent;
                Vector3 localPos;
                if (pose != null && j < pose.SkeletonDef.Length)
                {
                    var parentIndex = pose.SkeletonDef[j];
                    parent = parentIndex == 0 || parentIndex - 1 >= j || parentIndex - 1 < 0
                        ? skelet
                        : joints[parentIndex - 1];
                    var bp = pose.BindingPose[j];
                    var parentBp = parentIndex >= 1 && parentIndex - 1 < pose.BindingPose.Length
                        ? pose.BindingPose[parentIndex - 1]
                        : new Point3D(0, 0, 0);
                    localPos = new Vector3(
                        (float)((bp.X - parentBp.X) / scale),
                        (float)((bp.Y - parentBp.Y) / scale),
                        (float)((bp.Z - parentBp.Z) / scale));
                }
                else
                {
                    parent = skelet;
                    localPos = new Vector3(0, 0, 0);
                }
                joints[j] = parent.CreateNode("Joint " + j).WithLocalTranslation(localPos);
            }
        }

        var snode = scene.CreateNode("Skeleton Node");
        snode.Skin = modelRoot.CreateSkin();
        snode.Skin.BindJoints(joints);
        snode.WithMesh(modelRoot.CreateMesh(meshB));

        modelRoot.SaveGLTF(Path.Combine(dir, name + ".gltf"));
    }

    private static void EmitMesh(IPrimitiveBuilder prim, JetBlackEngineLib.Data.Models.Mesh mesh,
        AnimData? pose, int frame, double scale, bool bakePose)
    {
        var verts = new VERTEX[mesh.Positions.Count];

        var hasVertexWeights = mesh.VertexWeights.Count > 0;
        var vwNum = 0;
        var vw = hasVertexWeights ? mesh.VertexWeights[vwNum] : new VertexWeight();

        for (var i = 0; i < mesh.Positions.Count; i++)
        {
            var pos = mesh.Positions[i];
            var uv = mesh.TextureCoordinates[i];

            if (hasVertexWeights && vw.endVertex < i)
            {
                ++vwNum;
                vw = mesh.VertexWeights[vwNum];
                if (i < vw.startVertex || i > vw.endVertex)
                {
                    Debug.Fail("Vertex " + i + " out of range of bone weights " + vw.startVertex + " -> " +
                               vw.endVertex);
                }
            }

            Point3D point = pos;
            if (bakePose)
            {
                var bone1No = vw.bone1;
                var bindingPos1 = pose!.BindingPose[bone1No];
                var bone1Pose = pose.PerFrameFkPoses?[frame, bone1No]
                                ?? throw new InvalidDataException("Invalid frame/bone pair encountered!");
                if (vw.bone2 == 0xFF)
                {
                    var m = Matrix3D.Identity;
                    m.Translate(new Vector3D(-bindingPos1.X, -bindingPos1.Y, -bindingPos1.Z));
                    m.Rotate(bone1Pose.Rotation);
                    m.Translate(new Vector3D(bone1Pose.Position.X, bone1Pose.Position.Y, bone1Pose.Position.Z));
                    point = m.Transform(point);
                }
                else
                {
                    var bone2No = vw.bone2;
                    var bindingPos2 = pose.BindingPose[bone2No];
                    var bone2Pose = pose.PerFrameFkPoses[frame, bone2No];
                    double boneSum = vw.boneWeight1 + vw.boneWeight2;
                    var bone1Coeff = vw.boneWeight1 / boneSum;
                    var bone2Coeff = vw.boneWeight2 / boneSum;

                    var m = Matrix3D.Identity;
                    m.Translate(new Vector3D(-bindingPos1.X, -bindingPos1.Y, -bindingPos1.Z));
                    m.Rotate(bone1Pose.Rotation);
                    m.Translate(new Vector3D(bone1Pose.Position.X, bone1Pose.Position.Y, bone1Pose.Position.Z));
                    var point1 = m.Transform(point);

                    var m2 = Matrix3D.Identity;
                    m2.Translate(new Vector3D(-bindingPos2.X, -bindingPos2.Y, -bindingPos2.Z));
                    m2.Rotate(bone2Pose.Rotation);
                    m2.Translate(new Vector3D(bone2Pose.Position.X, bone2Pose.Position.Y,
                        bone2Pose.Position.Z));
                    var point2 = m2.Transform(point);

                    point = new Point3D((point1.X * bone1Coeff) + (point2.X * bone2Coeff),
                        (point1.Y * bone1Coeff) + (point2.Y * bone2Coeff),
                        (point1.Z * bone1Coeff) + (point2.Z * bone2Coeff));
                }
            }

            VertexPosition pos1 = new((float)(point.X / scale), (float)(point.Y / scale),
                (float)(point.Z / scale));
            VertexTexture1 tex1 = new(new Vector2((float)uv.X, (float)uv.Y));
            List<(int JointIndex, float Weight)> weight = new();

            if (bakePose)
            {
                // Pose is baked into positions; bind every vertex to a single
                // identity joint so a skin-aware viewer won't double-transform.
                weight.Add((0, 1.0f));
            }
            else
            {
                weight.Add((vw.bone1, vw.boneWeight1 / 255.0f));
                if (vw.bone2 != 255)
                {
                    weight.Add((vw.bone2, vw.boneWeight2 / 255.0f));
                }

                if (vw.bone3 != 255)
                {
                    weight.Add((vw.bone3, vw.boneWeight3 / 255.0f));
                }

                if (vw.bone4 != 255)
                {
                    weight.Add((vw.bone4, vw.boneWeight4 / 255.0f));
                }
            }

            verts[i] = new VERTEX(pos1, tex1, weight.ToArray());
        }

        for (var i = 0; i < mesh.TriangleIndices.Count - 3; i += 6)
        {
            prim.AddTriangle(verts[mesh.TriangleIndices[i]], verts[mesh.TriangleIndices[i + 1]],
                verts[mesh.TriangleIndices[i + 2]]);
        }
    }
}
