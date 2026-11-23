using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.Tests;

public static class FeedbackTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        var folder = Path.Combine(api.Storage.Root, "feedback-validation"); Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add(text); GD.Print("PASS " + text); }
        try
        {
            Check(owner.Characters.List().Count(c => c.Builtin && c.Compatible) == 4, "four standard male/female targets ship without user imports");
            var official = SceneAsset.Load(RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx"), out _);
            try
            {
                var report = AssetReadiness.Inspect(official, true);
                Check(report.Ready, "official Manny Idle passes readiness: " + string.Join("; ", report.Errors.Take(2)));
            }
            finally { official.Free(); }
            string source = RetargetTests.Fixture(Path.Combine(folder, "source.glb"), "ue4-mannequin");
            var originalBytes = File.ReadAllBytes(source);
            foreach (string id in RigCatalog.BodyIds)
            {
                var result = await RetargetBaker.BakeAsync(source, new() { Retarget = new() { TargetRig = id }, SampleFps = 30 });
                try
                {
                    owner.AddChild(result.Root);
                    var skeleton = Preview3DViewport.Descendants(result.Root).OfType<Skeleton3D>().Single();
                    Check(skeleton.GetBoneCount() == (RigCatalog.Family(id) == "ue4-mannequin" ? 68 : 161), id + " has complete standard hierarchy");
                    var player = Preview3DViewport.Descendants(result.Root).OfType<AnimationPlayer>().Single();
                    player.Play("Test"); player.Advance(0); player.Pause(); player.Seek(0, true, true);
                    var first = skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_l")); player.Seek(.8, true, true);
                    Check(!first.IsEqualApprox(skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_l"))), id + " actually animates");
                    Check(AssetReadiness.Inspect(result.Root, true).Ready, id + " baked bone tracks resolve");
                    player.Stop(); skeleton.ResetBonePoses();
                    var output = Path.Combine(folder, id + ".glb"); ExportService.WriteScene(output, result.Root, 30, false, 1);
                    var reload = SceneAsset.Load(output, out _);
                    try { Check(AssetReadiness.Inspect(reload, true).Ready, id + " export reload remains Ready"); } finally { reload.Free(); }
                }
                finally { result.Root.GetParent()?.RemoveChild(result.Root); result.Root.Free(); }
            }
            var custom = SceneAsset.Load(source, out _, forExport: true);
            string customPath = Path.Combine(folder, "mixamo.glb");
            try
            {
                var sk = Preview3DViewport.Descendants(custom).OfType<Skeleton3D>().Single();
                var names = new Dictionary<string, string> { ["pelvis"] = "Hips", ["spine_01"] = "Spine", ["spine_02"] = "Spine1", ["spine_03"] = "Spine2", ["neck_01"] = "Neck", ["head"] = "Head" };
                foreach (var (side, word) in new[] { ("l", "Left"), ("r", "Right") })
                    foreach (var (part, alias) in new[] { ("clavicle", "Shoulder"), ("upperarm", "Arm"), ("lowerarm", "ForeArm"), ("hand", "Hand"), ("thigh", "UpLeg"), ("calf", "Leg"), ("foot", "Foot"), ("ball", "ToeBase") })
                        names[part + "_" + side] = word + alias;
                foreach (var (before, after) in names) sk.SetBoneName(sk.FindBone(before), "mixamorig:" + after);
                foreach (var mesh in Preview3DViewport.Descendants(custom).OfType<MeshInstance3D>().Where(m => m.Skin != null))
                    for (int b = 0; b < mesh.Skin!.GetBindCount(); b++)
                        if (names.TryGetValue(mesh.Skin.GetBindName(b), out var renamed)) mesh.Skin.SetBindName(b, "mixamorig:" + renamed);
                foreach (var player in Preview3DViewport.Descendants(custom).OfType<AnimationPlayer>())
                    foreach (string clip in player.GetAnimationList())
                    {
                        var a = player.GetAnimation(clip);
                        for (int t = 0; t < a.GetTrackCount(); t++)
                        {
                            var p = a.TrackGetPath(t); string bone = p.GetConcatenatedSubNames();
                            if (names.TryGetValue(bone, out var renamed)) a.TrackSetPath(t, new NodePath(p.GetConcatenatedNames() + ":mixamorig:" + renamed));
                        }
                    }
                // Godot's NodePath uses ':' for subnames; importers sanitize source namespaces to underscores.
                for (int b = 0; b < sk.GetBoneCount(); b++) sk.SetBoneName(b, sk.GetBoneName(b).ToString().Replace(':', '_'));
                foreach (var mesh in Preview3DViewport.Descendants(custom).OfType<MeshInstance3D>().Where(m => m.Skin != null))
                    for (int b = 0; b < mesh.Skin!.GetBindCount(); b++) mesh.Skin.SetBindName(b, mesh.Skin.GetBindName(b).ToString().Replace(':', '_'));
                foreach (var player in Preview3DViewport.Descendants(custom).OfType<AnimationPlayer>())
                    foreach (string clip in player.GetAnimationList())
                        for (int t = 0; t < player.GetAnimation(clip).GetTrackCount(); t++)
                        { var a = player.GetAnimation(clip); a.TrackSetPath(t, new NodePath(a.TrackGetPath(t).ToString().Replace(":mixamorig:", ":mixamorig_"))); }
                ExportService.WriteScene(customPath, custom, 60, false, 1);
            }
            finally { custom.Free(); }
            var entry = owner.Characters.Import(customPath, "Mixamo fixture");
            Check(entry.Compatible, "different bone names pass Ready: " + entry.Reason);
            foreach (string id in RigCatalog.BodyIds)
            {
                var result = await RetargetBaker.BakeAsync(customPath, new() { Retarget = new() { TargetRig = id }, SampleFps = 30 });
                Check(result.Source.Id.StartsWith("humanoid:"), "automatic source map to " + id); result.Root.Free();
            }
            var targetResult = await RetargetBaker.BakeAsync(source, new() { Retarget = entry.Options(), SampleFps = 30 });
            Check(targetResult.Target.Names.Contains("mixamorig_Hips"), "custom target keeps original bone names and skin binding"); targetResult.Root.Free();
            var broken = SceneAsset.Load(customPath, out _);
            try
            {
                var sk = Preview3DViewport.Descendants(broken).OfType<Skeleton3D>().Single();
                sk.SetBoneName(sk.FindBone("mixamorig_LeftForeArm"), "UnknownJoint");
                var report = AssetReadiness.Inspect(broken, true);
                Check(!report.Ready && report.Errors.Any(e => e.Contains("lowerarm_l")), "missing essential mapping cannot be Ready and names the missing role");
            }
            finally { broken.Free(); }
            var standardRoot = RigCatalog.Load("ue4-mannequin", out var standardSk);
            var compactRoot = new Node3D(); var compactSk = new Skeleton3D(); compactRoot.AddChild(compactSk);
            try
            {
                var standard = HumanoidMap.Describe(standardSk, standardRoot);
                foreach (int bone in standard.Order.Where(i => HumanoidMap.Required.Contains(standard.Names[i])))
                {
                    int parent = standard.Parents[bone];
                    while (parent >= 0 && !HumanoidMap.Required.Contains(standard.Names[parent])) parent = standard.Parents[parent];
                    int i = compactSk.AddBone(standard.Names[bone]);
                    compactSk.SetBoneParent(i, parent < 0 ? -1 : compactSk.FindBone(standard.Names[parent]));
                    var parentRest = parent < 0 ? Transform3D.Identity : standard.GlobalRest[parent];
                    compactSk.SetBoneRest(i, parentRest.AffineInverse() * standard.GlobalRest[bone]);
                }
                var compact = HumanoidMap.Describe(compactSk, compactRoot);
                var pose = compact.Rest.ToArray(); pose[compact.Indices["pelvis"]].Origin += Vector3.Right;
                var solver = new RetargetSolver(compact, standard, new() { TargetRig = "ue4-mannequin" });
                var world = standard.Globals(solver.Evaluate(pose, compact.Frame, Vector3.Zero));
                Check(world[standard.Indices["foot_l"]].Origin.DistanceTo(standard.GlobalRest[standard.Indices["foot_l"]].Origin + Vector3.Right) < .001,
                    "hips-as-root source transfers translation once without doubling pelvis motion");
                compactSk.SetBoneParent(compactSk.FindBone("hand_l"), compactSk.FindBone("pelvis"));
                Check(!AssetReadiness.Inspect(compactRoot, false).Ready, "broken arm ancestry cannot be Ready");
            }
            finally { standardRoot.Free(); compactRoot.Free(); }
            await api.ImportAsync([source]); commands.Load("source"); await commands.PendingTask;
            await owner.ImportCharacters([customPath]);
            Check(owner.PreviewCharacterId == entry.Id && preview.AppliedRetarget?.Character?.Id == entry.Id, "imported Ready character automatically becomes active target");
            ((Window)owner.FindChild("CharacterLibrary", true, false)!).Hide();
            await owner.SelectPreviewCharacterAsync("ue4-mannequin");
            string meshPath = Path.Combine(folder, "MeshOnly.glb");
            var meshOnly = new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh() };
            try { ExportService.WriteScene(meshPath, meshOnly, null, false, 0); } finally { meshOnly.Free(); }
            int characters = owner.Characters.List().Length;
            await owner.Import([entry.ModelPath, meshPath]);
            Check(owner.PreviewCharacterId == entry.Id && api.Search().Total == 1,
                "ordinary mesh import can select a Ready character without adding it to the animation library");
            var imported = api.Jobs.Get(api.Jobs.LatestId!);
            Check(imported.Failed == 0 && imported.Items.Sum(item => item.ExcludedWithoutAnimationAssets) == 2 &&
                owner.Characters.List().Length == characters, "ordinary import skips an unrigged mesh in both libraries");
            var stage = (PreviewStage)owner.Workspace.Panel("preview");
            stage.SpeedControl.Value = 1.35;
            Check(Math.Abs(preview.Player!.SpeedScale - 1.35) < .001, "continuous speed slider controls actual player");
            Check(preview.TotalFrames == 61 && preview.CurrentFrame == 1, "source 60 FPS retains 61 samples including endpoint after retarget");
            preview.Seek(.5); Check(preview.CurrentFrame == 31, "frame count follows seeking independent of speed");
            preview.Seek(preview.Duration); Check(preview.CurrentFrame == preview.TotalFrames, "last frame is reachable");
            preview.SnapAxis(0); Check(preview.Orthographic && preview.CameraBasis.Z.Dot(Vector3.Right) > .999f, "X gizmo snap produces orthographic X view");
            var beforeBasis = preview.CameraBasis; preview.Navigate("orbit", new(30, 15));
            Check(!preview.CameraBasis.IsEqualApprox(beforeBasis), "orbit changes camera orientation");
            var beforeTarget = preview.CameraTarget; preview.Navigate("pan", new(15, 8));
            Check(!preview.CameraTarget.IsEqualApprox(beforeTarget), "pan changes camera target");
            preview.ToggleProjection(); Check(!preview.Orthographic, "perspective toggle works");
            Check(File.ReadAllBytes(source).SequenceEqual(originalBytes), "source asset is never modified");
            if (DisplayServer.GetName() != "headless")
            {
                foreach (var dialog in Preview3DViewport.Descendants(owner).OfType<AcceptDialog>()) dialog.Hide();
                owner.GetWindow().Size = new(1440, 1000); owner.DisplayScale.SetScale(1); owner.Workspace.ShowPanel("preview");
                preview.Seek(.5); preview.FrameModel();
                for (int i = 0; i < 25; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                var gizmo = (Control)preview.FindChild("AxisGizmo", true, false)!;
                var point = gizmo.GetGlobalTransformWithCanvas() * (gizmo.Size / 2);
                var oldBasis = preview.CameraBasis;
                void Push(InputEvent ev) { owner.GetViewport().PushInput(ev, true); ev.Dispose(); }
                Push(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true });
                Push(new InputEventMouseMotion { Position = point + new Vector2(20, 10), GlobalPosition = point + new Vector2(20, 10), Relative = new(20, 10), ButtonMask = MouseButtonMask.Left });
                Push(new InputEventMouseButton { Position = point + new Vector2(20, 10), GlobalPosition = point + new Vector2(20, 10), ButtonIndex = MouseButton.Left, Pressed = false });
                Check(!preview.CameraBasis.IsEqualApprox(oldBasis), "real GUI input on axis gizmo rotates the preview");
                await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = owner.GetViewport().GetTexture().GetImage(); image.SavePng(Path.Combine(folder, "feedback-preview.png"));
                owner.DisplayScale.SetScale(2); owner.GetWindow().Size = new(1024, 700); owner.Workspace.ShowPanel("preview");
                for (int i = 0; i < 25; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                Check(owner.GetViewport().GetVisibleRect().Encloses(stage.SpeedControl.GetGlobalRect()) && owner.GetViewport().GetVisibleRect().Encloses(stage.TimeLabel.GetGlobalRect()),
                    "200 percent compact window keeps frame display and speed control inside the window");
                await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var compactImage = owner.GetViewport().GetTexture().GetImage(); compactImage.SavePng(Path.Combine(folder, "feedback-compact-200.png"));
            }
            File.WriteAllLines(Path.Combine(folder, "results.txt"), checks.Select(s => "PASS " + s));
            GD.Print("FEEDBACK_TESTS_OK " + checks.Count); owner.GetTree().Quit();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "results.txt"), string.Join("\n", checks) + "\nFAIL " + e); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
    }
}
