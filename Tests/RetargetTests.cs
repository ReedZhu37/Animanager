using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Shell;

namespace Animanager.Tests;

public static class RetargetTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        var folder = Path.Combine(api.Storage.Root, "retarget-validation"); Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); GD.Print("PASS " + message); }
        try
        {
            foreach (string id in RigCatalog.Ids)
            {
                var root = RigCatalog.Load(id, out var sk);
                try
                {
                    var rig = RigCatalog.Identify(sk, root);
                    Check(rig.Id == id && sk.GetBoneCount() == (id == RigCatalog.Ids[0] ? 68 : 161), id + " full reference rig recognized");
                    var solver = new RetargetSolver(rig, rig, new() { TargetRig = id });
                    var pose = solver.Evaluate(rig.Rest, rig.Frame, Vector3.Zero);
                    Check(pose.Zip(rig.Rest).All(p => Near(p.First, p.Second, .0001f, .001f)), id + " rest pose identity");
                    var animated = rig.Rest.ToArray();
                    animated[rig.Indices["spine_01"]].Basis *= new Basis(Vector3.Right, .25f);
                    animated[rig.Indices["root"]].Origin += new Vector3(.2f, .1f, 0);
                    pose = solver.Evaluate(animated, rig.Frame, Vector3.Zero);
                    Check(pose.Zip(animated).All(p => Near(p.First, p.Second, .0001f, .001f)), id + " animated identity includes helpers and translation");
                    var bad = sk.FindBone("pelvis"); var rest = sk.GetBoneRest(bad); sk.SetBoneRest(bad, new Transform3D(rest.Basis, rest.Origin * 1.2f));
                    bool rejected = false; try { RigCatalog.Identify(sk, root); } catch (NotSupportedException) { rejected = true; }
                    Check(rejected, "custom proportions rejected: " + id);
                }
                finally { root.Free(); }
            }
            foreach (string filename in new[] { "UE4_Mannequin.fbx", "UE5_Manny_Idle.fbx" })
            {
                var root = SceneAsset.Load(RuntimePaths.Bundled("BuiltinAssets/Baselines/" + filename), out _);
                try {
                    var sk = Preview3DViewport.Descendants(root).OfType<Skeleton3D>().Single();
                    var rig = RigCatalog.Identify(sk, root); Check(rig.Id.Length > 0, "native FBX reference recognized: " + filename);
                }
                finally { root.Free(); }
            }
            foreach (var (sourceId, targetId) in new[] { (RigCatalog.Ids[0], RigCatalog.Ids[1]), (RigCatalog.Ids[1], RigCatalog.Ids[0]) })
            {
                string file = Fixture(Path.Combine(folder, sourceId + ".glb"), sourceId);
                var bytes = File.ReadAllBytes(file);
                var imported = await api.ImportAsync([file]); Check(imported.Errors.Count == 0, "import animated " + sourceId);
                var asset = api.Find(sourceId);
                var options = new ExportOptions { Retarget = new() { TargetRig = targetId }, SampleFps = 60, IncludeMesh = false };
                var result = await RetargetBaker.BakeAsync(file, options);
                try
                {
                    owner.AddChild(result.Root);
                    var player = Preview3DViewport.Descendants(result.Root).OfType<AnimationPlayer>().Single();
                    var sk = Preview3DViewport.Descendants(result.Root).OfType<Skeleton3D>().Single();
                    Check(sk.GetBoneCount() == (targetId == RigCatalog.Ids[0] ? 68 : 161), sourceId + " → " + targetId + " complete target skeleton");
                    Check(!Preview3DViewport.Descendants(result.Root).OfType<MeshInstance3D>().Any(), "mesh-free bake retains skeleton");
                    player.Play("Test"); player.Advance(0); player.Pause(); player.Seek(.5, true, true);
                    var before = sk.GetBoneGlobalPose(sk.FindBone("head")); player.Seek(.75, true, true);
                    Check(!before.IsEqualApprox(sk.GetBoneGlobalPose(sk.FindBone("head"))), "baked spine/head motion changes over time");
                    var rootPose = sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("root"));
                    Check(Math.Abs(rootPose.Origin.X - .75f) < .001, "root travel preserved in metres");
                    Check(player.GetAnimation("Test").TrackGetKeyTime(0, player.GetAnimation("Test").TrackGetKeyCount(0) - 1) == 1, "exact final bake sample");
                    var output = await api.ExportAsync(asset.Id, Path.Combine(folder, "fbx"), JobKind.ExportFbx, options);
                    var loaded = SceneAsset.Load(output, out _, forExport: true, sampleFps: 60);
                    try
                    {
                        owner.AddChild(loaded);
                        var fbxSk = Preview3DViewport.Descendants(loaded).OfType<Skeleton3D>().Single();
                        var fbxPlayer = Preview3DViewport.Descendants(loaded).OfType<AnimationPlayer>().Single();
                        var fbxClip = fbxPlayer.GetAnimationList().First(c => c != "RESET");
                        fbxPlayer.Play(fbxClip); fbxPlayer.Advance(0); fbxPlayer.Pause();
                        Check(fbxSk.GetBoneCount() == sk.GetBoneCount() && result.Target.Names.All(n => fbxSk.FindBone(n) >= 0), "FBX contains exactly the target bones");
                        float maxP = 0, maxA = 0;
                        foreach (double t in new[] { 0, .25, .5, .75, 1 })
                        {
                            player.Seek(t, true, true); fbxPlayer.Seek(t, true, true);
                            foreach (string name in result.Target.Names)
                            {
                                var a = sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone(name));
                                var b = fbxSk.GlobalTransform * fbxSk.GetBoneGlobalPose(fbxSk.FindBone(name));
                                maxP = Math.Max(maxP, a.Origin.DistanceTo(b.Origin)); maxA = Math.Max(maxA, Angle(a, b));
                            }
                        }
                        GD.Print($"FBX_ERRORS {sourceId} p={maxP} angle={maxA}");
                        Check(maxP < .001f && maxA < .1f, "FBX matches bake within 1 mm / 0.1 degrees");
                    }
                    finally { loaded.GetParent()?.RemoveChild(loaded); loaded.Free(); }
                    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "source file unchanged");
                }
                finally { result.Root.GetParent()?.RemoveChild(result.Root); result.Root.Free(); }
                var inPlace = await RetargetBaker.BakeAsync(file, options with { Retarget = options.Retarget with { InPlace = true }, StartSeconds = .2, EndSeconds = .8 });
                try
                {
                    owner.AddChild(inPlace.Root); var player = Preview3DViewport.Descendants(inPlace.Root).OfType<AnimationPlayer>().Single();
                    var sk = Preview3DViewport.Descendants(inPlace.Root).OfType<Skeleton3D>().Single(); player.Play("Test"); player.Advance(0); player.Pause();
                    player.Seek(0, true, true); var a = sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("root"));
                    player.Seek(.6, true, true); var b = sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("root"));
                    Check(Math.Abs(a.Origin.X - b.Origin.X) < .0001 && Math.Abs(a.Origin.Y - b.Origin.Y) > .01, "in-place removes horizontal movement and keeps vertical movement");
                }
                finally { inPlace.Root.GetParent()?.RemoveChild(inPlace.Root); inPlace.Root.Free(); }
                var preset = api.RetargetPresets.Save("Test " + sourceId, api.InspectRetarget(asset.Id, options.Retarget!));
                api.RetargetPresets.Associate(asset.Id, preset.Options);
                Check(api.RetargetPresets.ForAsset(asset.Id)?.TargetHash == RigCatalog.Hash(targetId), "asset configuration persists target hash");
                var plan = api.Planner.ExportSingle(asset.Id, Path.Combine(folder, "batch"), JobKind.ExportGlb, options);
                Check(plan.ExportOptions!.Retarget!.TargetHash.Length == 64, "batch snapshots target identity");
                var job = await api.Jobs.WaitAsync(api.Jobs.Enqueue(plan));
                Check(job.State == JobState.Completed, "retarget uses existing queued export: " + string.Join("; ", job.Items.Select(i => i.Error)));
            }
            var real = RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx");
            await RetargetIntegrationChecks.Run(owner, preview, api, folder, Check);
            await CheckUi(owner, preview, api, commands, folder, Check);
            await api.ImportAsync([real]);
            owner.GetNode("/root/LimboConsole").Call("execute_command", "retarget preview UE5_Manny_Idle ue4-mannequin");
            await commands.PendingTask;
            preview.Seek(.5); Check(preview.BoneCount == 68 && preview.AppliedRetarget != null, "real Manny Idle preview attached to UE4 target");
            preview.SetRetargetView(0); preview.SetRetargetView(1); preview.SetRetargetView(2);
            var viewControl = (OptionButton)owner.Workspace.Panel("retarget").FindChild("RetargetView", true, false)!;
            viewControl.Select(2); viewControl.EmitSignal(OptionButton.SignalName.ItemSelected, 2L);
            Check(((Label)owner.Workspace.Panel("retarget").FindChild("RetargetInfo", true, false)!).Text.StartsWith("ue5-mannequin → ue4-mannequin"),
                "console retarget updates panel diagnostics for the current source and target");
            if (DisplayServer.GetName() != "headless")
            {
                owner.DisplayScale.SetScale(1); owner.GetWindow().Size = new(1440, 1000);
                owner.Workspace.ShowPanel("preview"); owner.Workspace.ShowPanel("retarget");
                owner.Workspace.ShowPanel("preview");
                for (int i = 0; i < 30; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                preview.Redraw(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var img = owner.GetViewport().GetTexture().GetImage(); img.SavePng(Path.Combine(folder, "retarget-preview.png"));
            }
            await RetargetIntegrationChecks.CheckMotionCache(owner, preview, api, Check);
            File.WriteAllLines(Path.Combine(folder, "results.txt"), checks.Select(s => "PASS " + s)); owner.GetTree().Quit();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "results.txt"), string.Join("\n", checks) + "\nFAIL " + e); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
    }
    private static async Task CheckUi(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands, string folder, Action<bool, string> check)
    {
        var asset = api.Find("ue4-mannequin"); commands.Load(asset.Id); await commands.PendingTask;
        var panel = owner.Workspace.Panel("retarget");
        T Control<T>(string name) where T : Node => (T)panel.FindChild(name, true, false)!;
        Control<CheckBox>("RetargetInPlace").ButtonPressed = true;
        Control<SpinBox>("RetargetHipHeight").Value = .15;
        Control<Button>("RetargetApply").EmitSignal(BaseButton.SignalName.Pressed); await owner.PendingRetargetPreview;
        check(preview.AppliedRetarget is { TargetRig: "ue5-mannequin", InPlace: true, HipHeight: .15f } && preview.BoneCount == 161,
            "Retarget panel applies parameters through the shared evaluator");
        Control<LineEdit>("RetargetPresetName").Text = "UI preset";
        Control<Button>("RetargetSave").EmitSignal(BaseButton.SignalName.Pressed);
        check(api.RetargetPresets.List().Any(p => p.Name == "UI preset") && api.RetargetPresets.ForAsset(asset.Id)?.InPlace == true,
            "Retarget panel saves reusable preset and asset association");
        check(api.Search().Total == 2, "presets create no derived library assets");
        preview.Seek(.5); preview.SetRetargetView(2);
        var instance = preview.Model!.GetInstanceId(); var placement = preview.Model.Transform;
        string scene = ExportService.Export(preview, Path.Combine(folder, "preview"), "retarget", false);
        var reloaded = SceneAsset.Load(scene, out _);
        try
        {
            check(Preview3DViewport.Descendants(reloaded).OfType<Skeleton3D>().Single().GetBoneCount() == 161 &&
                Math.Abs(reloaded.Position.X) < .00001 && preview.Model.GetInstanceId() == instance && preview.Model.Transform == placement && Math.Abs(preview.Playhead - .5) < .00001,
                "preview GLB retains full target rig without comparison offset or preview mutation");
        }
        finally { reloaded.Free(); }
        var export = owner.Workspace.Panel("export");
        var format = (OptionButton)export.FindChild("ExportFormat", true, false)!; format.Select(1); format.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
        ((LineEdit)export.FindChild("ExportFolder", true, false)!).Text = Path.Combine(folder, "ui-export");
        ((Button)export.FindChild("QueueExport", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        var job = await api.Jobs.WaitAsync(api.Jobs.LatestId!);
        check(job.Succeeded == 1 && job.ExportOptions?.Retarget is { InPlace: true, HipHeight: .15f, TargetRig: "ue5-mannequin" },
            "export panel queues the exact active retarget configuration");

        var browser = (AssetBrowser)owner.FindChild("AssetBrowser", true, false)!;
        browser.Selection.SetAll([api.Find("ue5-mannequin").Id]);
        var menu = (PopupMenu)owner.FindChild("AssetContextMenu", true, false)!;
        var primaryMesh = MotionTests.MeshHash(preview.Model!);
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 13L);
        await owner.PendingAnimationComparison;
        check(preview.AssetComparison is { Count: 2 } comparison &&
            comparison.Entries.All(entry => MotionTests.MeshHash(entry.Root) == primaryMesh),
            "animations from different source rigs use the same applied character mesh in comparison");
        ((Button)preview.FindChild("ExitAnimationComparison", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);

        Control<Button>("RetargetRestore").EmitSignal(BaseButton.SignalName.Pressed);
        check(preview.AppliedRetarget == null && preview.BoneCount == 68 && !Control<CheckBox>("RetargetEnabled").ButtonPressed,
            "restore source clears preview and export retarget state");
        commands.Load("ue5-mannequin"); await commands.PendingTask; commands.Load(asset.Id); await commands.PendingTask;
        check(Control<CheckBox>("RetargetEnabled").ButtonPressed && Control<CheckBox>("RetargetInPlace").ButtonPressed &&
            Math.Abs(Control<SpinBox>("RetargetHipHeight").Value - .15) < .00001, "reselecting asset restores saved configuration");
    }
    private static float Angle(Transform3D a, Transform3D b)
    {
        float dot = Math.Abs(a.Basis.Orthonormalized().GetRotationQuaternion().Dot(b.Basis.Orthonormalized().GetRotationQuaternion()));
        return Mathf.RadToDeg(2 * Mathf.Acos(Mathf.Clamp(dot, 0, 1)));
    }
    private static bool Near(Transform3D a, Transform3D b, float distance, float radians) => a.Origin.DistanceTo(b.Origin) <= distance && Angle(a, b) <= Mathf.RadToDeg(radians);
    internal static string Fixture(string path, string id)
    {
        var root = RigCatalog.Load(id, out var sk);
        try
        {
            var player = new AnimationPlayer { Name = "FixturePlayer", RootNode = new NodePath("..") }; root.AddChild(player);
            var a = new Animation { Length = 1 };
            foreach (string name in new[] { "root", "spine_01", "spine_02", "upperarm_l", "upperarm_r" })
            {
                int bone = sk.FindBone(name); var rest = sk.GetBoneRest(bone); var pathToBone = new NodePath(root.GetPathTo(sk) + ":" + name);
                int r = a.AddTrack(Animation.TrackType.Rotation3D); a.TrackSetPath(r, pathToBone);
                a.RotationTrackInsertKey(r, 0, rest.Basis.GetRotationQuaternion());
                a.RotationTrackInsertKey(r, 1, (rest.Basis * new Basis(Vector3.Right, name == "root" ? .2f : .35f)).GetRotationQuaternion());
                if (name == "root")
                {
                    int p = a.AddTrack(Animation.TrackType.Position3D); a.TrackSetPath(p, pathToBone);
                    a.PositionTrackInsertKey(p, 0, rest.Origin); a.PositionTrackInsertKey(p, 1, rest.Origin + new Vector3(1, 0, .15f));
                }
            }
            RetargetBaker.AddAnimation(player, "Test", a);
            ExportService.WriteScene(path, root, 60, false, 1); return path;
        }
        finally { root.Free(); }
    }
}
