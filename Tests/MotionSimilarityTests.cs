using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Pipeline;
using Animanager.Core.Motion;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.Tests;

internal static class MotionSimilarityTests
{
    internal static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api)
    {
        int checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException(label);
            GD.Print("PASS " + label);
            checks++;
        }
        try
        {
            MotionSamples Clip(string name, params float[] positions) => new(name, (positions.Length - 1) / 30d,
                positions.Select(value => Enumerable.Repeat(Vector3.Right * value, MotionSimilarity.Roles.Length).ToArray()).ToArray());
            var reference = Clip("A", 1, 2, 3);
            var longer = Clip("B", 3, 2, 1, 0, 1, 2, 3);
            var hit = MotionSimilarity.Find(reference, 0, reference.Duration, [longer], 0, default)!;
            Check(hit.Score == 100 && hit.Start == longer.Time(4) && hit.End == longer.Time(6), "ordered subsequence and exact boundaries");
            var prefix = MotionSimilarity.Find(reference, 0, reference.Duration, [longer], .1, default)!;
            Check(prefix.Score < 100 && prefix.End <= .1, "wind-up limit excludes late matches");
            var pose = MotionSimilarity.Find(reference, 0, 0, [longer], .1, default)!;
            Check(pose.Score == 100 && pose.Start == longer.Time(2) && pose.Start == pose.End, "single pose inside wind-up");
            var bytes = MotionSampleCodec.Encode([reference, longer])!;
            Check(MotionSampleCodec.Decode(bytes) is { Length: 2 } decoded && decoded[1].Frames[^1][3] == longer.Frames[^1][3]
                && MotionSampleCodec.Decode(bytes[..^1]) == null, "sample cache roundtrip and truncation");

            var path = RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx");
            var sampled = await MotionSampleReader.Read(path, owner.GetTree(), default);
            Check(sampled.Length > 0 && sampled[0].Frames.Any(f => !f.SequenceEqual(sampled[0].Frames[0])), "animated FBX skeleton sampling");
            var root = (await RetargetBaker.BakeAsync(path, new ExportOptions { IncludeMesh = true, SampleFps = 30,
                StartSeconds = 0, EndSeconds = 1.5, Retarget = new() { TargetRig = "ue4-mannequin" } })).Root;
            string glb = Path.Combine(api.Storage.Root, "Candidate.glb");
            try
            {
                using var doc = new GltfDocument();
                using var state = new GltfState();
                Check(doc.AppendFromScene(root, state) == Error.Ok && doc.WriteToFilesystem(state, glb) == Error.Ok, "create a second animation asset");
            }
            finally { root.Free(); }
            var imported = await api.ImportAsync([path, glb]);
            Check(imported.Errors.Count == 0, "import FBX and GLB candidates");
            var first = api.Find("UE5_Manny_Idle");
            var second = api.Find("Candidate");
            await owner.PreviewAssetAsync(first);
            await owner.PendingBrowse;
            T Find<T>(string name) where T : Node => (T)owner.FindChild(name, true, false)!;
            void Press(string name) => Find<Button>(name).EmitSignal(BaseButton.SignalName.Pressed);
            var search = Find<LineEdit>("AssetSearch");
            void Search(string term)
            {
                search.Text = term;
                search.EmitSignal(LineEdit.SignalName.TextSubmitted, term);
            }
            var browser = Find<AssetBrowser>("AssetBrowser");
            Search("Candidate");
            await owner.PendingBrowse;
            preview.Seek(.4);
            Press("MotionUsePose");
            await owner.PendingBrowse;
            Check(browser.Count == 1 && browser.Row(0)?.Id == second.Id && browser.Row(0)!.MotionLabel.Contains("分"),
                "reference outside current filter finds candidate with time and score");
            browser.Selection.Select(second.Id);
            MotionAudition OpenCandidate()
            {
                using var enter = new InputEventKey { Keycode = Key.Enter, Pressed = true };
                browser.ActiveView.EmitSignal(Control.SignalName.GuiInput, enter);
                return owner.GetChildren().OfType<MotionAudition>().Single(w => w.Visible && !w.IsQueuedForDeletion());
            }
            var superseded = OpenCandidate();
            var popup = OpenCandidate();
            Check(superseded.IsQueuedForDeletion() && !superseded.Visible, "a second audition dismisses the first while loading");
            await owner.PendingMotionAuditions;
            Check(!GodotObject.IsInstanceValid(superseded) && GodotObject.IsInstanceValid(popup) && popup.IsProcessing(),
                "only the latest audition finishes loading and starts playback");
            var view = Preview3DViewport.Descendants(popup).OfType<Preview3DViewport>().Single();
            var camera = view.View.GetCamera3D();
            var mesh = Preview3DViewport.Descendants(view.Model!).OfType<MeshInstance3D>().First(m => m.Skin != null);
            Check(view.BoneCount == 161 && view.Clips.SequenceEqual(["From", "To"]), "UE5 and UE4 clips bind to one Manny skeleton");
            view.Navigate("orbit", new(40, -20));
            view.Zoom(.85f);
            view.Navigate("pan", new(5, 4));
            var cameraPose = camera.GlobalTransform;
            var clipBefore = view.CurrentClip;
            for (int i = 0; i < 100 && view.CurrentClip == clipBefore; i++) popup._Process(.025);
            Check(view.CurrentClip != clipBefore && view.View.GetCamera3D() == camera && camera.GlobalTransform.IsEqualApprox(cameraPose) &&
                mesh.IsVisibleInTree() && mesh.IsInsideTree(), "role mesh, orbit, zoom and pan survive the transition");
            if (DisplayServer.GetName() != "headless")
            {
                for (int i = 0; i < 5; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                using var image = popup.GetTexture().GetImage();
                Check(image != null && !image.IsEmpty() && image.SavePng(Path.Combine(api.Storage.Root, "audition.png")) == Error.Ok,
                    "rendered transition audition");
            }
            CheckButton Repeat(MotionAudition audition) => Preview3DViewport.Descendants(audition).OfType<CheckButton>()
                .Single(b => b.Text == "循环试接");
            var repeat = Repeat(popup);
            Check(!repeat.ButtonPressed, "audition plays once by default");
            repeat.ButtonPressed = true;
            popup._Process(10);
            Check(popup.IsProcessing() && popup.Visible, "explicit repeat keeps the audition playing");
            repeat.ButtonPressed = false;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (GodotObject.IsInstanceValid(popup) && timer.Elapsed.TotalSeconds < 5)
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!GodotObject.IsInstanceValid(popup), "finishing the pair automatically destroys the audition window");

            var cancelled = OpenCandidate();
            using var escape = new InputEventKey { Keycode = Key.Escape, Pressed = true };
            cancelled.PushInput(escape, true);
            await owner.PendingMotionAuditions;
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!GodotObject.IsInstanceValid(cancelled), "Escape closes a loading audition and cancels its preparation");
            var backgrounded = OpenCandidate();
            await owner.PendingMotionAuditions;
            Repeat(backgrounded).ButtonPressed = true;
            if (DisplayServer.GetName() == "headless")
                backgrounded.EmitSignal(Window.SignalName.FocusExited);
            else if (backgrounded.IsEmbedded())
            {
                using var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true,
                    Position = Vector2.One, GlobalPosition = Vector2.One };
                owner.GetWindow().PushInput(click, true);
                click.Pressed = false;
                owner.GetWindow().PushInput(click, true);
            }
            else owner.GetWindow().GrabFocus();
            timer.Restart();
            while (GodotObject.IsInstanceValid(backgrounded) && timer.Elapsed.TotalSeconds < 3)
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!GodotObject.IsInstanceValid(backgrounded), "returning focus to the main window destroys the audition");
            preview.Seek(.2);
            Press("MotionMarkStart");
            preview.Seek(.45);
            Press("MotionUseRange");
            await owner.PendingBrowse;
            Check(browser.Count == 1 && browser.Row(0)!.DetailLine.Contains('–'), "range match reports both boundaries");
            Find<SpinBox>("MotionWindow").Value = .1;
            await owner.PendingBrowse;
            Check(browser.Count == 0, "narrower candidate range excludes clip");
            Press("AssetClearFilters");
            await owner.PendingBrowse;
            Check(browser.Count == 1 && browser.Row(0)!.MotionLabel == "" && search.Text == "Candidate", "clearing filter restores ordinary search");

            api.SetTags(first.Id, "Combo");
            api.SetTags(second.Id, "Combo");
            var collection = owner.AppCollections.Create("Combos");
            var empty = owner.AppCollections.Create("Empty combos");
            string libraryId = owner.Libraries.Active!.Identity.Id;
            owner.AppCollections.SetMembership(collection.Id, [new(libraryId, first.Id), new(libraryId, second.Id)], true);
            Search("");
            await owner.PendingBrowse;
            Press("MotionUsePose");
            await owner.PendingBrowse;
            Press("CollectionRow" + empty.Id);
            await owner.PendingBrowse;
            Check(browser.Count == 0, "empty collection narrows an active pose search");
            Press("CollectionRow" + collection.Id);
            await owner.PendingBrowse;
            var tag = Preview3DViewport.Descendants(owner).OfType<Button>().First(b => b.Text == "Combo 2");
            tag.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(browser.Count == 1 && browser.Row(0)!.Id == second.Id, "collection and tag intersect with motion search");
            var chip = Preview3DViewport.Descendants(owner).OfType<FilterChip>().Single(c => c.Key == "motion");
            chip.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(browser.Count == 2 && Preview3DViewport.Descendants(owner).OfType<FilterChip>().Any(c => c.Key == "tags"),
                "removing only motion retains collection and tag");

            var manifests = RuntimePaths.Bundled("Libraries/Nodachi-Motion/motion");
            if (Directory.Exists(manifests) && Directory.EnumerateFiles(manifests, "*.motion.json").FirstOrDefault() is { } manifest)
            {
                var native = (await MotionSampleReader.Read(manifest, owner.GetTree(), default)).Single();
                Check(native.Frames.Length > 1 && native.Frames.All(f => f.All(v => v.IsFinite())), "Nodachi split-body animation sampling");
                var others = RuntimePaths.Bundled("Libraries/Nodachi-CUE4Parse/assets");
                string name = Core.Motion.MotionJson.ReadManifest(manifest).Name + ".glb";
                if (Directory.Exists(others) && Directory.EnumerateFiles(others, name, SearchOption.AllDirectories).FirstOrDefault() is { } counterpart)
                {
                    var same = await MotionSampleReader.Read(counterpart, owner.GetTree(), default);
                    var aligned = MotionSimilarity.Find(native, native.Duration * .35, native.Duration * .65, same, 0, default);
                    Check(aligned is { Score: > 95 } && Math.Abs(aligned.Start - native.Duration * .35) < .08,
                        $"Nodachi motion and independently imported UE clip align · {aligned?.Score:F1} points at {aligned?.Start:F3}s");
                    var armed = new MotionAudition();
                    owner.AddChild(armed);
                    armed.PopupCenteredClamped(new(760, 520));
                    try
                    {
                        await armed.Prepare(new(counterpart, same[0].Name, "A", 0, .3),
                            new(manifest, native.Name, "B", .1, .4), new() { TargetRig = "ue4-mannequin" });
                        var shared = Preview3DViewport.Descendants(armed).OfType<Preview3DViewport>().Single();
                        var prop = shared.Model?.GetNodeOrNull<Node3D>("CandidateWeapon");
                        Check(armed.IsProcessing() && shared.BoneCount == 68 && prop is { Visible: false },
                            "both actions use the requested UE4 mesh before the armed follow-up");
                        armed._Process(.35);
                        Check(shared.CurrentClip == "To" && prop!.Visible, "the follow-up weapon appears on the shared character at the cut");
                    }
                    finally { armed.Dismiss(); }
                }
            }
            Press("CollectionRow" + collection.Id);
            Press("AssetClearFilters");
            var (json, data) = MotionGlb.Unpack(File.ReadAllBytes(RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Idle.glb")));
            var pages = Directory.CreateDirectory(Path.Combine(api.Storage.Root, "paging-fixtures"));
            var files = Enumerable.Range(1, 61).Select(i =>
            {
                var file = Path.Combine(pages.FullName, $"Paging-{i:D2}.glb");
                json["asset"]!["generator"] = $"Paging test {i}";
                File.WriteAllBytes(file, MotionGlb.Pack(json, data));
                return file;
            }).ToArray();
            await api.ImportAsync(files);
            Search("Paging");
            await owner.PendingBrowse;
            Press("AssetAttributeFilters");
            var next = Find<Button>("AssetPageNext");
            var previous = Find<Button>("AssetPagePrevious");
            var filters = Find<PanelContainer>("FilterSidebar");
            var window = owner.GetWindow();
            var viewport = owner.GetViewport();
            if (DisplayServer.GetName() != "headless") window.GrabFocus();
            if (DisplayServer.GetName() == "headless") viewport.NotifyMouseEntered();
            foreach (var (size, scale) in new[] { (new Vector2I(1280, 800), 1), (new Vector2I(720, 400), 1), (new Vector2I(1024, 700), 2) })
            {
                owner.DisplayScale.SetScale(scale);
                window.Size = size;
                await owner.ToSignal(owner.GetTree().CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
                Check(filters.IsVisibleInTree() && !filters.GetGlobalRect().Intersects(next.GetGlobalRect()) &&
                    window.GetVisibleRect().Encloses(next.GetGlobalRect()), $"filters leave the pager visible at {size}");
                if (DisplayServer.GetName() != "headless")
                {
                    using var image = window.GetTexture().GetImage();
                    image.SavePng(Path.Combine(api.Storage.Root, $"filters-{size.X}.png"));
                }
                await Click(next);
                Check(browser.Count == 1 && filters.IsVisibleInTree(), $"mouse reaches Next with filters open at {size}");
                await Click(previous);
                Check(browser.Count == 60 && filters.IsVisibleInTree(), $"mouse reaches Previous with filters open at {size}");
            }
            async Task Click(Button button)
            {
                Vector2 point = button.GetGlobalRect().GetCenter();
                using var move = new InputEventMouseMotion { Position = point, GlobalPosition = point };
                using var down = new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true };
                using var up = (InputEventMouseButton)down.Duplicate();
                up.Pressed = false;
                viewport.PushInput(move, true);
                // A native audition can close with the OS cursor still outside the main window.
                if (viewport.GuiGetHoveredControl() == null && DisplayServer.GetName() != "headless")
                {
                    viewport.NotifyMouseEntered();
                    viewport.PushInput(move, true);
                }
                viewport.PushInput(down, true);
                viewport.PushInput(up, true);
                await owner.PendingBrowse;
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Search("Candidate");
            await owner.PendingBrowse;
            Press("MotionUsePose");
            await owner.PendingBrowse;
            browser.Selection.Select(second.Id);
            var last = OpenCandidate();
            window.EmitSignal(Window.SignalName.CloseRequested);
            Check(last.IsQueuedForDeletion() && !last.Visible, "quitting the app closes the audition during preparation");
            await owner.PendingMotionAuditions;
            GD.Print($"PASS {checks} motion similarity checks");
        }
        catch (Exception ex)
        {
            GD.PushError("MOTION SIMILARITY TEST FAILED: " + ex);
            owner.GetTree().Quit(1);
        }
    }
}
