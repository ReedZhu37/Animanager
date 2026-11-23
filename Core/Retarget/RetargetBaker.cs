using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Godot;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Preview;
using Animanager.UI.Components;

namespace Animanager.Core.Retarget;

public sealed record RetargetResult(Node3D Root, RigDescriptor Source, RigDescriptor Target, string[] Diagnostics, double SampleFps, bool HasProp);

public static class RetargetBaker
{
    public const string PropNodeName = "PrimaryWeaponMotion";
    private const int PreviewBatchSize = 48;
    private sealed record SamplePose(Transform3D[] Bones)
    {
        internal double Time;
        internal Transform3D Frame;
        internal Transform3D Prop;
        internal Transform3D[] Solved = [];
    }
    public static Task<RetargetResult> BakeAsync(string source, ExportOptions export, IProgress<string>? progress = null, CancellationToken ct = default)
        => BakeCoreAsync(source, null, export, progress, ct);

    internal static Task<RetargetResult> BakePreviewAsync(string source, RetargetOptions options, CancellationToken ct,
        PreviewSource? prepared = null, RigDescriptor? sourceRig = null, Func<Task<Node3D>>? loadTarget = null)
        => BakeCoreAsync(source, null, new() { Retarget = options, IncludeMesh = true }, null, ct, preview: true, prepared, sourceRig, loadTarget);

    // Explicit custom target: TargetRig chooses the required naming/hierarchy family, not replacement geometry.
    // Callers own the target-file identity; this entry point does not create or persist a catalogue preset.
    public static Task<RetargetResult> BakeToTargetAsync(string source, string targetFile, ExportOptions export,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => BakeCoreAsync(source, Path.GetFullPath(targetFile), export, progress, ct);

    private static async Task<RetargetResult> BakeCoreAsync(string source, string? targetFile, ExportOptions export, IProgress<string>? progress,
        CancellationToken ct, bool preview = false, PreviewSource? prepared = null, RigDescriptor? sourceRig = null,
        Func<Task<Node3D>>? loadTarget = null)
    {
        export = export.Snapshot(animationLibrary: false);
        var options = export.Retarget ?? throw new ArgumentException("Retarget configuration is required.");
        targetFile ??= loadTarget != null ? options.Character?.ModelPath : options.Character?.VerifiedPath();
        var tree = Engine.GetMainLoop() as SceneTree ?? throw new InvalidOperationException("Retarget requires the Godot runtime.");
        // Startup preview may be requested from MainShell._Ready while the root still adds children.
        ct.ThrowIfCancellationRequested();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        ct.ThrowIfCancellationRequested();
        var stage = new Node3D { Visible = false }; tree.Root.AddChild(stage);
        Node3D? output = null;
        PoseSnapshot? baseline = null;
        AnimationPlayer? player = null;
        Animation? sampling = null;
        Animation.LoopModeEnum sourceLoop = default;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (prepared == null) RejectAdditive(source);
            var motion = prepared != null ? prepared.Motion : MotionJson.IsMotion(source) ? new MotionPlayback(source) : null;
            float? sourceRate = prepared?.ReportedFrameRate;
            var sourceRoot = prepared?.Root ?? motion?.Root ?? SceneAsset.Load(source, out sourceRate, forExport: true, sampleFps: export.SampleFps, ct: ct);
            stage.AddChild(sourceRoot);
            if (sourceRig == null)
            {
                var readiness = AssetReadiness.Inspect(sourceRoot, true);
                if (!readiness.Ready) throw new NotSupportedException(readiness.Summary);
            }
            var skeletons = Preview3DViewport.Descendants(sourceRoot).OfType<Skeleton3D>().ToArray();
            if (skeletons.Length != 1) throw new NotSupportedException("Retarget 首版需要一个人体 Skeleton。");
            var sk = skeletons[0]; var src = sourceRig ?? HumanoidMap.Describe(sk, sourceRoot);
            player = Preview3DViewport.Descendants(sourceRoot).OfType<AnimationPlayer>().FirstOrDefault()
                ?? throw new IOException("源资产没有动画播放器。");
            baseline = new PoseSnapshot(sourceRoot);
            output = new Node3D { Name = "Retargeted" }; stage.AddChild(output);
            var character = loadTarget != null ? await loadTarget() :
                targetFile == null ? RigCatalog.Load(options.TargetRig, out _) : SceneAsset.Load(targetFile, out _, forExport: true);
            character.Name = "Character"; output.AddChild(character);
            ct.ThrowIfCancellationRequested();
            var targetSkeletons = Preview3DViewport.Descendants(character).OfType<Skeleton3D>().ToArray();
            if (targetSkeletons.Length != 1) throw new NotSupportedException("目标角色需要一个人体 Skeleton。");
            var target = targetSkeletons[0];
            foreach (var existing in Preview3DViewport.Descendants(character).OfType<AnimationPlayer>().ToArray())
            { existing.GetParent().RemoveChild(existing); existing.Free(); }
            target.ResetBonePoses();
            var dst = new RigDescriptor(target, output) { Id = targetFile == null ? options.TargetRig : "custom:" + Path.GetFileNameWithoutExtension(targetFile) };
            HumanoidMap.RequireReady(dst);
            var solver = new RetargetSolver(src, dst, options);
            progress?.Report($"Retarget {src.Id} ({src.Names.Length}) → {dst.Id} ({dst.Names.Length}); {solver.Diagnostics.Length} 个辅助骨保留参考姿势；无接触 IK。");
            foreach (var diagnostic in solver.Diagnostics) progress?.Report(diagnostic);
            var bakedPlayer = new AnimationPlayer { Name = "RetargetPlayer", RootNode = new NodePath("..") }; output.AddChild(bakedPlayer);
            Node3D? prop = null;
            if (motion != null && options.IncludeProps)
            {
                prop = new Node3D { Name = PropNodeName }; output.AddChild(prop);
                prop.AddChild(motion.Grip.Duplicate());
            }
            // Flattening hand-relative prop motion into independent world tracks is nonlinear.
            // Dense defaults preserve fast spins between keys; an explicit export rate remains authoritative.
            double rate = export.SampleFps ?? (prop != null ? 600 : motion?.Manifest.SourceFps ?? sourceRate ?? 60);
            var available = player.GetAnimationList().Where(c => c != "RESET").ToArray();
            var selected = export.Clips ?? available;
            if (selected.Length == 0 || selected.Any(c => !available.Contains(c))) throw new IOException("源动画不存在所选片段。");
            int sourceHand = prop != null ? src.Indices["hand_r"] : -1;
            int targetHand = prop != null ? dst.Indices["hand_r"] : -1;
            var handChain = new List<int>();
            for (int bone = targetHand; bone >= 0; bone = dst.Parents[bone]) handChain.Add(bone);
            handChain.Reverse();
            var propCorrection = prop != null
                ? new Transform3D(dst.GlobalRest[targetHand].Basis.Orthonormalized().Inverse() * src.GlobalRest[sourceHand].Basis.Orthonormalized(), Vector3.Zero)
                : Transform3D.Identity;
            var budget = Stopwatch.StartNew();
            foreach (string clip in selected)
            {
                ct.ThrowIfCancellationRequested();
                var animation = player.GetAnimation(clip);
                double start = export.StartSeconds ?? 0, end = export.EndSeconds ?? animation.Length;
                if (start < 0 || end > animation.Length + 1e-6 || end - start < .001) throw new IOException($"片段 {clip} 的裁剪区间无效。");
                end = Math.Min(end, animation.Length); double duration = end - start;
                int intervals = checked((int)Math.Ceiling(duration * rate));
                if ((long)(intervals + 1) * dst.Names.Length > 4_000_000) throw new IOException("Retarget 超过每片段 4,000,000 姿态采样预算，请裁剪片段或降低帧率。");
                var baked = new Animation { Length = (float)duration, Step = (float)(1 / rate),
                    LoopMode = start == 0 && end == animation.Length ? animation.LoopMode : Animation.LoopModeEnum.None };
                // Disable source looping so the exact end pose is sampled instead of wrapping to frame zero.
                sampling = animation; sourceLoop = animation.LoopMode;
                animation.LoopMode = Animation.LoopModeEnum.None;
                player.Stop(); baseline.Restore(sourceRoot); player.Play(clip); player.Advance(0); player.Pause();
                var tracks = dst.Names.Select(n => AddTracks(baked, new NodePath(output.GetPathTo(target) + ":" + n))).ToArray();
                var propTracks = prop != null ? AddTracks(baked, output.GetPathTo(prop)) : null;
                Sample(start);
                var initialRoot = RigDescriptor.SceneFrame(sk, sourceRoot) * sk.GetBoneGlobalPose(src.Indices["root"]);
                var initialDisplacement = initialRoot.Origin - src.GlobalRest[src.Indices["root"]].Origin;
                bool parallelPreview = preview && intervals >= PreviewBatchSize && System.Environment.ProcessorCount > 1;
                var batch = Enumerable.Range(0, parallelPreview ? PreviewBatchSize : 1)
                    .Select(_ => new SamplePose(new Transform3D[src.Names.Length])).ToArray();
                var workers = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 };
                for (int first = 0; first <= intervals; first += batch.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int last = Math.Min(first + batch.Length - 1, intervals);
                    int count = 0;
                    for (int sample = first; sample <= last; sample++)
                    {
                        double t = sample == intervals ? duration : Math.Min(sample / rate, duration);
                        if (sample < intervals && (float)t >= (float)duration) continue;
                        Sample(start + t);
                        var pose = batch[count++];
                        pose.Time = t;
                        pose.Frame = RigDescriptor.SceneFrame(sk, sourceRoot);
                        for (int i = 0; i < pose.Bones.Length; i++) pose.Bones[i] = sk.GetBonePose(i);
                        if (propTracks != null) pose.Prop = motion!.Prop.Transform;
                    }
                    if (count == 0) continue;
                    // Only the FK math runs on workers. Sampling Godot nodes and writing animation keys stay on the main thread.
                    if (parallelPreview)
                    {
                        try { await Task.Run(() => Parallel.For(0, count, workers, Solve), ct); }
                        catch (AggregateException e) { ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw(); }
                    }
                    else Solve(0);

                    ct.ThrowIfCancellationRequested();
                    for (int frame = 0; frame < count; frame++)
                    {
                        var pose = batch[frame];
                        for (int i = 0; i < pose.Solved.Length; i++) Insert(baked, tracks[i], pose.Time, pose.Solved[i]);
                        if (propTracks != null)
                        {
                            var hand = dst.Frame;
                            foreach (int bone in handChain) hand *= pose.Solved[bone];
                            Insert(baked, propTracks, pose.Time, hand * propCorrection * pose.Prop);
                        }
                    }
                    if (!parallelPreview && (last % 64 == 63 || budget.ElapsedMilliseconds > 8))
                    {
                        progress?.Report($"Retarget {clip}: {last + 1}/{intervals + 1}");
                        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); budget.Restart();
                    }
                }
                AddAnimation(bakedPlayer, clip, baked);
                animation.LoopMode = sourceLoop; sampling = null;
                void Sample(double t) { baseline.Restore(sourceRoot); player.Seek(t, true, true); }
                void Solve(int index)
                {
                    var pose = batch[index];
                    pose.Solved = solver.Evaluate(pose.Bones, pose.Frame, initialDisplacement);
                }
            }
            target.ResetBonePoses();
            if (!export.IncludeMesh)
                foreach (var mesh in Preview3DViewport.Descendants(output).OfType<MeshInstance3D>().ToArray())
                { var node = new Node3D { Name = mesh.Name, Transform = mesh.Transform, Visible = mesh.Visible }; mesh.ReplaceBy(node); mesh.Free(); }
            foreach (var node in Preview3DViewport.Descendants(output).Skip(1)) node.Owner = output;
            stage.RemoveChild(output);
            return new(output, src, dst, solver.Diagnostics, rate, prop != null);
        }
        catch { if (output != null && output.GetParent() == null) output.Free(); throw; }
        finally
        {
            if (prepared != null && prepared.Root.GetParent() == stage)
            {
                if (sampling != null) sampling.LoopMode = sourceLoop;
                player?.Stop();
                baseline?.Restore(prepared.Root);
                stage.RemoveChild(prepared.Root);
            }
            stage.GetParent()?.RemoveChild(stage); stage.Free();
        }
    }
    internal static void AddAnimation(AnimationPlayer player, string clip, Animation animation)
    {
        int slash = clip.IndexOf('/'); string libraryName = slash < 0 ? "" : clip[..slash], name = slash < 0 ? clip : clip[(slash + 1)..];
        if (!player.HasAnimationLibrary(libraryName)) player.AddAnimationLibrary(libraryName, new AnimationLibrary());
        var error = player.GetAnimationLibrary(libraryName).AddAnimation(name, animation);
        if (error != Error.Ok) throw new IOException("无法创建烘焙动画：" + clip);
    }
    private static int[] AddTracks(Animation animation, NodePath path)
    {
        var types = new[] { Animation.TrackType.Position3D, Animation.TrackType.Rotation3D, Animation.TrackType.Scale3D };
        return types.Select(type => { int i = animation.AddTrack(type); animation.TrackSetPath(i, path); animation.TrackSetInterpolationType(i, Animation.InterpolationType.Linear); return i; }).ToArray();
    }
    private static void Insert(Animation a, int[] tracks, double time, Transform3D pose)
    {
        if (!pose.IsFinite()) throw new IOException("重定向生成了无效姿态。");
        a.PositionTrackInsertKey(tracks[0], time, pose.Origin);
        a.RotationTrackInsertKey(tracks[1], time, pose.Basis.Orthonormalized().GetRotationQuaternion().Normalized());
        a.ScaleTrackInsertKey(tracks[2], time, pose.Basis.Scale);
    }
    internal static void RejectAdditive(string path)
    {
        if (!Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase)) return;
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
        if (file.Length < 20) return;
        file.Position = 12; int size = reader.ReadInt32(); file.Position = 20;
        if (size < 0 || size > file.Length - 20) throw new IOException("Invalid GLB header.");
        using var json = System.Text.Json.JsonDocument.Parse(reader.ReadBytes(size));
        if (json.RootElement.TryGetProperty("extras", out var extras) && extras.TryGetProperty("sourceAdditiveType", out var additive))
        {
            bool regular = additive.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => additive.TryGetInt32(out int value) && value == 0,
                System.Text.Json.JsonValueKind.String => additive.GetString() == "AAT_None",
                _ => false
            };
            if (!regular) throw new NotSupportedException("加法动画缺少基准姿势，不能作为普通动画重定向。");
        }
    }
}
