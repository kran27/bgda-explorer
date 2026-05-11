using System.Windows.Media.Media3D;

namespace JetBlackEngineLib.Data.Animation;

public class BdgaAnmDecoder : AnmDecoder
{
    private static readonly EngineVersion[] StaticSupportedVersions =
        {EngineVersion.DarkAlliance, EngineVersion.BrotherhoodOfSteel};
    public override IReadOnlyList<EngineVersion> SupportedVersions => StaticSupportedVersions;

    public override AnimData Decode(ReadOnlySpan<byte> data)
    {
        var endIndex = data.Length;
        var numBones = DataUtil.GetLeInt(data, 0);
        var offset4Val = DataUtil.GetLeInt(data, 4);
        var offset8Val = DataUtil.GetLeInt(data, 8);
        var offset10Val = DataUtil.GetLeInt(data, 0x10);
        var offset14Val = DataUtil.GetLeInt(data, 0x14);
        var offset18Val = DataUtil.GetLeInt(data, 0x18);
        var bindingPoseOffset = DataUtil.GetLeInt(data, 0x0C);

        var bindingPose = new Point3D[numBones];
        for (var i = 0; i < numBones; ++i)
        {
            bindingPose[i] = new Point3D(
                -DataUtil.GetLeShort(data, bindingPoseOffset + i * 8 + 0) / 64.0,
                -DataUtil.GetLeShort(data, bindingPoseOffset + i * 8 + 2) / 64.0,
                -DataUtil.GetLeShort(data, bindingPoseOffset + i * 8 + 4) / 64.0
            );
        }

        // Skeleton structure

        var skeletonDef = new int[numBones];
        for (var i = 0; i < numBones; ++i)
        {
            skeletonDef[i] = data[offset10Val + i];
        }

        var curPose = new AnimMeshPose[numBones];
        var meshPoses = new List<AnimMeshPose?>();
        AnimMeshPose? pose;
        for (var boneNum = 0; boneNum < numBones; ++boneNum)
        {
            pose = new AnimMeshPose { BoneNum = boneNum, FrameNum = 0 };
            var frameOff = offset8Val + boneNum * 0x0e;

            pose.Position = new Point3D(
                DataUtil.GetLeShort(data, frameOff) / 64.0,
                DataUtil.GetLeShort(data, frameOff + 2) / 64.0,
                DataUtil.GetLeShort(data, frameOff + 4) / 64.0);

            var a = DataUtil.GetLeShort(data, frameOff + 6) / 4096.0;
            var b = DataUtil.GetLeShort(data, frameOff + 8) / 4096.0;
            var c = DataUtil.GetLeShort(data, frameOff + 0x0A) / 4096.0;
            var d = DataUtil.GetLeShort(data, frameOff + 0x0C) / 4096.0;

            pose.Rotation = new Quaternion(b, c, d, a);

            pose.Velocity = new Point3D(0, 0, 0);
            pose.AngularVelocity = new Quaternion(0, 0, 0, 0);

            // This may give us duplicate frame zero poses, but that's ok.
            meshPoses.Add(pose);
            curPose[boneNum] = new AnimMeshPose(pose);
        }

        var curAngVelFrame = new int[numBones];
        var curVelFrame = new int[numBones];

        var numFrames = offset4Val;

        var frameNumber = 0;
        var otherOff = offset8Val + numBones * 0x0e;

        pose = null;
        while (otherOff < endIndex)
        {
            int count = data[otherOff];
            if (frameNumber + count >= numFrames) break;
            otherOff++;
            var byte2 = data[otherOff++];
            var boneNum = byte2 & 0x3f;

            frameNumber += count;

            if (pose == null || pose.FrameNum != frameNumber || pose.BoneNum != boneNum)
            {
                if (pose != null)
                {
                    meshPoses.Add(pose);
                }

                // A new keyframe must store the integrated pose at frameNumber
                // for BOTH channels, even if this record only updates one of
                // them. BuildPerFramePoses extrapolates forward from each
                // keyframe using its stored velocity, so a stale rotation or
                // position on a keyframe shows up as a one-frame snap.
                var angDt = (frameNumber - curAngVelFrame[boneNum]) / 131072.0;
                var posDt = (frameNumber - curVelFrame[boneNum]) / 512.0;
                var av = curPose[boneNum].AngularVelocity;
                var v = curPose[boneNum].Velocity;
                pose = new AnimMeshPose
                {
                    FrameNum = frameNumber,
                    BoneNum = boneNum,
                    Position = new Point3D(
                        curPose[boneNum].Position.X + v.X * posDt,
                        curPose[boneNum].Position.Y + v.Y * posDt,
                        curPose[boneNum].Position.Z + v.Z * posDt),
                    Rotation = new Quaternion(
                        curPose[boneNum].Rotation.X + av.X * angDt,
                        curPose[boneNum].Rotation.Y + av.Y * angDt,
                        curPose[boneNum].Rotation.Z + av.Z * angDt,
                        curPose[boneNum].Rotation.W + av.W * angDt),
                    AngularVelocity = av,
                    Velocity = v
                };
            }

            // bit 7 specifies an angular-velocity (set) vs linear-velocity
            // record; bit 6 specifies byte (set) vs short element width.
            if ((byte2 & 0x80) == 0x80)
            {
                int a, b, c, d;
                if ((byte2 & 0x40) == 0x40)
                {
                    a = (sbyte)data[otherOff++];
                    b = (sbyte)data[otherOff++];
                    c = (sbyte)data[otherOff++];
                    d = (sbyte)data[otherOff++];
                }
                else
                {
                    a = DataUtil.GetLeShort(data, otherOff);
                    b = DataUtil.GetLeShort(data, otherOff + 2);
                    c = DataUtil.GetLeShort(data, otherOff + 4);
                    d = DataUtil.GetLeShort(data, otherOff + 6);
                    otherOff += 8;
                }

                Quaternion angVel = new(b, c, d, a);
                pose.AngularVelocity = angVel;
                curPose[boneNum].Rotation = pose.Rotation;
                curPose[boneNum].AngularVelocity = angVel;
                curAngVelFrame[boneNum] = frameNumber;
            }
            else
            {
                int x, y, z;
                if ((byte2 & 0x40) == 0x40)
                {
                    x = (sbyte)data[otherOff++];
                    y = (sbyte)data[otherOff++];
                    z = (sbyte)data[otherOff++];
                }
                else
                {
                    x = DataUtil.GetLeShort(data, otherOff);
                    y = DataUtil.GetLeShort(data, otherOff + 2);
                    z = DataUtil.GetLeShort(data, otherOff + 4);
                    otherOff += 6;
                }

                Point3D vel = new(x, y, z);
                pose.Velocity = vel;
                curPose[boneNum].Position = pose.Position;
                curPose[boneNum].Velocity = vel;
                curVelFrame[boneNum] = frameNumber;
            }
        }

        if (pose != null)
        {
            meshPoses.Add(pose);
        }

        var animData = new AnimData(bindingPose, numBones, numFrames, offset4Val, offset14Val, offset18Val,
            skeletonDef, meshPoses);
        return animData;
    }
}