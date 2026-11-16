using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;
using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Animanager.Tests;

public static class LibraryP0Tests
{
    public static async Task Run(MainShell owner, ConsoleCommands commands)
    {
        var root = Path.GetFullPath(ProjectSettings.GlobalizePath("res://TestResults/library-p0-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var skipped = new List<string>();
        var console = owner.GetNode("/root/LimboConsole");
        var manager = owner.Libraries;
        var fixture = manager.Active!;
        var preview = Preview3DViewport.Descendants(owner.Workspace.Panel("preview")).OfType<Preview3DViewport>().Single();

        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks.Add(description);
            GD.Print("PASS " + description);
        }

        void Save(string error) => MotionJson.Atomic(Path.Combine(root, "results.json"), new { checks, skipped, error });
        async Task Execute(string command, bool failure = false)
        {
            console.Call("execute_command", command);
            await commands.PendingTask;
            bool failed = commands.LastResult.StartsWith("ERROR:", StringComparison.Ordinal);
            if (failed != failure) throw new InvalidOperationException(command + "\n" + commands.LastResult);
        }

        async Task Frames(int count = 6)
        {
            for (int i = 0; i < count; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        try
        {
            var explicitPath = RuntimePaths.ExplicitLibraryRoot();
            Check(explicitPath == null || LibraryManager.SamePath(Path.GetDirectoryName(fixture.Root)!, explicitPath),
                "Explicit --data-dir chooses the isolated startup library");
            var startupRegistry = Path.Combine(root, "startup.json");
            var firstRoot = Path.Combine(root, "first-startup");
            var recentRoot = Path.Combine(root, "recent-startup");
            using (var startup = new LibraryManager(startupRegistry, fixture.Api.Blender))
            {
                startup.CreateAndOpen(firstRoot, "First");
                startup.CreateAndOpen(recentRoot, "Recent");
            }
            using (var restart = new LibraryManager(startupRegistry, fixture.Api.Blender))
            {
                Check(restart.OpenStartup(null, firstRoot).Session?.Root == recentRoot &&
                    restart.OpenStartup(firstRoot, recentRoot).Session?.Root == firstRoot, "Startup remembers the last library and explicit paths take priority");
            }
            Directory.Move(firstRoot, firstRoot + "-moved");
            using (var restart = new LibraryManager(startupRegistry, fixture.Api.Blender))
            {
                var preferred = restart.OpenStartup(null, recentRoot);
                Check(preferred.Session?.Root == recentRoot && preferred.Message.Contains(firstRoot), "Startup skips a missing recent library with a diagnostic");
            }
            var fixtureId = fixture.Identity.Id;
            await Execute("library current");
            Check(commands.LastResult.Contains(fixtureId), "Limbo reports the active library identity");
            var samples = RuntimePaths.Bundled("BuiltinAssets/Samples");
            await Execute("asset import " + Quote(samples));
            var seed = fixture.Api.Find("StudioBot_Walk");
            var untouched = fixture.Api.Find("StudioBot_Run");
            await Execute("asset rename " + seed.Id + " " + Quote("测试 Walk"));
            await Execute("asset tags_add " + seed.Id + " " + Quote("步行,humanoid"));
            await Execute("asset tags_remove " + seed.Id + " humanoid");
            await Execute("asset favorites " + seed.Id + " true");
            await Execute("collection create " + Quote("第一组"));
            await Execute("collection create " + Quote("第二组"));
            var collections = fixture.Api.Database.Collections();
            foreach (var collection in collections) await Execute($"collection add {collection.Id} {seed.Id}");
            Check(fixture.Api.Find(seed.Id).Name == "测试 Walk" && fixture.Api.Find(seed.Id).VirtualPath == seed.VirtualPath &&
                fixture.Api.Find(seed.Id).Favorite && fixture.Api.Find(seed.Id).Tags == "步行" && fixture.Api.Find(untouched.Id) == untouched,
                "Rename, tag and favorite operations only update selected metadata");
            Check(collections.All(c => fixture.Api.Search(category: c.Id).Items.Single().Id == seed.Id),
                "One asset is referenced from two collections");
            await Execute("preview load " + seed.Id);
            await Execute("asset trash " + seed.Id);
            Check(!fixture.Api.Database.SearchAll().Any(a => a.Id == seed.Id) &&
                fixture.Api.Database.SearchAll(category: "Trash").Single().Id == seed.Id && preview.Model == null,
                "Trashed assets leave the live list and their previews are cleared");
            var duplicate = await fixture.Api.ImportAsync([Path.Combine(samples, "StudioBot_Walk.glb")]);
            Check(duplicate.Skipped == 1 && duplicate.Added == 0, "Trashed content remains deduplicated");
            await Execute("asset restore " + seed.Id);
            Check(collections.All(c => fixture.Api.Search(category: c.Id).Items.Single().Id == seed.Id) &&
                fixture.Api.Find(seed.Id).Favorite && fixture.Api.Find(seed.Id).Tags == "步行",
                "Trash restore retains identity, tags, favorite and collection references");
            await Execute($"collection remove {collections[0].Id} {seed.Id}");
            Check(fixture.Api.Find(seed.Id) != null && fixture.Api.Search(category: collections[0].Id).Total == 0,
                "Removing a collection reference keeps the original asset");

            var moveRoot = Path.Combine(root, "movable");
            var movedRoot = Path.Combine(root, "moved");
            var original = manager.Create(moveRoot, "Move test");
            Directory.Move(moveRoot, movedRoot);
            await Execute($"library open {original.Id}", failure: true);
            Check(manager.Active == fixture, "Failed switch keeps the active session");
            await Execute($"library relocate {original.Id} {Quote(movedRoot)}");
            var queued = fixture.Api.Jobs.Enqueue(await fixture.Api.Planner.ImportAsync([samples]));
            await Execute($"library open {original.Id}", failure: true);
            await fixture.Api.Jobs.WaitAsync(queued);
            while (fixture.Api.Jobs.Busy) await Frames(1);
            Check(manager.Active == fixture, "Switch refuses while an actual queue job is admitted");
            await Execute($"library open {original.Id}");
            Check(manager.Active!.Identity.Id == original.Id && manager.Active.Api.Search().Total == 0 && preview.Model == null &&
                manager.Active.Api.Jobs.Recent().Length == 0 && commands.SelectedId == null, "Switch resets preview, selection, and job history");
            var clone = Path.Combine(root, "clone-conflict");
            CopyLibrary(movedRoot, clone);
            await Execute("library register " + Quote(clone), failure: true);
            Check(manager.Active.Root == movedRoot, "Duplicate identity at an accessible path is rejected");
            using (var competing = new LibraryManager(Path.Combine(root, "competing.json"), new BlenderPipeline(fixture.Api.Blender.ScriptPath)))
            {
                try { competing.Open(movedRoot); throw new InvalidOperationException("Concurrent process lock was not acquired"); }
                catch (IOException) { Check(competing.Active == null, "A second manager cannot open a leased library"); }
            }

            await Execute($"library open {fixtureId}");
            var current = manager.Active!.Api;
            Check(current.Find(seed.Id).Tags == "步行" && current.Jobs.Recent().Length >= 2 &&
                manager.Entries().First().Id == fixtureId, "Switching back restores library metadata, jobs and most-recent order");
            var snapshots = current.Database.Snapshot();
            File.WriteAllText(Path.Combine(current.Storage.Root, AssetDatabase.IndexName), "intentionally damaged");
            Check((await new LibraryMaintenance(current).CheckAsync()).Any(i => i.Path == AssetDatabase.IndexName), "Damaged index is diagnosed");
            await Execute("library rebuild " + Quote(current.Storage.Root));
            Check(current.Database.Snapshot().Assets.SequenceEqual(snapshots.Assets) &&
                current.Database.Snapshot().Collections.Zip(snapshots.Collections).All(pair => pair.First.AssetIds.SequenceEqual(pair.Second.AssetIds)) &&
                current.Search("步行").Items.Single().Id == seed.Id, "Rebuild restores asset metadata, FTS and collection index");
            var badLegacy = Path.Combine(root, "bad-legacy");
            Directory.CreateDirectory(badLegacy);
            File.WriteAllText(Path.Combine(badLegacy, AssetDatabase.IndexName), "damaged legacy");
            await Execute("library open " + Quote(badLegacy), failure: true);
            Check(commands.LastResult.Contains("无法找回原 ID"), "Legacy damage reports unavailable fields");

            var fixtureBackup = Path.Combine(root, "fixture-backup");
            var fixtureRestore = Path.Combine(root, "fixture-restored");
            await Execute("library backup " + Quote(fixtureBackup));
            await Execute("library register " + Quote(fixtureBackup), failure: true);
            Check(!File.Exists(Path.Combine(fixtureBackup, LibraryManager.LockName)), "Registration recognizes a backup before opening or modifying it");
            await Execute($"library restore {Quote(fixtureBackup)} {Quote(fixtureRestore)}");
            Check(manager.Active!.Identity.Id != fixtureId && manager.Active.Root == fixtureRestore &&
                manager.Active.Api.Find(seed.Id).Name == "测试 Walk" && manager.Active.Api.Jobs.Recent().Length >= 2,
                "Backup and restore installs an independent identity with complete data and reports");
            await Execute($"library restore {Quote(fixtureBackup)} {Quote(fixtureRestore)}", failure: true);
            Check(manager.Active.Root == fixtureRestore, "Restore requires a new target directory");
            var corruptBackupFile = Path.Combine(fixtureBackup, AssetDatabase.CatalogName);
            File.AppendAllText(corruptBackupFile, "changed");
            await Execute($"library restore {Quote(fixtureBackup)} {Quote(Path.Combine(root, "invalid-restore"))}", failure: true);
            Check(manager.Active.Root == fixtureRestore && !Directory.Exists(Path.Combine(root, "invalid-restore")),
                "Damaged backups are rejected before publishing a restore");

            var nodachiSource = ProjectSettings.GlobalizePath("res://Libraries/Nodachi-Motion");
            var movementSource = ProjectSettings.GlobalizePath("res://Libraries/MovementAnimsetPro");
            if (!LibraryManager.HasLibrary(nodachiSource) || !LibraryManager.HasLibrary(movementSource))
            {
                skipped.Add("Real-library workflow requires the local Nodachi-Motion and MovementAnimsetPro libraries.");
                GD.Print("SKIP " + skipped[^1]);
            }
            else
            {
                var nodachi = Path.Combine(root, "Nodachi");
                var movement = Path.Combine(root, "Movement");
                await Task.Run(() => { CopyLibrary(nodachiSource, nodachi); CopyLibrary(movementSource, movement); });
                await Execute("library open " + Quote(nodachi));
                var p0 = manager.Active!.Api;
                var spin = p0.Database.SearchAll().Single(a => a.SourceFormat == ".motion" && a.Name.EndsWith("__AS_Combo_Attack_03_All_Seq"));
                Check(p0.Database.SearchAll().Count(a => a.SourceFormat == ".motion") == 740, "Legacy Nodachi migration preserves all 740 motions");
                var rangeCount = p0.Database.SearchAll().Count;
                owner.DisplayScale.SetScale(1);
                owner.GetWindow().Size = new(1440, 1000);
                if (DisplayServer.GetName() != "headless") owner.GetWindow().GrabFocus();
                owner.Workspace.UsePreset("browse");
                owner.Workspace.ShowPanel("assets");
                await Frames(30);
                var panel = owner.Workspace.Panel("assets");
                var rows = (ItemList)panel.FindChild("AssetRows", true, false)!;
                var next = (Button)panel.FindChild("AssetPageNext", true, false)!;
                if (DisplayServer.GetName() == "headless") owner.GetViewport().NotifyMouseEntered();
                async Task ClickRow(int index, bool control = false, bool shift = false)
                {
                    var viewport = owner.GetViewport();
                    var local = new Vector2(36, rows.GetItemRect(index).GetCenter().Y);
                    if (rows.GetItemAtPosition(local) != index || !rows.IsVisibleInTree())
                        throw new InvalidOperationException($"Asset click target {index} missing at {local} in {rows.Size}.");
                    var point = rows.GetGlobalTransformWithCanvas() * local;
                    if (DisplayServer.GetName() != "headless")
                    {
                        Input.WarpMouse(point * owner.GetWindow().ContentScaleFactor);
                        await Frames(4);
                        Input.FlushBufferedEvents();
                    }
                    using var motion = new InputEventMouseMotion { Position = point, GlobalPosition = point, CtrlPressed = control, ShiftPressed = shift };
                    using var press = new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left,
                        Pressed = true, CtrlPressed = control, ShiftPressed = shift };
                    using var release = (InputEventMouseButton)press.Duplicate();
                    release.Pressed = false;
                    viewport.PushInput(motion, true);
                    viewport.PushInput(press, true);
                    viewport.PushInput(release, true);
                    await Frames();
                }

                await ClickRow(0);
                Check(owner.SelectedAssetIds.Length == 1, $"Single click selects one asset (native rows {string.Join(',', rows.GetSelectedItems())})");
                await ClickRow(2, shift: true);
                Check(owner.SelectedAssetIds.Length == 3, $"Shift selects a contiguous visible asset range (selected {owner.SelectedAssetIds.Length})");
                await ClickRow(1, control: true);
                Check(owner.SelectedAssetIds.Length == 2, "Ctrl removes one member without affecting other rows");
                next.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                await ClickRow(0, control: true);
                var ids = owner.SelectedAssetIds;
                Check(ids.Length == 3 && rows.GetSelectedItems().Length == 1, "Ctrl extends selection across asset pages");
                var search = (LineEdit)panel.FindChild("AssetSearch", true, false)!;
                search.Text = "P0-test-No-Matches";
                search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
                await owner.ToSignal(owner.GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
                Check(rows.ItemCount == 0 && owner.SelectedAssetIds.ToHashSet().SetEquals(ids), "Search keeps all selected IDs when they are hidden");
                await Execute("collection create " + Quote("验收动作"));
                var collectionId = p0.Database.Collections().Single(c => c.Name == "验收动作").Id;
                await Execute($"collection add {collectionId} {string.Join(',', ids)}");
                Check(p0.Database.SearchAll(category: collectionId).Count == ids.Length && p0.Search().Total == rangeCount,
                    "Batch collection includes the exact cross-page selection");

                var spinPath = p0.MotionPath(spin.Id);
                var spinManifest = MotionJson.Load(spinPath);
                p0.SetMotionGrip(spin.Id, [.013f, 0, 0], [0, 0, 0, 1]);
                p0.SetMotionContact(spin.Id, .1, .2, "right");
                var savedPresentation = File.ReadAllBytes(MotionJson.PresentationPath(spinPath));
                p0.SetTrash([spin.Id], true);
                p0.SetTrash([spin.Id], false);
                Check(File.ReadAllBytes(MotionJson.PresentationPath(spinPath)).SequenceEqual(savedPresentation), "Nodachi trash restore retains grip and contacts");
                var beforeExport = MotionExport.Glb(spinPath, Path.Combine(root, "before-cache.glb"), true);
                var purge = p0.Database.SearchAll().First(a => a.SourceFormat == ".motion" && a.Id != spin.Id);
                p0.SetTrash([purge.Id], true);
                await Execute($"asset purge {purge.Id} true");
                Check(p0.Database.FindByHash(purge.FileHash) == null && File.Exists(MotionJson.ResolveObject(spinPath, spinManifest.Objects["preset"])) &&
                    File.Exists(MotionJson.ResolveObject(spinPath, spinManifest.Objects["rig"])), "Permanent removal keeps shared Manny and rig objects");
                var historical = p0.Database.SearchAll().First(a => a.SourceFormat == ".motion" && a.Id != spin.Id);
                var historicalPath = p0.MotionPath(historical.Id);
                p0.SetMotionGrip(historical.Id, [.01f, 0, 0], [0, 0, 0, 1]);
                p0.SetTrash([historical.Id], true);
                await Execute($"asset purge {historical.Id} true");
                Check(p0.Database.FindByHash(historical.FileHash) == null && File.Exists(historicalPath) && MotionJson.Load(historicalPath).Objects.Count > 0,
                    "Permanent removal preserves the manifest and objects used by retained revisions");

                var bodyPath = MotionJson.ResolveObject(spinPath, spinManifest.Objects["body"]);
                var parkedBody = bodyPath + ".validation";
                File.Move(bodyPath, parkedBody);
                try
                {
                    var issues = await new LibraryMaintenance(p0).CheckAsync();
                    Check(issues.Any(i => i.AssetId == spin.Id && i.Path.EndsWith(spinManifest.Objects["body"].Path)),
                        "Integrity check identifies the asset and its missing body track");
                }
                finally { File.Move(parkedBody, bodyPath); }

                await Execute("library open " + Quote(movement));
                Check(manager.Active!.Api.Search().Total > 300 && preview.Model == null &&
                    !manager.Active.Api.Database.Collections().Any(c => c.Name == "验收动作"), "Movement opens with separate asset and collection state");
                var cachePath = SeedCache(nodachi, spin);
                var libraryButton = (Button)owner.FindChild("LibraryButton", true, false)!;
                libraryButton.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                var libraryDialog = (AcceptDialog)owner.FindChild("LibraryDialog", true, false)!;
                var libraryRows = (ItemList)libraryDialog.FindChild("LibraryRows", true, false)!;
                Check(libraryDialog.Visible, "Toolbar opens the Library manager");
                libraryRows.Select(Enumerable.Range(0, libraryRows.ItemCount).Single(i => libraryRows.GetItemText(i).StartsWith("Nodachi · ")));
                var openChoice = (Button)libraryDialog.FindChild("OpenLibraryChoice", true, false)!;
                openChoice.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                await Execute("library current");
                Check(manager.Active!.Root == nodachi && commands.LastResult.Contains("Nodachi"), "Library UI and Limbo switch the same active session");
                var nodachiApi = manager.Active!.Api;
                var maintenance = new LibraryMaintenance(nodachiApi);
                var cache = await maintenance.PreviewCacheAsync();
                Check(cache.Files.Any(f => f.Path == cachePath) && !cache.Files.Any(f => f.Path.EndsWith("preserve.bin")),
                    "Cache preview includes a verified completed output and preserves unknown files");
                var cleanup = await maintenance.ClearCacheAsync(cache);
                Check(cleanup.Removed > 0 && !File.Exists(nodachiApi.Storage.Resolve(cachePath)) &&
                    File.Exists(nodachiApi.Storage.Resolve(Path.GetDirectoryName(cachePath)! + "/preserve.bin")),
                    "Cache cleanup only removes the confirmed file set");
                var afterExport = MotionExport.Glb(nodachiApi.MotionPath(spin.Id), Path.Combine(root, "after-cache.glb"), true);
                Check(SameExport(beforeExport, afterExport) &&
                    File.ReadAllBytes(MotionJson.PresentationPath(nodachiApi.MotionPath(spin.Id))).SequenceEqual(savedPresentation),
                    "Exported geometry and tracks, grip and contacts match after cache cleanup");

                var backup = Path.Combine(root, "nodachi-backup");
                var restored = Path.Combine(root, "nodachi-restored");
                await Execute("library backup " + Quote(backup));
                await Execute($"library restore {Quote(backup)} {Quote(restored)}");
                var restoredApi = manager.Active!.Api;
                Directory.Move(nodachi, nodachi + "-offline");
                Check(restoredApi.Search().Total == rangeCount - 2 && File.Exists(restoredApi.Storage.Resolve(historical.VirtualPath)) &&
                    restoredApi.Search(category: collectionId).Total == ids.Count(id => id != purge.Id && id != historical.Id),
                    "Full Nodachi restore retains asset count, collection references and preserved revision data");
                await Execute("preview load " + spin.Id);
                Check(preview.Motion != null && preview.BoneCount == 161 &&
                    Preview3DViewport.Descendants(preview.Model!).OfType<MeshInstance3D>().Any(m => m.Skin?.GetBindCount() == 89),
                    "Restored Nodachi plays with the fixed skeleton and original skin bindings");
                var restoredPath = restoredApi.MotionPath(spin.Id);
                Check(File.ReadAllBytes(MotionJson.PresentationPath(restoredPath)).SequenceEqual(savedPresentation) &&
                    MotionJson.ResolveObject(restoredPath, spinManifest.Objects["source"]).StartsWith(restored, StringComparison.OrdinalIgnoreCase),
                    "Restored motion uses its archived local source and saved presentation");
            }

            Save("");
            owner.GetTree().Quit();
        }
        catch (Exception ex)
        {
            Save(ex.ToString());
            GD.PushError(ex.ToString());
            owner.GetTree().Quit(1);
        }
    }

    private static string Quote(string value) => "\"" + value.Replace('\\', '/') + "\"";

    private static bool SameExport(string firstPath, string secondPath)
    {
        static (JsonNode Json, byte[] Binary) Read(string path)
        {
            var data = File.ReadAllBytes(path);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4));
            var json = JsonNode.Parse(data.AsSpan(20, length))!;
            foreach (var node in json["nodes"]!.AsArray())
                if (node?["name"]?.GetValue<string>().StartsWith("_PhysicalBoneSimulator3D_", StringComparison.Ordinal) == true)
                    node["name"] = "_PhysicalBoneSimulator3D";
            return (json, data[(20 + length)..]);
        }
        var first = Read(firstPath);
        var second = Read(secondPath);
        return JsonNode.DeepEquals(first.Json, second.Json) && first.Binary.AsSpan().SequenceEqual(second.Binary);
    }

    private static void CopyLibrary(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative is ".library.lock" or "index.sqlite-shm") continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string SeedCache(string root, AssetItem asset)
    {
        var id = Guid.NewGuid().ToString("N");
        var storage = new LocalStorageProvider(root);
        var cache = storage.Resolve($"cache/conversion/{id}");
        var reports = storage.Resolve($"uasset/{id}");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(reports);
        var name = "completed.motion.json";
        var output = Path.Combine(cache, name);
        File.Copy(storage.Resolve(asset.VirtualPath), output);
        File.WriteAllText(Path.Combine(cache, "preserve.bin"), "not listed in reports");
        var report = Path.Combine(reports, "conversion-report.json");
        const string source = "verified-fixture.uasset";
        MotionJson.Atomic(report, new UassetReport { Version = 3, Converted = 1,
            Items = [new UassetResult { Output = output, Source = source, Status = "converted", OutputSha256 = asset.FileHash }] });
        MotionJson.Atomic(Path.Combine(reports, "import-report.json"), new { version = 2, errors = Array.Empty<string>(),
            items = new[] { new { source, id = asset.Id, path = asset.VirtualPath, sha256 = asset.FileHash, status = "added" } } });
        var job = new JobRecord { Kind = JobKind.ImportUasset, State = JobState.Completed,
            Items = [new JobItem { Output = report, State = ItemState.Succeeded }] };
        var jobs = storage.Resolve("jobs/reports");
        Directory.CreateDirectory(jobs);
        BatchPlanner.AtomicJson(Path.Combine(jobs, job.Id + ".json"), job);
        return $"cache/conversion/{id}/{name}";
    }
}
