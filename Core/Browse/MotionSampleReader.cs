using Animanager.Core.Motion;
using Animanager.Core.Retarget;
using Animanager.Core.Preview;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Core.Browse;

internal static class MotionSampleReader
{
    internal static MotionSamples? CurrentPose(Preview3DViewport preview)
    {
        foreach (var item in preview.Bones.Skeletons)
        {
            var skeleton = item.Node;
            var joints = Map(Enumerable.Range(0, skeleton.GetBoneCount()).Select(i => skeleton.GetBoneName(i).ToString()));
            if (joints.Length == 0) continue;
            var space = new MotionSimilarity.BodySpace(joints.Select(i => skeleton.GetBoneGlobalRest(i).Origin).ToArray());
            var pose = space.Encode(joints.Select(i => skeleton.GetBoneGlobalPose(i).Origin).ToArray());
            return new(preview.CurrentClip, 1, [pose, pose]);
        }
        return null;
    }

    internal static async Task<MotionSamples[]> Read(string path, SceneTree tree, CancellationToken ct)
    {
        if (MotionJson.IsMotion(path))
            return await Task.Run(() => ReadBody(path, ct), ct);

        using var source = await PreviewSource.LoadAsync(path, ct, fullTracks: true);
        ct.ThrowIfCancellationRequested();
        var nodes = Preview3DViewport.Descendants(source.Root).ToArray();
        var player = nodes.OfType<AnimationPlayer>().FirstOrDefault();
        if (player == null) return [];
        foreach (var skeleton in nodes.OfType<Skeleton3D>())
        {
            int[] joints = Map(Enumerable.Range(0, skeleton.GetBoneCount()).Select(i => skeleton.GetBoneName(i).ToString()));
            if (joints.Length == 0) continue;
            var baseline = new PoseSnapshot(source.Root);
            var space = new MotionSimilarity.BodySpace(joints.Select(i => skeleton.GetBoneGlobalRest(i).Origin).ToArray());
            var clips = new List<MotionSamples>();
            var viewport = new SubViewport { OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                ProcessMode = Node.ProcessModeEnum.Disabled };
            try
            {
                foreach (var mixer in nodes.OfType<AnimationMixer>()) mixer.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
                tree.Root.AddChild(viewport);
                viewport.AddChild(source.Root);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                foreach (string clip in player.GetAnimationList().Where(c => c != "RESET"))
                {
                    var animation = player.GetAnimation(clip);
                    if (!(animation.Length > 0) || !double.IsFinite(animation.Length)) continue;
                    var target = player.GetNode(player.RootNode);
                    bool TargetsSkeleton(int track)
                    {
                        var bone = animation.TrackGetPath(track);
                        return bone.GetSubNameCount() == 1 && target.GetNodeOrNull(bone.GetConcatenatedNames().ToString()) == skeleton;
                    }
                    if (!Enumerable.Range(0, animation.GetTrackCount()).Any(TargetsSkeleton)) continue;
                    player.Stop();
                    baseline.Restore(source.Root);
                    animation.LoopMode = Animation.LoopModeEnum.None;
                    player.Play(clip);
                    player.Advance(0);
                    var frames = new Vector3[MotionSamples.FrameCount(animation.Length, source.FrameRate)][];
                    var samples = new MotionSamples(clip, animation.Length, frames);
                    for (int i = 0; i < frames.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        player.Seek(samples.Time(i), update: true, updateOnly: true);
                        frames[i] = space.Encode(joints.Select(j => skeleton.GetBoneGlobalPose(j).Origin).ToArray());
                        if (timer.ElapsedMilliseconds < 12) continue;
                        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                        timer.Restart();
                    }
                    clips.Add(samples);
                }
            }
            finally
            {
                if (source.Root.GetParent() == viewport) viewport.RemoveChild(source.Root);
                viewport.Free();
            }
            if (clips.Count > 0) return clips.ToArray();
        }
        return [];
    }

    private static int[] Map(IEnumerable<string> bones)
    {
        var roles = bones.Select((bone, index) => (Role: HumanoidMap.Role(bone), index)).ToLookup(p => p.Role, p => p.index);
        if (MotionSimilarity.Roles.Any(role => roles[role].Count() != 1)) return [];
        var names = MotionSimilarity.Roles.AsEnumerable();
        if (MotionSimilarity.Fingers.All(role => roles[role].Count() == 1)) names = names.Concat(MotionSimilarity.Fingers);
        return names.Select(role => roles[role].Single()).ToArray();
    }

    // The split motion format already has an indexed joint hierarchy. Avoid loading its skinned mesh.
    internal static MotionSamples[] ReadBody(string path, CancellationToken ct)
    {
        var manifest = MotionJson.ReadManifest(path);
        var body = MotionGlb.Read(MotionJson.ResolveObject(path, manifest.Objects["body"]));
        var joints = Map(body.Nodes.Select(n => n.Name));
        if (joints.Length == 0) return [];
        var ordered = new List<int>();
        var seen = new HashSet<int>();
        foreach (int joint in joints)
        {
            var chain = new Stack<int>();
            var visiting = new HashSet<int>();
            for (int i = joint; i >= 0 && !seen.Contains(i); i = body.Nodes[i].Parent)
            {
                if (!visiting.Add(i)) throw new InvalidDataException("人体骨架包含循环。");
                chain.Push(i);
            }
            foreach (int i in chain) { seen.Add(i); ordered.Add(i); }
        }
        var world = new System.Numerics.Matrix4x4[body.Nodes.Length];
        Vector3[] Positions() => joints.Select(j =>
        {
            var v = world[j].Translation;
            return new Vector3(v.X, v.Y, v.Z);
        }).ToArray();
        void Set(int i, System.Numerics.Matrix4x4 local) =>
            world[i] = body.Nodes[i].Parent < 0 ? local : local * world[body.Nodes[i].Parent];

        foreach (int i in ordered)
        {
            var bone = body.Nodes[i];
            Set(i, System.Numerics.Matrix4x4.CreateScale(bone.Scale[0], bone.Scale[1], bone.Scale[2]) *
                System.Numerics.Matrix4x4.CreateFromQuaternion(MotionGlb.QuaternionOf(bone.Rotation)) *
                System.Numerics.Matrix4x4.CreateTranslation(bone.Position[0], bone.Position[1], bone.Position[2]));
        }
        var space = new MotionSimilarity.BodySpace(Positions());
        var frames = new Vector3[MotionSamples.FrameCount(manifest.Duration, manifest.SourceFps)][];
        var result = new MotionSamples(manifest.Clip, manifest.Duration, frames);
        for (int frame = 0; frame < frames.Length; frame++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (int i in ordered) Set(i, body.LocalAt(i, result.Time(frame)));
            frames[frame] = space.Encode(Positions());
        }
        return [result];
    }
}
