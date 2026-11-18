using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Pipeline;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.Tests;

public static class SecondFeedbackTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        string folder = Path.Combine(api.Storage.Root, "second-feedback"); Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); GD.Print("PASS " + message); }
        async Task Frames(int n = 5) { for (int i = 0; i < n; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Capture(string name)
        {
            if (DisplayServer.GetName() == "headless") return;
            await Frames(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = owner.GetViewport().GetTexture().GetImage(); image.SavePng(Path.Combine(folder, name + ".png"));
        }
        try
        {
            string a = RetargetTests.Fixture(Path.Combine(folder, "A.glb"), "ue4-mannequin");
            string b = RetargetTests.Fixture(Path.Combine(folder, "B.glb"), "ue5-mannequin");
            string c = RetargetTests.Fixture(Path.Combine(folder, "C.glb"), "ue4-mannequin");
            // Make C distinct for the library's content deduplication.
            var root = SceneAsset.Load(c, out _, forExport: true);
            try { root.Name = "DistinctThirdSource"; ExportService.WriteScene(c, root, 30, false, 1); } finally { root.Free(); }
            await api.ImportAsync([a, b, c]);
            await owner.SelectPreviewCharacterAsync("ue5-mannequin");
            await owner.PreviewAssetAsync(api.Find("B"));
            Check(preview.AppliedRetarget?.TargetRig == "ue5-mannequin", "default target is ready before showing the first animation");
            preview.Seek(.6); ulong oldModel = preview.Model!.GetInstanceId();
            bool flashed = false, blank = false; int observed = 0;
            void Observe()
            {
                observed++;
                blank |= preview.Model == null;
                flashed |= preview.Model != null && (preview.BoneCount != 161 || preview.AppliedRetarget?.TargetRig != "ue5-mannequin");
            }
            owner.GetTree().ProcessFrame += Observe;
            try { await owner.PreviewAssetAsync(api.Find("A")); await Frames(2); }
            finally { owner.GetTree().ProcessFrame -= Observe; }
            Check(observed > 0 && !flashed && !blank && preview.Model!.GetInstanceId() != oldModel, "every observed frame stays on the configured rig until atomic replacement");
            Check(preview.Playhead < .001 && Math.Abs(preview.DisplayFps - 60) < .001, "new source starts at its own first frame and source rate");
            oldModel = preview.Model!.GetInstanceId();
            using (var cancel = new CancellationTokenSource())
            {
                var pending = owner.PreviewAssetAsync(api.Find("C"), cancel.Token); cancel.Cancel(); await pending;
                Check(preview.Model!.GetInstanceId() == oldModel, "cancelled asset change preserves the displayed model");
            }
            var first = owner.PreviewAssetAsync(api.Find("C"));
            var latest = owner.PreviewAssetAsync(api.Find("B"));
            await Task.WhenAll(first, latest);
            Check(commands.SelectedId == api.Find("B").Id && preview.AppliedRetarget?.TargetRig == "ue5-mannequin", "latest rapid selection wins without stale replacement");
            oldModel = preview.Model!.GetInstanceId();
            string bad = Path.Combine(folder, "Unready.glb");
            var invalid = SceneAsset.Load(a, out _, forExport: true);
            try
            {
                var sk = Preview3DViewport.Descendants(invalid).OfType<Skeleton3D>().Single(); sk.SetBoneName(sk.FindBone("head"), "MissingHeadMapping");
                ExportService.WriteScene(bad, invalid, 60, false, 1);
            }
            finally { invalid.Free(); }
            await api.ImportAsync([bad]); await owner.PreviewAssetAsync(api.Find("Unready"));
            Check(preview.Model!.GetInstanceId() == oldModel && preview.AppliedRetarget != null, "unready source fails without showing its original rig");
            commands.Load("Unready"); await commands.PendingTask;
            Check(commands.LastResult.StartsWith("ERROR:") && preview.Model!.GetInstanceId() == oldModel, "console reports failed preparation without replacing the preview");
            commands.Load("A"); await commands.PendingTask;
            Check(preview.AppliedRetarget?.TargetRig == "ue5-mannequin", "console and asset browser share the atomic loader");
            owner.DisplayScale.SetScale(1); owner.GetWindow().Size = new(1440, 1000); owner.Workspace.ShowPanel("preview"); await Frames(15);
            var stage = (PreviewStage)owner.Workspace.Panel("preview");
            Check(new Rect2(Vector2.Zero, preview.Size).Encloses(((Control)preview.FindChild("DragNavigation", true, false)!).GetRect()), "drag buttons stay inside the viewport");
            Check(preview.Position.Y == 0 && stage.CharacterSlot.GetWindow() == stage.SettingsDialog && !stage.SettingsDialog.Visible,
                "character and metadata controls live in settings without reserving a stage header");
            preview.Bones.Select("hand_l"); await Frames();
            var tools = (Control)preview.FindChild("SelectedBoneTools", true, false)!;
            var trail = (CheckButton)preview.FindChild("SelectedBoneTrail", true, false)!;
            Check(tools.Visible && !trail.Disabled, "selected bone exposes its trajectory option directly in the viewport");
            trail.ButtonPressed = true; await preview.Bones.PendingSampling;
            Check(preview.Bones.TrailEnabled && preview.Bones.Trajectory?.Bone == preview.Bones.Selected && preview.Bones.Overlay.SegmentCount > 0, "viewport checkbox creates the selected bone trajectory");
            Check(!Preview3DViewport.Descendants(preview.Bones.Overlay).OfType<Label3D>().Any() && preview.FindChild("BoneScreenLabels", true, false)!.GetViewport() == owner.GetViewport(),
                "bone text is in the window canvas rather than the scaled 3D texture");
            preview.Seek(.45); preview.FrameModel(); await Capture("labels-far");
            preview.Zoom(.55f); await Capture("labels-near");
            preview.Bones.ShowTrail(false); await Frames(); Check(!trail.ButtonPressed, "panel/API changes synchronize the viewport trajectory toggle");
            preview.Bones.ShowTrail(true); await preview.Bones.PendingSampling;
            if (DisplayServer.GetName() == "headless") owner.GetViewport().NotifyMouseEntered();
            void Push(InputEvent ev) { owner.GetViewport().PushInput(ev, true); ev.Dispose(); }
            async Task Drag(string tool, Vector2 delta)
            {
                var button = (Control)preview.FindChild("Tool_" + tool, true, false)!;
                var point = button.GetGlobalTransformWithCanvas() * (button.Size / 2);
                Push(new InputEventMouseMotion { Position = point, GlobalPosition = point });
                Push(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true });
                Push(new InputEventMouseMotion { Position = point + delta, GlobalPosition = point + delta, Relative = delta, ButtonMask = MouseButtonMask.Left });
                Push(new InputEventMouseButton { Position = point + delta, GlobalPosition = point + delta, ButtonIndex = MouseButton.Left, Pressed = false });
                await Frames(2);
            }
            var target = preview.CameraTarget; var basis = preview.CameraBasis;
            await Drag("pan", new(20, 10)); Check(!target.IsEqualApprox(preview.CameraTarget) && basis.IsEqualApprox(preview.CameraBasis), "dragging directly on Pan pans without an extra mode-selection click");
            basis = preview.CameraBasis; await Drag("orbit", new(20, 10)); Check(!basis.IsEqualApprox(preview.CameraBasis), "dragging directly on Orbit rotates the camera");
            float distance = preview.CameraDistance; await Drag("zoom", new(0, 20)); Check(Math.Abs(distance - preview.CameraDistance) > .01, "dragging directly on Zoom changes camera distance");
            await Capture("viewport");
            ((Button)preview.FindChild("ViewportSettings", true, false)!).EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Check(stage.SettingsDialog.Visible && stage.CharacterSlot.IsVisibleInTree(), "gear opens the independent viewport settings page");
            if (DisplayServer.GetName() != "headless")
            {
                await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var settingsImage = stage.SettingsDialog.GetTexture().GetImage(); settingsImage.SavePng(Path.Combine(folder, "settings.png"));
            }
            stage.SettingsDialog.Hide();
            owner.DisplayScale.SetScale(2); owner.GetWindow().Size = new(1024, 700); owner.Workspace.ShowPanel("preview"); await Frames(20);
            await Capture("labels-200");
            Check(owner.GetViewport().GetVisibleRect().Encloses(stage.TimeLabel.GetGlobalRect()), "compact 200 percent layout retains transport after header removal");
            Check(new Rect2(Vector2.Zero, preview.Size).Encloses(((Control)preview.FindChild("DragNavigation", true, false)!).GetRect()), "compact navigation buttons are fully visible at 200 percent");
            ((Button)preview.FindChild("ViewportSettings", true, false)!).EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            var picker = (OptionButton)stage.CharacterSlot.FindChild("PreviewCharacter", true, false)!;
            int ue4 = Enumerable.Range(0, picker.ItemCount).First(i => picker.GetItemText(i).Contains("UE4 男体"));
            picker.Select(ue4); picker.EmitSignal(OptionButton.SignalName.ItemSelected, ue4); await owner.PendingRetargetPreview;
            Check(preview.BoneCount == 68 && preview.AppliedRetarget?.TargetRig == "ue4-mannequin", "character choice from the settings page changes the displayed target");
            if (DisplayServer.GetName() != "headless")
            {
                await Frames(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var settingsImage = stage.SettingsDialog.GetTexture().GetImage(); settingsImage.SavePng(Path.Combine(folder, "settings-200.png"));
            }
            stage.SettingsDialog.Hide();
            File.WriteAllLines(Path.Combine(folder, "results.txt"), checks.Select(t => "PASS " + t));
            GD.Print("SECOND_FEEDBACK_TESTS_OK " + checks.Count); owner.GetTree().Quit();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "results.txt"), string.Join("\n", checks) + "\nFAIL " + e); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
    }
}
