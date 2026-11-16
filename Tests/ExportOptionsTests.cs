using System.Text.Json;
using System.Text.Json.Nodes;
using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

public static class ExportOptionsTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        var prefix = "ExportOptions-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(api.Storage.Root, prefix);
        Directory.CreateDirectory(root);
        var results = Path.Combine(root, "results.txt");
        var checks = new List<string>();
        var console = owner.GetNode("/root/LimboConsole");
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
            GD.Print("PASS " + message);
        }
        async Task Execute(string command, bool wait = true)
        {
            console.Call("execute_command", command);
            if (wait) await commands.PendingTask.WaitAsync(TimeSpan.FromMinutes(3));
            if (commands.LastResult.StartsWith("ERROR:", StringComparison.Ordinal)) throw new Exception(commands.LastResult);
        }
        Task<JobRecord> Done(WorkspaceApi workspace, string? id = null) =>
            workspace.Jobs.WaitAsync(id ?? workspace.Jobs.LatestId!).WaitAsync(TimeSpan.FromMinutes(3));
        string Folder(string name) => Path.Combine(root, name);
        const double start = .137, end = .792, fps = 20;
        var options = new ExportOptions { IncludeMesh = false, Clips = ["Walk fast"], StartSeconds = start, EndSeconds = end, SampleFps = fps };
        string compact = JsonSerializer.Serialize(options);
        bool SameOptions(ExportOptions? stored) => stored is { IncludeMesh: false, StartSeconds: start, EndSeconds: end, SampleFps: fps }
            && stored.Clips is ["Walk fast"];

        try
        {
            var sourceA = Fixture(Folder(prefix + " Alpha.glb"), "Walk fast");
            var sourceB = Fixture(Folder(prefix + " Beta.glb"), "Different");
            var imported = await api.ImportAsync([sourceA, sourceB]);
            Check(imported.Added == 2, "import two distinct multi-clip scene fixtures");
            var assetA = api.Find(Path.GetFileNameWithoutExtension(sourceA));
            var assetB = api.Find(Path.GetFileNameWithoutExtension(sourceB));
            var sourceBytes = File.ReadAllBytes(sourceA);
            var archivedA = api.Storage.Resolve(assetA.VirtualPath);
            var archivedB = api.Storage.Resolve(assetB.VirtualPath);
            var originalB = File.ReadAllBytes(archivedB);
            var original = MotionGlb.Read(sourceBytes);
            await Execute("preview load " + assetA.Id);
            Check(preview.Clips.Contains("Walk fast") && preview.Clips.Contains("Hold") && preview.BoneCount == 16,
                "original scene exposes existing clips and 16 skeletal joints");
            preview.SelectClip("Walk fast");
            preview.Seek(.37);
            var previewInstance = preview.Model!.GetInstanceId();
            var previewMaterial = Preview3DViewport.Descendants(preview.Model).OfType<MeshInstance3D>().Single().MaterialOverride;
            var previewAnimation = preview.Player!.GetAnimation("Walk fast");
            var previewTrackCount = previewAnimation.GetTrackCount();

            var glbOutput = Folder("GLB 输出");
            await Execute($"export options_plan {prefix} glb {Quote(glbOutput)} {compact}", false);
            var planId = commands.LastResult.Split(' ')[1];
            var plan = api.Planner.Load(planId);
            Check(plan.Kind == JobKind.ExportGlb && plan.Items.Count == 2 && SameOptions(plan.ExportOptions) && !Directory.Exists(glbOutput),
                "Limbo dry run persists the exact GLB settings without creating an output folder");
            await Execute("export run " + planId);
            var result = await Done(api);
            Check(result.State == JobState.CompletedWithErrors && result.Succeeded == 1 && result.Failed == 1 && SameOptions(result.ExportOptions),
                "GLB batch records the missing named clip per asset and keeps valid output: " + string.Join(" / ", result.Items.Select(i => i.Error)));
            Check(result.Items.Single(i => i.State == ItemState.Failed).Error.Contains("Walk fast"), "missing clip name appears in the individual job error");
            Check(preview.Model.GetInstanceId() == previewInstance && Math.Abs(preview.Playhead - .37) < .001 &&
                ReferenceEquals(previewMaterial, Preview3DViewport.Descendants(preview.Model).OfType<MeshInstance3D>().Single().MaterialOverride) &&
                previewAnimation.GetTrackCount() == previewTrackCount && sourceBytes.SequenceEqual(File.ReadAllBytes(archivedA)) && originalB.SequenceEqual(File.ReadAllBytes(archivedB)),
                "export preserves archived bytes, preview model and clay material, playhead, and source tracks");

            var glb = result.Items.Single(i => i.State == ItemState.Succeeded).Output;
            var exported = MotionGlb.Read(glb);
            var metadata = MotionGlb.Unpack(File.ReadAllBytes(glb)).Json;
            Check(exported.Name == "Walk fast" && metadata["animations"]!.AsArray().Count == 1 && metadata["meshes"] == null,
                "configured GLB includes the selected existing clip and no meshes");
            var meshless = SceneAsset.Load(glb, out var storedFps, forExport: true);
            try
            {
                var rig = Preview3DViewport.Descendants(meshless).OfType<Skeleton3D>().Single();
                Check(storedFps == fps && rig.GetBoneCount() == 16 && rig.GetBoneName(rig.GetBoneParent(rig.FindBone("Head"))) == "Chest",
                    "mesh-free GLB roundtrips the same joint hierarchy and declared sampling rate");
            }
            finally { meshless.Free(); }
            var translation = exported.Channels.Single(c => c.Node == exported.ByName["Hips"] && c.Path == "translation");
            Check(translation.Times.Length == 15 && Near(translation.Times[0], 0) && Near(translation.Times[1], 1 / fps) &&
                Near(translation.Times[^1], end - start) && Near(translation.Values[0][0], 2 * start) && Near(translation.Values[^1][0], 2 * end),
                "GLB samples a rebased interval at requested ticks and preserves nonaligned boundary poses");
            var srcRot = original.Channels.Single(c => c.Node == original.ByName["LeftUpperArm"] && c.Path == "rotation");
            var outRot = exported.Channels.Single(c => c.Node == exported.ByName["LeftUpperArm"] && c.Path == "rotation");
            Check(RotationNear(MotionGlb.ValuesAt(srcRot, start), outRot.Values[0]) && RotationNear(MotionGlb.ValuesAt(srcRot, end), outRot.Values[^1]),
                "GLB resamples source quaternion poses at both trim boundaries");

            var persisted = new WorkspaceApi(api.Database, api.Storage, api.Blender);
            Check(SameOptions(persisted.Jobs.Get(result.Id).ExportOptions), "reopening job history recovers all export options");
            File.Copy(sourceA, archivedB, true); // Repair only the scratch fixture used to exercise failed-item retry.
            var retried = await Done(persisted, persisted.Jobs.Retry(result.Id));
            Check(retried.Succeeded == 1 && retried.Total == 1 && retried.RetryOf == result.Id && retried.Items[0].Source.AssetId == assetB.Id &&
                SameOptions(retried.ExportOptions) && SameOptions(api.Planner.Load(retried.PlanId).ExportOptions),
                "retry uses saved options and retries exactly the failed asset");
            await persisted.Jobs.ShutdownAsync();

            var resOutput = Folder("RES 输出");
            await Execute($"batch export_with {prefix} res {Quote(resOutput)} {compact}");
            var resBatch = await Done(api);
            Check(resBatch.Kind == JobKind.ExportRes && resBatch.Succeeded == 2 && SameOptions(resBatch.ExportOptions),
                "Limbo applies the same options to every item in a batch RES export");
            foreach (var item in resBatch.Items)
            {
                using var library = ResourceLoader.Load<AnimationLibrary>(item.Output);
                Check(library.GetAnimationList().Count == 1 && library.HasAnimation("Walk fast"), "RES contains only the requested clip");
                var animation = library.GetAnimation("Walk fast");
                int track = Enumerable.Range(0, animation.GetTrackCount()).Single(i => animation.TrackGetType(i) == Animation.TrackType.Position3D &&
                    animation.TrackGetPath(i).ToString().EndsWith(":Hips", StringComparison.Ordinal));
                var last = animation.TrackGetKeyCount(track) - 1;
                Check(Near(animation.Length, end - start) && Near(animation.Step, 1 / fps) && Near(animation.TrackGetKeyTime(track, last), end - start) &&
                    Near(animation.PositionTrackInterpolate(track, 0).X, 2 * start) && Near(animation.PositionTrackInterpolate(track, end - start).X, 2 * end),
                    "RES reloads with exact trim duration, sample spacing, and boundary values");
            }

            await Execute($"export asset {assetA.Id} glb {Quote(glbOutput)} {{\"Clips\":[\"Hold\"]}}");
            var hold = (await Done(api)).Items.Single();
            Check(hold.State == ItemState.Succeeded, "single GLB accepts a constant clip: " + hold.Error);
            var holdScene = MotionGlb.Unpack(File.ReadAllBytes(hold.Output)).Json;
            Check(holdScene["meshes"]!.AsArray().Count == 1 && holdScene["animations"]![0]!["name"]!.GetValue<string>() == "Hold" &&
                hold.Output != glb && File.Exists(glb), "constant clip retains its mesh and existing files are numbered");

            var panel = owner.Workspace.Panel("export");
            var formatControl = (OptionButton)panel.FindChild("ExportFormat", true, false)!;
            var meshControl = (CheckBox)panel.FindChild("ExportIncludeMesh", true, false)!;
            var path = (LineEdit)panel.FindChild("ExportFolder", true, false)!;
            var run = (Button)panel.FindChild("QueueExport", true, false)!;
            formatControl.Select(1);
            formatControl.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            meshControl.ButtonPressed = false;
            path.Text = Folder("App export");
            Check(!run.Disabled && owner.SelectedAssetIds.SequenceEqual([assetA.Id]), "App export panel uses the real library selection");
            run.EmitSignal(BaseButton.SignalName.Pressed);
            var app = await Done(api);
            Check(app.Kind == JobKind.ExportGlb && app.Succeeded == 1 && app.ExportOptions is { IncludeMesh: false, Clips: null } &&
                MotionGlb.Unpack(File.ReadAllBytes(app.Items[0].Output)).Json["animations"]!.AsArray().Count == 3,
                "App controls queue all existing clips through the same GLB job service");

            var cancelPlan = api.Planner.Export(prefix, Folder("cancelled"), JobKind.ExportRes, options);
            void CancelAfterFirst(JobUpdate update)
            {
                if (update.State == JobState.Running && update.Finished == 1 && update.Message.EndsWith("Succeeded", StringComparison.Ordinal))
                    Callable.From(() => { api.Jobs.Cancel(update.Id); }).CallDeferred();
            }
            api.Jobs.Changed += CancelAfterFirst;
            var cancelled = await Done(api, api.Jobs.Enqueue(cancelPlan));
            api.Jobs.Changed -= CancelAfterFirst;
            Check(cancelled.State == JobState.Cancelled && cancelled.Succeeded == 1 && cancelled.Cancelled == 1 &&
                Directory.EnumerateFiles(cancelPlan.OutputFolder, "*.res").Count() == 1,
                "in-process batch yields to UI cancellation between outputs");

            var invalidId = Guid.NewGuid().ToString("N");
            var altered = JsonSerializer.SerializeToNode(plan)!;
            altered["Id"] = invalidId;
            altered["ExportOptions"]!["SampleFps"] = 0;
            File.WriteAllText(Path.Combine(api.Planner.PlansFolder, invalidId + ".json"), altered.ToJsonString());
            bool rejected = false;
            try { _ = api.Planner.Load(invalidId); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid persisted options are rejected before job admission");

            if (api.Blender.Available)
            {
                await Execute($"export asset {assetA.Id} fbx {Quote(Folder("FBX 输出"))} {compact}");
                var fbxJob = await Done(api);
                Check(fbxJob.Succeeded == 1 && SameOptions(fbxJob.ExportOptions), "configured FBX uses the durable export queue: " + fbxJob.Items[0].Error);
                var fbx = fbxJob.Items[0].Output;
                Check(FbxAttributeReader.Read(fbx, default).SourceFps == fps, "configured FBX records the requested frame rate in the header");
                var fbxScene = SceneAsset.Load(fbx, out _, forExport: true);
                try
                {
                    owner.GetTree().Root.AddChild(fbxScene);
                    var nodes = Preview3DViewport.Descendants(fbxScene).ToArray();
                    var rig = nodes.OfType<Skeleton3D>().Single();
                    var player = nodes.OfType<AnimationPlayer>().Single();
                    var clip = player.GetAnimationList().Single();
                    Check(!nodes.OfType<MeshInstance3D>().Any() && rig.GetBoneCount() == 16 && clip == "Walk fast" &&
                        Near(player.GetAnimation(clip).Length, end - start, .001),
                        $"configured FBX keeps the meshless rig and selected take duration ({rig.GetBoneCount()} joints, " +
                        $"{nodes.OfType<MeshInstance3D>().Count()} meshes, '{clip}' length {player.GetAnimation(clip).Length:G7} s)");
                    player.Play(clip);
                    player.Advance(0);
                    player.Pause();
                    Vector3 HipsAt(double time)
                    {
                        player.Seek(time, true);
                        return rig.GlobalTransform * rig.GetBoneGlobalPose(rig.FindBone("Hips")).Origin;
                    }
                    var delta = HipsAt(end - start).DistanceTo(HipsAt(0));
                    Check(Near(delta, 2 * (end - start), .002), $"FBX keeps both boundary poses on nonaligned trims (motion {delta:G7} m)");
                }
                finally { fbxScene.Free(); }

                var fbxImport = await api.ImportAsync([fbx]);
                Check(fbxImport.Added == 1, "reimport the configured FBX as an existing scene source");
                var fbxAsset = api.Database.FindByHash(MotionJson.Hash(File.ReadAllBytes(fbx)))!;
                var optionsWithoutRate = new ExportOptions { StartSeconds = .1, EndSeconds = .5 };
                var recrop = await api.ExportAsync(fbxAsset.Id, Folder("From FBX"), JobKind.ExportGlb, optionsWithoutRate);
                var recropJson = MotionGlb.Unpack(File.ReadAllBytes(recrop)).Json;
                Check(recropJson["extras"]!["animanagerSourceFps"]!.GetValue<double>() == fps &&
                    Near(MotionGlb.Read(recrop).Channels.Max(channel => channel.Times[^1]), .4),
                    "crop-only GLB from binary FBX keeps the declared source rate");
                var recropRes = await api.ExportAsync(fbxAsset.Id, Folder("From FBX"), JobKind.ExportRes, optionsWithoutRate);
                using (var library = ResourceLoader.Load<AnimationLibrary>(recropRes))
                {
                    var animation = library.GetAnimation(library.GetAnimationList()[0]);
                    Check(Near(animation.Length, .4) && animation.GetTrackCount() > 0,
                        "crop-only RES from binary FBX keeps the selected source interval and track data");
                }

                var sceneInput = Fixture(Folder(prefix + " Multi.glb"), "Walk fast", animateMarker: true);
                await api.ImportAsync([sceneInput]);
                var sceneAsset = api.Find(Path.GetFileNameWithoutExtension(sceneInput));
                var mergedFbx = await api.ExportAsync(sceneAsset.Id, Folder("multi-target"), JobKind.ExportFbx,
                    options with { Clips = ["Walk fast", "Alternate"] });
                var grouped = SceneAsset.Load(mergedFbx, out _, forExport: true);
                try
                {
                    owner.GetTree().Root.AddChild(grouped);
                    var nodes = Preview3DViewport.Descendants(grouped).ToArray();
                    var player = nodes.OfType<AnimationPlayer>().Single();
                    var marker = nodes.OfType<Node3D>().Single(node => node.Name == "MovingMarker");
                    var rig = nodes.OfType<Skeleton3D>().Single();
                    Check(player.GetAnimationList().Order().SequenceEqual(new[] { "Alternate", "Walk fast" }),
                        "FBX has one whole-scene take per requested clip");
                    (float Marker, float Hips) Travel(string clip)
                    {
                        player.Stop();
                        player.Play(clip);
                        player.Advance(0);
                        player.Pause();
                        Vector3 Hips() => rig.GlobalTransform * rig.GetBoneGlobalPose(rig.FindBone("Hips")).Origin;
                        var markerStart = marker.GlobalPosition;
                        var hipsStart = Hips();
                        player.Seek(end - start, true);
                        return (marker.GlobalPosition.DistanceTo(markerStart), Hips().DistanceTo(hipsStart));
                    }
                    var walk = Travel("Walk fast");
                    var alternate = Travel("Alternate");
                    Check(Near(walk.Marker, 3 * (end - start), .002) && Near(walk.Hips, 2 * (end - start), .002) &&
                        Near(alternate.Marker, 0, .002) && Near(alternate.Hips, 2 * (end - start), .002),
                        $"FBX keeps rig and independent motion together with no slot leakage ({walk} / {alternate})");
                }
                finally { grouped.Free(); }
            }
            else GD.Print("SKIP configured FBX roundtrip, Blender unavailable");

            var motionDir = ProjectSettings.GlobalizePath("res://Libraries/Nodachi-Motion/motion");
            if (Directory.Exists(motionDir) && Directory.EnumerateFiles(motionDir, "*.motion.json").FirstOrDefault() is { } motionFixture)
            {
                var bytes = File.ReadAllBytes(motionFixture);
                var motionImport = await api.ImportAsync([motionFixture]);
                var motion = api.Database.FindByHash(MotionJson.Hash(bytes)) ?? throw new IOException(string.Join("\n", motionImport.Errors));
                var manifest = MotionJson.Load(motionFixture, false);
                var settings = new ExportOptions { IncludeMesh = false, StartSeconds = .073, EndSeconds = Math.Min(.318, manifest.Duration), SampleFps = 25 };
                var output = await api.ExportAsync(motion.Id, Folder("motion"), JobKind.ExportGlb, settings);
                var data = MotionGlb.Read(output);
                var json = MotionGlb.Unpack(File.ReadAllBytes(output)).Json;
                Check(data.Channels.Any(c => data.Nodes[c.Node].Name == "PrimaryWeaponMotion") && json["meshes"] == null &&
                    json["skins"]!.AsArray().Any(skin => skin!["joints"]!.AsArray().Count == 161) && bytes.SequenceEqual(File.ReadAllBytes(motionFixture)),
                    "configured .motion GLB retains 161 joints and existing independent prop tracks without mesh or source edits");

                if (api.Blender.Available)
                {
                    var runtime = new MotionPlayback(motionFixture);
                    owner.GetTree().Root.AddChild(runtime.Root);
                    Transform3D[] expected;
                    try
                    {
                        runtime.Player.Play(manifest.Clip);
                        runtime.Player.Advance(0);
                        runtime.Player.Pause();
                        expected = new[] { settings.StartSeconds!.Value, settings.EndSeconds!.Value }.Select(time =>
                        {
                            runtime.Player.Seek(time, true);
                            runtime.Sync();
                            return runtime.Prop.GlobalTransform;
                        }).ToArray();
                    }
                    finally { runtime.Root.Free(); }
                    var fbx = await api.ExportAsync(motion.Id, Folder("motion"), JobKind.ExportFbx, settings);
                    var scene = SceneAsset.Load(fbx, out var rate, forExport: true);
                    try
                    {
                        owner.GetTree().Root.AddChild(scene);
                        var nodes = Preview3DViewport.Descendants(scene).ToArray();
                        var player = nodes.OfType<AnimationPlayer>().Single();
                        var prop = nodes.OfType<Node3D>().Single(node => node.Name == "PrimaryWeaponMotion");
                        var rig = nodes.OfType<Skeleton3D>().Single();
                        var clip = player.GetAnimationList().Single();
                        var duration = settings.EndSeconds!.Value - settings.StartSeconds!.Value;
                        Check(clip == manifest.Clip && rate == 25 && rig.GetBoneCount() == 161 && !nodes.OfType<MeshInstance3D>().Any() &&
                            Near(player.GetAnimation(clip).Length, duration, .001),
                            "configured .motion FBX retains the complete rig, requested sample rate, and interval");
                        player.Play(clip);
                        player.Advance(0);
                        player.Pause();
                        Transform3D Sample(double time) { player.Seek(time, true); return prop.GlobalTransform; }
                        var actual = new[] { Sample(0), Sample(duration) };
                        float distance = Enumerable.Range(0, 2).Max(i => expected[i].Origin.DistanceTo(actual[i].Origin));
                        float rotation = Enumerable.Range(0, 2).Min(i => Math.Abs(expected[i].Basis.GetRotationQuaternion().Dot(actual[i].Basis.GetRotationQuaternion())));
                        Check(distance < .005f && rotation > .9999f,
                            $"configured .motion FBX roundtrips independent prop at both trim endpoints ({distance:G5} m, {rotation:G7} rotation dot)");
                    }
                    finally { scene.Free(); }
                    var sameRate = await api.ExportAsync(motion.Id, Folder("motion"), JobKind.ExportFbx, settings with { SampleFps = null });
                    Check(Near(FbxAttributeReader.Read(sameRate, default).SourceFps ?? 0, manifest.SourceFps, .001),
                        "configured .motion FBX without an FPS override keeps the declared manifest rate");
                }
            }
            else GD.Print("SKIP configured .motion roundtrip, Nodachi fixture unavailable");

            string report = $"PASS: {checks.Count} export option checks\n" + string.Join("\n", checks);
            File.WriteAllText(results, report);
            GD.Print(report);
            owner.GetTree().Quit(0);
        }
        catch (Exception error)
        {
            await api.Jobs.ShutdownAsync();
            File.WriteAllText(results, $"{checks.Count} passed\n" + error);
            GD.PushError("EXPORT OPTIONS TEST FAILED: " + error);
            owner.GetTree().Quit(1);
        }
    }

    private static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";
    private static bool Near(double actual, double expected, double tolerance = .00002) => Math.Abs(actual - expected) < tolerance;
    private static bool RotationNear(float[] source, float[] output) => Math.Abs(System.Numerics.Quaternion.Dot(
        MotionGlb.QuaternionOf(source), MotionGlb.QuaternionOf(output))) > .99999f;

    private static string Fixture(string path, string name, bool animateMarker = false)
    {
        var sample = File.ReadAllBytes(ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_Walk.glb"));
        var (json, original) = MotionGlb.Unpack(sample);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(original);
        var accessors = json["accessors"]!.AsArray();
        var views = json["bufferViews"]!.AsArray();
        int Accessor(float[] values, int count, string type)
        {
            long offset = stream.Position;
            foreach (float value in values) writer.Write(value);
            var accessor = new JsonObject { ["bufferView"] = views.Count, ["componentType"] = 5126, ["count"] = count, ["type"] = type };
            if (type == "SCALAR") { accessor["min"] = new JsonArray(values[0]); accessor["max"] = new JsonArray(values[^1]); }
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = stream.Position - offset });
            accessors.Add(accessor);
            return accessors.Count - 1;
        }
        var animations = json["animations"]!.AsArray();
        var clip = animations[0]!;
        clip["name"] = name;
        var channel = clip["channels"]!.AsArray().Single(c => c!["target"]!["path"]!.GetValue<string>() == "translation")!;
        var sampler = clip["samplers"]![channel["sampler"]!.GetValue<int>()]!;
        int times = Accessor([0, 1.2f], 2, "SCALAR");
        sampler["input"] = times;
        sampler["output"] = Accessor([0, 1, 0, 2.4f, 1, 0], 2, "VEC3");
        var alternate = clip.DeepClone();
        alternate["name"] = "Alternate";
        animations.Add(alternate);
        if (animateMarker)
        {
            var nodes = json["nodes"]!.AsArray();
            int marker = nodes.Count;
            nodes.Add(new JsonObject { ["name"] = "MovingMarker", ["translation"] = new JsonArray(.7f, .4f, 0) });
            json["scenes"]![0]!["nodes"]!.AsArray().Add(marker);
            var samplers = clip["samplers"]!.AsArray();
            clip["channels"]!.AsArray().Add(new JsonObject
            {
                ["sampler"] = samplers.Count, ["target"] = new JsonObject { ["node"] = marker, ["path"] = "translation" }
            });
            samplers.Add(new JsonObject { ["input"] = times, ["output"] = Accessor([.7f, .4f, 0, 4.3f, .4f, 0], 2, "VEC3") });
        }
        animations.Add(new JsonObject
        {
            ["name"] = "Hold",
            ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = Accessor([0, 1, 0, 0, 1, 0], 2, "VEC3") }),
            ["channels"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["target"] = channel["target"]!.DeepClone() })
        });
        json["buffers"]![0]!["byteLength"] = stream.Length;
        json["asset"]!["generator"] = Path.GetFileName(path);
        File.WriteAllBytes(path, MotionGlb.Pack(json, stream.ToArray()));
        return path;
    }
}
