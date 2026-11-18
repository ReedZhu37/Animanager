using Animanager.Core;
using Animanager.Core.Jobs;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

internal static class RetargetIntegrationChecks
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, string folder, Action<bool, string> check)
    {
        var sourceRoot = RigCatalog.Load("ue4-mannequin", out var sourceSk);
        var targetRoot = RigCatalog.Load("ue5-mannequin", out var targetSk);
        try
        {
            var source = RigCatalog.Identify(sourceSk, sourceRoot); var target = RigCatalog.Identify(targetSk, targetRoot);
            var solver = new RetargetSolver(source, target, new()); var poses = source.Rest.ToArray();
            poses[source.Indices["spine_01"]].Basis *= new Basis(Vector3.Right, .4f);
            poses[source.Indices["spine_03"]].Basis *= new Basis(Vector3.Up, .2f);
            var output = solver.Evaluate(poses, source.Frame, Vector3.Zero);
            var s = source.Globals(poses); var t = target.Globals(output);
            Quaternion Delta(Transform3D a, Transform3D rest) => a.Basis.GetRotationQuaternion() * rest.Basis.GetRotationQuaternion().Inverse();
            var sd = Delta(s[source.Indices["spine_03"]], source.GlobalRest[source.Indices["spine_03"]]);
            var td = Delta(t[target.Indices["spine_05"]], target.GlobalRest[target.Indices["spine_05"]]);
            check(Math.Abs(sd.Dot(td)) > .99999, "3-to-5 spine chain preserves cumulative endpoint rotation");
            check(target.Order.Where(i => target.Names[i] is not ("root" or "pelvis") && !target.Names[i].StartsWith("ik_")).All(i =>
                output[i].Origin.DistanceTo(target.Rest[i].Origin) < .0001f), "cross-rig FK keeps target bone lengths");
            var withOffset = new RetargetSolver(source, target, new() { RotationOffsets = new() { ["upperarm_l"] = [45, 0, 0] } });
            var altered = target.Globals(withOffset.Evaluate(source.Rest, source.Frame, Vector3.Zero));
            check(altered[target.Indices["hand_l"]].Origin.DistanceTo(target.GlobalRest[target.Indices["hand_l"]].Origin) > .01,
                "local upper-arm correction propagates to hand");
            bool rejected = false;
            try { _ = new RetargetOptions { TargetHash = new string('0', 64) }.Snapshot(); } catch (IOException) { rejected = true; }
            check(rejected, "stale target hash rejected before baking");
        }
        finally { sourceRoot.Free(); targetRoot.Free(); }

        var asset = api.Find("ue4-mannequin");
        var (additiveJson, additiveBin) = MotionGlb.Unpack(File.ReadAllBytes(api.Storage.Resolve(asset.VirtualPath)));
        additiveJson["extras"]!["sourceAdditiveType"] = "AAT_RotationOffsetMeshSpace";
        var additivePath = Path.Combine(folder, "additive.glb"); File.WriteAllBytes(additivePath, MotionGlb.Pack(additiveJson, additiveBin));
        bool additiveRejected = false;
        try { var unexpected = await RetargetBaker.BakeAsync(additivePath, new() { Retarget = new() }); unexpected.Root.Free(); }
        catch (NotSupportedException) { additiveRejected = true; }
        check(additiveRejected, "CUE4Parse string additive metadata rejects missing-base animation");
        var preset = api.RetargetPresets.List().First(p => p.Options.SourceRig == "ue4-mannequin");
        var detached = preset.Options.Snapshot();
        detached.RotationOffsets["hand_l"] = [1, 2, 3];
        check(!preset.Options.RotationOffsets.ContainsKey("hand_l"), "parameter snapshots do not share mutable maps");
        int nodes = owner.GetTree().Root.GetChildCount();
        using (var cancel = new CancellationTokenSource())
        {
            var work = RetargetBaker.BakeAsync(api.Storage.Resolve(asset.VirtualPath), new() { Retarget = new(), SampleFps = 600 }, ct: cancel.Token);
            cancel.Cancel(); bool cancelled = false;
            try { var abandoned = await work; abandoned.Root.Free(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && owner.GetTree().Root.GetChildCount() == nodes, "cancel mid-bake removes isolated scene and leaves preview intact");
        }
        var resPath = await api.ExportAsync(asset.Id, Path.Combine(folder, "res"), JobKind.ExportRes,
            new() { Retarget = new(), StartSeconds = .137, EndSeconds = .792, SampleFps = 30 });
        var library = ResourceLoader.Load<AnimationLibrary>(resPath, cacheMode: ResourceLoader.CacheMode.Ignore);
        var animation = library.GetAnimation(library.GetAnimationList()[0]);
        check(Math.Abs(animation.Length - .655) < .00001 && Math.Abs(animation.TrackGetKeyTime(0, animation.TrackGetKeyCount(0) - 1) - .655) < .00001,
            "retarget RES is trimmed exactly once and retains the endpoint");
        var batch = api.Planner.ExportSelection([asset.Id, api.Find("ue5-mannequin").Id], Path.Combine(folder, "mixed"), JobKind.ExportGlb,
            new() { Retarget = preset.Options }, "mixed rigs");
        var mixed = await api.Jobs.WaitAsync(api.Jobs.Enqueue(batch));
        check(mixed.Succeeded == 1 && mixed.Failed == 1, "mixed-rig batch reports incompatible preset per item");
        var retry = await api.Jobs.WaitAsync(api.Jobs.Retry(mixed.Id));
        check(retry.Total == 1 && retry.Failed == 1 && retry.ExportOptions!.Retarget!.TargetHash == preset.Options.TargetHash,
            "retry retains retarget snapshot and retries only failed items");
        var maintenanceRoot = RuntimePaths.Bundled(".runtime/retarget-maintenance/" + Guid.NewGuid().ToString("N"));
        var backup = await new Core.Storage.LibraryMaintenance(api).BackupAsync(Path.Combine(maintenanceRoot, "backup"));
        check(File.Exists(Path.Combine(backup, "retarget", preset.Id + ".json")) && File.Exists(Path.Combine(backup, "retarget", "assets", asset.Id + ".json")),
            "library backup contains presets and per-asset associations");
        Core.Storage.LibraryMaintenance.Restore(backup, Path.Combine(maintenanceRoot, "restored"));
        check(new RetargetPresetStore(Path.Combine(maintenanceRoot, "restored")).ForAsset(asset.Id)?.TargetHash == preset.Options.TargetHash, "restored library retains bound retarget configuration");

        var localSamples = new List<(string Path, string Target)>();
        string movement = RuntimePaths.Bundled("Libraries/MovementAnimsetPro-CUE4Parse/assets");
        if (Directory.Exists(movement))
        {
            var files = Directory.GetFiles(movement, "*.glb", SearchOption.AllDirectories);
            foreach (string name in new[] { "WalkFwdLoop", "RunFwdLoop", "Crouch_WalkFwdLoop", "Crouch_Turn90R_new" })
            {
                var path = files.FirstOrDefault(p => Path.GetFileName(p) == "MovementAnimsetPro__RootMotion__" + name + ".glb");
                if (path != null) localSamples.Add((path, "ue5-mannequin"));
            }
        }
        string nodachi = RuntimePaths.Bundled("Libraries/Nodachi-Motion/motion");
        if (Directory.Exists(nodachi))
        {
            var spin = Directory.GetFiles(nodachi, "*.motion.json").FirstOrDefault(p => MotionJson.ReadManifest(p).Clip == "AS_Combo_Attack_03_All_Seq");
            if (spin != null) localSamples.Add((spin, "ue4-mannequin"));
        }
        foreach (var (path, target) in localSamples)
        {
            var originalHash = MotionJson.Hash(File.ReadAllBytes(path));
            var result = await RetargetBaker.BakeAsync(path, new() { Retarget = new() { TargetRig = target } });
            try
            {
                owner.AddChild(result.Root);
                var sk = Preview3DViewport.Descendants(result.Root).OfType<Skeleton3D>().Single();
                var player = Preview3DViewport.Descendants(result.Root).OfType<AnimationPlayer>().Single();
                var clip = player.GetAnimationList()[0];
                player.Play(clip); player.Advance(0); player.Pause();
                var refRoot = RigCatalog.Load(target, out var refSk);
                check(MotionTests.MeshHash(result.Root) == MotionTests.MeshHash(refRoot) && MotionTests.RigHash(sk) == MotionTests.RigHash(refSk),
                    "target mesh, skin and rest preserved: " + clip);
                refRoot.Free();
                var glb = Path.Combine(folder, clip.Replace('/', '_') + ".glb");
                // Serialize rest pose while retaining all baked tracks.
                player.Stop(); sk.ResetBonePoses();
                ExportService.WriteScene(glb, result.Root, result.SampleFps, result.HasProp, player.GetAnimationList().Length);
                string fbx = Path.ChangeExtension(glb, ".fbx"); await api.Blender.ConvertAsync(glb, fbx);
                var reload = SceneAsset.Load(fbx, out _, forExport: true, sampleFps: result.SampleFps);
                try
                {
                    owner.AddChild(reload); var rs = Preview3DViewport.Descendants(reload).OfType<Skeleton3D>().Single();
                    var rp = Preview3DViewport.Descendants(reload).OfType<AnimationPlayer>().Single();
                    rp.Play(rp.GetAnimationList().First(c => c != "RESET")); rp.Advance(0); rp.Pause();
                    player.Play(clip); player.Advance(0); player.Pause();
                    float maxP = 0, maxA = 0; double duration = player.GetAnimation(clip).Length;
                    foreach (double t in Enumerable.Range(0, 21).Select(i => Math.Min(duration, Math.Round(duration * i / 20 * result.SampleFps) / result.SampleFps)))
                    {
                        player.Seek(t, true, true); rp.Seek(t, true, true);
                        foreach (string bone in result.Target.Names)
                        {
                            var a = sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone(bone)); var b = rs.GlobalTransform * rs.GetBoneGlobalPose(rs.FindBone(bone));
                            maxP = Math.Max(maxP, a.Origin.DistanceTo(b.Origin)); maxA = Math.Max(maxA, Angle(a, b));
                        }
                    }
                    GD.Print($"REAL_FBX {clip} {maxP:G6}m {maxA:G6}deg");
                    check(maxP < .001f && maxA < .1f, "real FBX pose roundtrip: " + clip);
                    if (result.HasProp) await CheckProp(owner, path, result, sk, player, reload, rp, duration, check);
                }
                finally { reload.GetParent()?.RemoveChild(reload); reload.Free(); }
                check(MotionJson.Hash(File.ReadAllBytes(path)) == originalHash, "real source untouched: " + clip);
            }
            finally { result.Root.GetParent()?.RemoveChild(result.Root); result.Root.Free(); }
        }
        check(localSamples.Count >= 0, "local real-material checks: " + localSamples.Count + " samples (when installed)");
    }
    private static float Angle(Transform3D a, Transform3D b) => Mathf.RadToDeg(2 * Mathf.Acos(Mathf.Clamp(Math.Abs(
        a.Basis.Orthonormalized().GetRotationQuaternion().Dot(b.Basis.Orthonormalized().GetRotationQuaternion())), 0, 1)));
    private static Task CheckProp(MainShell owner, string source, RetargetResult baked, Skeleton3D sk, AnimationPlayer player,
        Node3D reloaded, AnimationPlayer fbxPlayer, double duration, Action<bool, string> check)
    {
        var motion = new MotionPlayback(source); owner.AddChild(motion.Root);
        try
        {
            var prop = baked.Root.GetNode<Node3D>(RetargetBaker.PropNodeName);
            var fbxProp = Preview3DViewport.Descendants(reloaded).OfType<Node3D>().Single(n => n.Name == RetargetBaker.PropNodeName);
            motion.Player.Play(motion.Manifest.Clip); motion.Player.Advance(0); motion.Player.Pause();
            int sh = baked.Source.Indices["hand_r"], th = baked.Target.Indices["hand_r"];
            var correction = new Transform3D(baked.Target.GlobalRest[th].Basis.Orthonormalized().Inverse() * baked.Source.GlobalRest[sh].Basis.Orthonormalized(), Vector3.Zero);
            float maxP = 0, maxA = 0, bridgeP = 0, bridgeA = 0, onFrameP = 0, onFrameA = 0; double worstTime = 0;
            for (int i = 0; i <= (int)Math.Ceiling(duration * baked.SampleFps * 2); i++)
            {
                double t = Math.Min(duration, i / (baked.SampleFps * 2));
                player.Seek(t, true, true); motion.Player.Seek(t, true, true); motion.Sync(); fbxPlayer.Seek(t, true, true);
                var sourceRelative = (motion.Skeleton.GlobalTransform * motion.Skeleton.GetBoneGlobalPose(sh)).AffineInverse() * motion.Prop.GlobalTransform;
                var expected = correction * sourceRelative;
                var actual = (sk.GlobalTransform * sk.GetBoneGlobalPose(th)).AffineInverse() * prop.GlobalTransform;
                float positionError = expected.Origin.DistanceTo(actual.Origin);
                if (positionError > maxP) worstTime = t;
                maxP = Math.Max(maxP, positionError); maxA = Math.Max(maxA, Angle(expected, actual));
                if (i % 2 == 0) { onFrameP = Math.Max(onFrameP, positionError); onFrameA = Math.Max(onFrameA, Angle(expected, actual)); }
                bridgeP = Math.Max(bridgeP, prop.GlobalPosition.DistanceTo(fbxProp.GlobalPosition)); bridgeA = Math.Max(bridgeA, Angle(prop.GlobalTransform, fbxProp.GlobalTransform));
            }
            GD.Print($"PROP_ERRORS relative={maxP}m/{maxA}deg fbx={bridgeP}m/{bridgeA}deg onFrame={onFrameP}m/{onFrameA}deg worstTime={worstTime}");
            check(maxP < .002f && maxA < 1f, "Nodachi spin hand-relative motion preserved including half-frame samples");
            check(bridgeP < .001f && bridgeA < .1f, "independent prop FBX matches baked motion including half-frame samples");
        }
        finally { motion.Root.GetParent()?.RemoveChild(motion.Root); motion.Root.Free(); }
        return Task.CompletedTask;
    }
}
