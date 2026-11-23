using Animanager.UI.Components;
using System.Diagnostics;
using Godot;

namespace Animanager.Core.Retarget;

public sealed record AssetReadiness(bool Ready, string[] Errors)
{
    public string Summary => Ready ? "Ready · 人体兼容检查通过" : "待修复 · " + string.Join("；", Errors);
    public static AssetReadiness Inspect(Node3D root, bool animation) =>
        InspectAsync(root, animation, null, default).GetAwaiter().GetResult();

    internal static Task<AssetReadiness> InspectPreviewAsync(Node3D root, SceneTree tree, CancellationToken ct) =>
        InspectAsync(root, true, tree, ct);

    private static async Task<AssetReadiness> InspectAsync(Node3D root, bool animation, SceneTree? tree, CancellationToken ct)
    {
        var errors = new List<string>();
        var budget = tree != null ? Stopwatch.StartNew() : null;
        try
        {
            var skeletons = Preview3DViewport.Descendants(root).OfType<Skeleton3D>().ToArray();
            if (skeletons.Length != 1) errors.Add("需要一个人体骨架，实际 " + skeletons.Length);
            else
            {
                errors.AddRange(HumanoidMap.Prepare(new(skeletons[0], root)));
                if (!animation) errors.AddRange(SkinErrors(root, skeletons[0]));
                else
                {
                    var player = Preview3DViewport.Descendants(root).OfType<AnimationPlayer>().FirstOrDefault();
                    var clips = player?.GetAnimationList().Where(c => c != "RESET").ToArray() ?? [];
                    var playerRoot = player?.GetNodeOrNull(player.RootNode);
                    if (clips.Length == 0) errors.Add("没有可播放动画片段");
                    foreach (string clip in clips)
                    {
                        var a = player!.GetAnimation(clip);
                        if (!double.IsFinite(a.Length) || a.Length <= 0) errors.Add(clip + "：时长无效");
                        bool boneTrack = false;
                        for (int t = 0; t < a.GetTrackCount(); t++)
                        {
                            var type = a.TrackGetType(t);
                            if (!a.TrackIsEnabled(t) || type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D)) continue;
                            var path = a.TrackGetPath(t);
                            if (path.GetSubNameCount() == 0) continue;
                            string bone = path.GetSubName(0);
                            var node = playerRoot?.GetNodeOrNull(new NodePath(path.GetConcatenatedNames()));
                            int keyCount = a.TrackGetKeyCount(t);
                            if (node != skeletons[0] || skeletons[0].FindBone(bone) < 0) errors.Add(clip + "：骨骼轨道无法绑定 " + path);
                            else boneTrack |= keyCount > 0;
                            for (int k = 0; k < keyCount; k++)
                            {
                                double time = a.TrackGetKeyTime(t, k);
                                using var value = a.TrackGetKeyValue(t, k);
                                // FBX import can retain support keys just beyond the clip interval.
                                // They define interpolation at the endpoint and are valid Godot tracks.
                                bool valid = double.IsFinite(time);
                                if (type == Animation.TrackType.Rotation3D)
                                { var q = value.AsQuaternion(); valid &= q.IsFinite() && q.LengthSquared() > 1e-8; }
                                else
                                {
                                    var v = value.AsVector3(); valid &= v.IsFinite();
                                    if (type == Animation.TrackType.Scale3D) valid &= v.X > 0 && RigDescriptor.Uniform(v);
                                }
                                if (!valid) { errors.Add($"{clip}：无效关键帧 / 不支持的骨骼缩放 {path} ({type}, t={time:G7}, 时长={a.Length:G7}, 值={value})"); break; }
                                if (budget != null && (k & 255) == 255 && budget.ElapsedMilliseconds >= 8)
                                {
                                    await tree!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                                    ct.ThrowIfCancellationRequested();
                                    budget.Restart();
                                }
                            }
                            if (budget != null && budget.ElapsedMilliseconds >= 8)
                            {
                                await tree!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                                ct.ThrowIfCancellationRequested();
                                budget.Restart();
                            }
                        }
                        if (!boneTrack) errors.Add(clip + "：没有有效的骨骼动画轨道");
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { errors.Add(e.Message); }
        return new(errors.Count == 0, errors.Distinct().ToArray());
    }
    public static string[] SkinErrors(Node3D root, Skeleton3D skeleton)
    {
        var errors = new List<string>();
        var meshes = Preview3DViewport.Descendants(root).OfType<MeshInstance3D>().Where(m => m.Mesh != null && m.Skin != null).ToArray();
        if (meshes.Length == 0) errors.Add("缺少绑定到人体骨架的蒙皮");
        foreach (var mesh in meshes)
        {
            var skin = mesh.Skin!;
            if (skin.GetBindCount() == 0) errors.Add(mesh.Name + "：蒙皮没有绑定骨骼");
            if (mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) != skeleton) errors.Add(mesh.Name + "：蒙皮未连接人体骨架");
            for (int i = 0; i < skin.GetBindCount(); i++)
            {
                int bone = skin.GetBindName(i).IsEmpty ? skin.GetBindBone(i) : skeleton.FindBone(skin.GetBindName(i));
                if (bone < 0 || bone >= skeleton.GetBoneCount() || !skin.GetBindPose(i).IsFinite() || Math.Abs(skin.GetBindPose(i).Basis.Determinant()) < 1e-12)
                    errors.Add(mesh.Name + "：蒙皮绑定无效 " + i);
            }
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                if (arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil || arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil)
                { errors.Add(mesh.Name + "：缺少顶点权重"); continue; }
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var joints = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                int stride = vertices.Length == 0 ? 0 : weights.Length / vertices.Length;
                if (stride == 0 || joints.Length != weights.Length || weights.Length % vertices.Length != 0)
                { errors.Add(mesh.Name + "：缺少有效顶点权重"); continue; }
                for (int v = 0; v < vertices.Length; v++)
                {
                    float sum = 0;
                    for (int j = 0; j < stride; j++)
                    {
                        int k = v * stride + j;
                        if (!float.IsFinite(weights[k]) || weights[k] < 0 || weights[k] > 0 && (joints[k] < 0 || joints[k] >= skin.GetBindCount())) { sum = float.NaN; break; }
                        sum += weights[k];
                    }
                    if (!vertices[v].IsFinite() || !float.IsFinite(sum) || sum <= 1e-5f || Math.Abs(sum - 1) > .02f)
                    { errors.Add(mesh.Name + "：顶点权重无效 / 未归一化，顶点 " + v); break; }
                }
            }
        }
        return errors.Distinct().ToArray();
    }
}
