using Godot;
using Animanager.Core.Motion;
using Animanager.UI.Components;

namespace Animanager.Core.Pipeline;

public static partial class ExportService
{
    internal static void WriteConfigured(string source, string output, bool animationLibrary, ExportOptions options, CancellationToken ct,
        bool forBlender = false)
    {
        ct.ThrowIfCancellationRequested();
        var motion = MotionJson.IsMotion(source) ? new MotionPlayback(source) : null;
        float? sourceFps;
        Node3D root;
        if (motion != null)
        {
            root = motion.Root;
            sourceFps = (float)motion.Manifest.SourceFps;
        }
        else root = SceneAsset.Load(source, out sourceFps, forExport: true, sampleFps: options.SampleFps, ct: ct);

        var stage = new Node3D { Name = "Scene" };
        stage.AddChild(root);
        try
        {
            bool worldSpaceProp = forBlender && motion != null;
            if (worldSpaceProp && motion != null)
            {
                var tree = Engine.GetMainLoop() as SceneTree ?? throw new InvalidOperationException("动画导出需要运行应用程序。");
                tree.Root.AddChild(stage);
                var bridgeRate = Math.Min(600, Math.Max(options.SampleFps ?? 0, motion.Manifest.SourceFps * 2));
                MotionExport.BakeWorldSpaceProp(motion, bridgeRate, ct);
                motion.Skeleton.ResetBonePoses();
            }
            var player = motion?.Player ?? Preview3DViewport.Descendants(root).OfType<AnimationPlayer>().FirstOrDefault();
            var available = player?.GetAnimationList().Where(name => name != "RESET").ToArray() ?? [];
            var selected = options.Clips ?? available;
            var missing = selected.Except(available, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0) throw new IOException("资产中找不到片段：" + string.Join("、", missing));
            if (selected.Length == 0 && (animationLibrary || options.HasTimeOptions))
                throw new IOException("当前资产没有可导出的动画。");

            var prepared = new Dictionary<string, Animation>(StringComparer.Ordinal);
            if (player != null)
            {
                var animationRoot = player.GetNode(player.RootNode);
                foreach (string clip in selected)
                {
                    var animation = player.GetAnimation(clip);
                    bool IncludeTrack(int track)
                    {
                        var path = animation.TrackGetPath(track);
                        if (animationLibrary || options.IncludeMesh) return true;
                        if (animation.TrackGetType(track) == Animation.TrackType.BlendShape) return false;
                        // Keep transform animation on mesh nodes as those nodes become plain Node3Ds.
                        if (animation.TrackGetType(track) != Animation.TrackType.Value ||
                            animationRoot.GetNodeOrNull<MeshInstance3D>(path) == null) return true;
                        return path.GetConcatenatedSubNames().ToString() is "position" or "rotation" or "rotation_degrees" or
                            "quaternion" or "scale" or "transform" or "visible";
                    }
                    prepared.Add(clip, AnimationExport.Copy(animation, clip, options, IncludeTrack, ct));
                }
            }

            if (animationLibrary)
            {
                WriteLibrary(output, prepared);
                return;
            }

            if (player != null) ReplaceAnimations(player, prepared);
            if (!options.IncludeMesh)
            {
                foreach (var mesh in Preview3DViewport.Descendants(root).OfType<MeshInstance3D>().ToArray())
                {
                    var node = new Node3D { Name = mesh.Name, Transform = mesh.Transform, Visible = mesh.Visible };
                    foreach (var key in mesh.GetMetaList()) node.SetMeta(key, mesh.GetMeta(key));
                    var owner = mesh.Owner;
                    mesh.ReplaceBy(node);
                    node.Owner = owner;
                    if (mesh == root) root = node;
                    mesh.Free();
                }
                if (prepared.Count == 0 && !Preview3DViewport.Descendants(root).OfType<Skeleton3D>().Any(s => s.GetBoneCount() > 0))
                    throw new IOException("移除网格后没有可导出的骨架或动画。");
            }

            var rate = options.SampleFps ?? sourceFps;
            WriteScene(output, root is Skeleton3D ? stage : root, rate, worldSpaceProp, prepared.Count);
        }
        finally { stage.Free(); }
    }

    private static void ReplaceAnimations(AnimationPlayer player, Dictionary<string, Animation> prepared)
    {
        foreach (var name in player.GetAnimationLibraryList())
        {
            var library = player.GetAnimationLibrary(name);
            foreach (var key in library.GetAnimationList().ToArray())
            {
                var clip = name.IsEmpty ? key.ToString() : $"{name}/{key}";
                library.RemoveAnimation(key);
                if (prepared.TryGetValue(clip, out var animation)) library.AddAnimation(key, animation);
            }
        }
    }

    internal static void WriteLibrary(string output, IEnumerable<KeyValuePair<string, Animation>> clips)
    {
        using var library = new AnimationLibrary();
        foreach (var (clip, animation) in clips)
        {
            var key = clip.Replace('/', '_').Replace(':', '_').Replace('.', '_');
            var name = key;
            for (int suffix = 2; library.HasAnimation(name); suffix++) name = $"{key}_{suffix}";
            var error = library.AddAnimation(name, animation);
            if (error != Error.Ok) throw new IOException($"无法写入动画片段 {clip}：{error}");
        }
        var result = ResourceSaver.Save(library, output);
        if (result != Error.Ok) throw new IOException("动画库导出失败：" + result);
    }

    internal static void WriteScene(string output, Node3D root, double? fps, bool worldSpaceProp, int clips)
    {
        using var document = new GltfDocument();
        using var state = new GltfState();
        if (fps is double rate) state.BakeFps = (float)rate;
        var error = document.AppendFromScene(root, state);
        if (error != Error.Ok) throw new IOException("场景转换失败：" + error);

        // A separate skin declares every joint even when there is no mesh or a mesh uses only some bones.
        // Mesh skins are serialized after these declarations and retain their original bind data.
        var skins = state.GetSkins();
        foreach (var skeleton in state.GetSkeletons())
        {
            var joints = skeleton.GetJoints();
            if (joints.Length == 0) continue;
            var rig = skeleton.GetGodotSkeleton();
            var restSkin = new Skin();
            var binds = new Godot.Collections.Array<Transform3D>();
            for (int bone = 0; bone < joints.Length; bone++)
            {
                var bind = rig.GetBoneGlobalRest(bone).AffineInverse();
                restSkin.AddBind(bone, bind);
                binds.Add(bind);
            }
            var skin = new GltfSkin { ResourceName = rig.Name.ToString() };
            skin.SetGodotSkin(restSkin);
            skin.SetJoints(joints);
            skin.SetInverseBinds(binds);
            skins.Add(skin);
        }
        state.SetSkins(skins);

        var extras = new Godot.Collections.Dictionary { ["animanagerConfiguredExport"] = true };
        if (fps is double sourceFps) extras["animanagerSourceFps"] = sourceFps;
        if (worldSpaceProp) extras["animanagerIndependentProps"] = true;
        var json = state.Json;
        json["extras"] = extras;
        state.Json = json;
        error = document.WriteToFilesystem(state, output);
        if (error != Error.Ok) throw new IOException("GLB 导出失败：" + error);
        var count = state.Json.TryGetValue("animations", out var animations) ? animations.AsGodotArray().Count : 0;
        if (count != clips) throw new IOException("有选定片段无法写入 GLB 的动画轨道。");
    }
}
