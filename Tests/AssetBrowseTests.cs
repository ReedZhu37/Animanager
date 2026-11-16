using System.Text.Json;
using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Console;
using Animanager.Core.Motion;
using Animanager.Core.Storage;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

public static class AssetBrowseTests
{
    public static async Task Run(MainShell owner, WorkspaceApi api, ConsoleCommands commands)
    {
        var output = Path.Combine(api.Storage.Root, "asset-browse-results");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks.Add(message);
            GD.Print("PASS " + message);
        }
        try
        {
            if (OS.GetCmdlineUserArgs().Contains("--asset-browse-render")) await CheckRendering(owner, api, commands, output, Check);
            else await CheckFilters(owner, api, commands, output, Check);
            var result = $"PASS: {checks.Count} asset browse checks\n" + string.Join("\n", checks);
            File.WriteAllText(Path.Combine(output, "results.txt"), result);
            GD.Print(result);
            owner.GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PushError("ASSET BROWSE TEST FAILED: " + ex);
            File.WriteAllText(Path.Combine(output, "results.txt"), $"{checks.Count} passed\n" + ex);
            if (DisplayServer.GetName() != "headless") Capture(owner, output, "browse-failed.png");
            owner.GetTree().Quit(1);
        }
    }

    private static async Task CheckFilters(MainShell owner, WorkspaceApi api, ConsoleCommands commands, string output, Action<bool, string> check)
    {
        var clips = new[] { ("Browse_Fast", 2f, (double?)60, "browse-rig"), ("Browse_Unknown", 2f, (double?)null, "browse-rig"),
            ("Browse_Long", 4f, (double?)60, "browse-rig"), ("Browse_Fast_Second", 3f, (double?)60, "browse-rig"),
            ("Browse_Other_Skeleton", 2f, (double?)60, "other-rig") };
        var paths = clips.Select(clip => CreateClip(output, clip.Item1, clip.Item2, clip.Item3, clip.Item4)).ToArray();
        var fbxPath = RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx");
        var imported = await api.ImportAsync([.. paths, fbxPath]);
        check(imported.Errors.Count == 0 && imported.Added == paths.Length + 1, "fixture GLBs and the Epic FBX are imported");

        var fast = api.Find("Browse_Fast");
        var second = api.Find("Browse_Fast_Second");
        var unknown = api.Find("Browse_Unknown");
        var parsed = await api.GetAssetAttributesAsync(fast);
        check(parsed.Duration == 2 && parsed.SourceFps == 60 && parsed.Skeletons.Single().Id == "browse-rig", "GLB input times and declared source metadata are parsed");
        var missingRate = await api.GetAssetAttributesAsync(unknown);
        check(missingRate.SourceFps == null && missingRate.Duration == 2, "a known duration does not substitute for a missing source rate");
        var fbx = await api.GetAssetAttributesAsync(api.Find("UE5_Manny_Idle"));
        check(fbx.Duration is > 7 and < 8 && fbx.SourceFps == 30 && fbx.Skeletons.Length == 1, "binary FBX take timing, bone hierarchy and explicit time mode are parsed");
        var staticFbx = AssetAttributeReader.Read(RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny.fbx"), ".fbx", CancellationToken.None);
        check(staticFbx.Skeletons.Length == 1 && staticFbx.Duration == null && staticFbx.SourceFps == null,
            "an empty FBX take leaves animation duration and source FPS unknown");
        var skin = CheckJointSets(output);
        check(skin.Skeletons.Length == 1 && skin.Skeletons[0].Name.Contains("3 骨骼"), "overlapping glTF skins describe a shared skeleton");

        var filter = AssetAttributeFilter.Parse("browse-rig", "2..3", "60");
        var page0 = await api.BrowseAssetsAsync("Browse", "All", filter, pageSize: 1);
        var page1 = await api.BrowseAssetsAsync("Browse", "All", filter, page: 1, pageSize: 1);
        check(page0.Total == 2 && page1.Total == 2 && new[] { page0.Items.Single().Asset.Id, page1.Items.Single().Asset.Id }.ToHashSet().SetEquals([fast.Id, second.Id]),
            "combined attribute filters operate across all matches before pagination");
        var unknownPage = await api.BrowseAssetsAsync("Browse", "All", AssetAttributeFilter.Parse("browse-rig", "..2", "unknown"));
        check(unknownPage.Total == 1 && unknownPage.Items.Single().Asset.Id == unknown.Id, "unknown FPS is searchable independently of numeric FPS");

        api.SetFavorites([fast.Id], true);
        var collection = api.CreateCollection("P1 browse checks");
        api.SetCollectionMembership(collection.Id, [fast.Id, second.Id, unknown.Id], true);
        api.SetTrash([second.Id], true);
        var favorites = await api.GetMatchingAssetsAsync("Browse", "Favorites", filter);
        var members = await api.GetMatchingAssetsAsync("Browse", collection.Id, filter);
        var trash = await api.GetMatchingAssetsAsync("Browse", "Trash", filter);
        check(favorites.Select(asset => asset.Id).SequenceEqual([fast.Id]) && members.Select(asset => asset.Id).SequenceEqual([fast.Id]) &&
            trash.Select(asset => asset.Id).SequenceEqual([second.Id]), "favorites, collections and trash compose with attribute filters");

        var console = owner.GetNode("/root/LimboConsole");
        console.Call("execute_command", "asset filter Browse All browse-rig 2..3 60 0");
        await commands.PendingTask;
        using var consoleResult = JsonDocument.Parse(commands.LastResult);
        check(consoleResult.RootElement.GetProperty("Total").GetInt32() == 1 &&
            consoleResult.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString() == fast.Id, "Limbo applies the shared filter rules");

        var derived = Directory.GetFiles(api.Storage.Resolve("preview-cache/v1/attributes"), "*.json");
        check(derived.Length >= paths.Length && derived.All(file => AssetBrowseCache.IsCacheFile(Path.GetRelativePath(api.Storage.Root, file))),
            "parsed attributes are stored as library-local derived files");
        foreach (var file in derived) File.WriteAllText(file, "{corrupt}");
        api.InvalidateAssetBrowseCache();
        check((await api.GetAssetAttributesAsync(fast)).SourceFps == 60, "corrupt attribute cache data is regenerated");

        var fastPath = api.Storage.Resolve(fast.VirtualPath);
        var original = File.ReadAllBytes(fastPath);
        var originalRequest = api.PrepareAssetThumbnail(fast);
        try
        {
            var (json, buffer) = MotionGlb.Unpack(original);
            json["extras"]!["animanagerSourceFps"] = 24;
            File.WriteAllBytes(fastPath, MotionGlb.Pack(json, buffer));
            File.SetLastWriteTimeUtc(fastPath, DateTime.UtcNow.AddSeconds(2));
            check((await api.GetAssetAttributesAsync(fast)).SourceFps == 24 && api.PrepareAssetThumbnail(fast) != originalRequest,
                "a changed source invalidates both derived metadata and the thumbnail key");
        }
        finally { File.WriteAllBytes(fastPath, original); }

        using var image = Image.CreateEmpty(AssetBrowseCache.ThumbnailWidth, AssetBrowseCache.ThumbnailHeight, false, Image.Format.Rgb8);
        image.Fill(Colors.RoyalBlue);
        var request = api.PrepareAssetThumbnail(fast);
        api.SaveAssetThumbnail(request, [0, 1, 2]);
        check(api.ReadAssetThumbnail(request) == null, "invalid thumbnail headers are treated as cache misses");
        api.SaveAssetThumbnail(request, image.SavePngToBuffer());
        check(api.ReadAssetThumbnail(request) != null, "valid PNG thumbnails are reusable from disk");
        var unrelated = api.Storage.Resolve("preview-cache/user-note.txt");
        File.WriteAllText(unrelated, "retain");
        var maintenance = new LibraryMaintenance(api);
        var backup = Path.Combine(Path.GetDirectoryName(api.Storage.Root)!, "asset-browse-backup-" + Guid.NewGuid().ToString("N"));
        await maintenance.BackupAsync(backup);
        check(!File.Exists(Path.Combine(backup, request.CachePath)) && File.Exists(Path.Combine(backup, "preview-cache/user-note.txt")),
            "backup excludes owned derived files and retains other files");
        var plan = await maintenance.PreviewCacheAsync();
        await maintenance.ClearCacheAsync(plan);
        check(plan.PreviewDiskBytes > 0 && !File.Exists(api.Storage.Resolve(request.CachePath)) && File.Exists(unrelated) &&
            !Directory.EnumerateFiles(api.Storage.Resolve("preview-cache/v1/attributes"), "*.json").Any(), "cache cleanup removes only owned preview files");
        check((await api.GetAssetAttributesAsync(fast)).SourceFps == 60 && Directory.EnumerateFiles(api.Storage.Resolve("preview-cache/v1/attributes"), "*.json").Any(),
            "attribute cache can be rebuilt after clearing it");

        // Exercise the controls and deferred list updates on the same path as the app.
        var toggle = Control<Button>(owner, "AssetAttributeFilters");
        var skeleton = Control<OptionButton>(owner, "AssetSkeletonFilter");
        var duration = Control<LineEdit>(owner, "AssetDurationFilter");
        var fps = Control<LineEdit>(owner, "AssetFpsFilter");
        var apply = Control<Button>(owner, "AssetApplyFilters");
        var clear = Control<Button>(owner, "AssetClearFilters");
        var rows = Control<ItemList>(owner, "AssetRows");
        var search = Control<LineEdit>(owner, "AssetSearch");
        search.Text = "Browse";
        commands.Load(fast.Id);
        await commands.PendingTask;
        check(owner.SelectedAssetIds.SequenceEqual([fast.Id]) && rows.ItemCount == 4, "the app selects an asset and shows the unfiltered search results");
        toggle.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(owner, () => skeleton.ItemCount > 3);
        skeleton.Select(Enumerable.Range(0, skeleton.ItemCount).Single(i => skeleton.GetItemText(i) == "browse-rig"));
        duration.Text = "2..3";
        fps.Text = "60";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        fps.Text = "未知";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(owner, () => rows.ItemCount == 1 && rows.GetItemText(0).Contains(unknown.Name));
        check(owner.SelectedAssetIds.SequenceEqual([fast.Id]) && rows.GetSelectedItems().Length == 0,
            "latest UI attribute filter wins and preserves a selection outside the result");
        clear.EmitSignal(BaseButton.SignalName.Pressed);
        check(rows.ItemCount == 4 && rows.GetSelectedItems().Length == 1 && owner.SelectedAssetIds.SequenceEqual([fast.Id]),
            "clearing UI attributes restores the search rows and their selected state");

        var alternate = Path.Combine(Path.GetDirectoryName(api.Storage.Root)!, "asset-browse-switch-" + Guid.NewGuid().ToString("N"));
        var entry = owner.Libraries.Create(alternate, "Browse alternate library");
        fps.Text = "60";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        commands.AssetFilter("Browse", "All", "*", "*", "60", 0);
        var pending = commands.PendingTask;
        commands.LibraryOpen(entry.Id);
        await pending;
        for (int i = 0; i < 6; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        check(owner.Libraries.Active?.Identity.Id == entry.Id && rows.ItemCount == 0 && fps.Text.Length == 0 && owner.SelectedAssetIds.Length == 0,
            "switching libraries cancels older browse results and resets their attribute controls");
    }

    private static T Control<T>(MainShell owner, string nodeName) where T : Godot.Control => (T)owner.FindChild(nodeName, true, false)!;

    private static AssetAttributes CheckJointSets(string output)
    {
        var path = Path.Combine(output, "overlapping-skins.gltf");
        File.WriteAllText(path, """
            {"asset":{"version":"2.0"},"nodes":[{"name":"root","children":[1]},{"name":"spine","children":[2]},{"name":"head"}],
             "skins":[{"joints":[0,1]},{"joints":[0,1,2]}]}
            """);
        return AssetAttributeReader.Read(path, ".gltf", CancellationToken.None);
    }

    private static string CreateClip(string output, string name, float seconds, double? fps, string skeleton)
    {
        var path = Path.Combine(output, name + ".glb");
        var (json, buffer) = MotionGlb.Unpack(MotionGlb.Write([new MotionBone { Name = "root" }],
            [new MotionChannel(0, "translation", [0, seconds], [[0, 0, 0], [1, 0, 0]])], name, 60, zUpParent: false));
        if (fps is { } rate) json["extras"]!["animanagerSourceFps"] = rate;
        else json["extras"]!.AsObject().Remove("animanagerSourceFps");
        json["extras"]!["skeletonProfile"] = skeleton;
        File.WriteAllBytes(path, MotionGlb.Pack(json, buffer));
        return path;
    }

    private static async Task CheckRendering(MainShell owner, WorkspaceApi api, ConsoleCommands commands, string output, Action<bool, string> check)
    {
        check(DisplayServer.GetName() != "headless", "thumbnail test is running with a renderer");
        owner.GetWindow().Mode = Window.ModeEnum.Windowed;
        owner.GetWindow().Size = new(1440, 960);
        owner.DisplayScale.SetScale(1);
        owner.Workspace.UsePreset("browse");
        owner.Workspace.ShowPanel("assets");
        var list = Control<ItemList>(owner, "AssetRows");
        await WaitUntil(owner, () => list.IsVisibleInTree() && list.Size.Y > 80);
        var candidates = api.Database.SearchAll();
        var asset = candidates.FirstOrDefault(a => a.SourceFormat == ".motion");
        if (asset == null)
        {
            foreach (var candidate in candidates.Where(a => a.SourceFormat is ".fbx" or ".glb" or ".gltf")
                .OrderBy(a => a.Type != "Animation"))
            {
                var metadata = await api.GetAssetAttributesAsync(candidate);
                if (metadata.Duration is > 0 && metadata.Skeletons.Length > 0) { asset = candidate; break; }
            }
        }
        if (asset == null) throw new InvalidOperationException("The isolated render library needs a rigged animation.");
        var request = api.PrepareAssetThumbnail(asset);
        api.SaveAssetThumbnail(request, [0, 1, 2]);
        var search = (LineEdit)owner.FindChild("AssetSearch", true, false)!;
        search.Text = asset.Name;
        commands.Load(asset.Id);
        await commands.PendingTask;
        var preview = Preview3DViewport.Descendants(owner).OfType<Preview3DViewport>().Single();
        var previewModel = preview.Model!.GetInstanceId();
        double playhead = Math.Min(.2, preview.Duration * .5);
        preview.Seek(playhead);
        preview.SetSpeed(2);
        await WaitUntil(owner, () => api.ReadAssetThumbnail(request)?.Length > 64);
        using var saved = new Image();
        check(saved.LoadPngFromBuffer(api.ReadAssetThumbnail(request)!) == Error.Ok && HasSubject(saved),
            "visible asset row generates a thumbnail with a rendered subject and replaces a corrupt cache file");
        var row = Enumerable.Range(0, list.ItemCount).First(i => list.GetItemText(i).Contains(asset.Name));
        using var assigned = list.GetItemIcon(row).GetImage();
        check(HasSubject(assigned), "the rendered thumbnail appears on the asset row");
        check(preview.Model?.GetInstanceId() == previewModel && Math.Abs(preview.Playhead - playhead) < .01 &&
            Math.Abs(preview.Player!.SpeedScale - 2) < .001, "rendering thumbnails preserves the active preview model, playback speed and playhead");
        var attributes = await api.GetAssetAttributesAsync(asset);
        check(attributes.Duration is > 0 && attributes.Skeletons.Length > 0 && attributes.SourceFps != 2,
            "real asset attributes reflect the source independently of preview speed");
        await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Capture(owner, output, "browse.png");
        Control<Button>(owner, "AssetAttributeFilters").EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(owner, () => Control<OptionButton>(owner, "AssetSkeletonFilter").ItemCount > 2);
        await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Capture(owner, output, "browse-filters.png");
        owner.Workspace.ShowPanel("export");
        await WaitUntil(owner, () => owner.Workspace.IsPanelVisible("export"));
        await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Capture(owner, output, "export-panel.png");
    }

    private static void Capture(MainShell owner, string output, string name)
    {
        using var screenshot = owner.GetViewport().GetTexture().GetImage();
        if (screenshot is { } && !screenshot.IsEmpty()) screenshot.SavePng(Path.Combine(output, name));
    }

    private static bool HasSubject(Image image)
    {
        var background = image.GetPixel(0, 0);
        int changed = 0;
        for (int y = 1; y < image.GetHeight(); y++)
            for (int x = 1; x < image.GetWidth(); x++)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) > .12f && ++changed > 50)
                    return true;
            }
        return false;
    }

    private static async Task WaitUntil(MainShell owner, Func<bool> predicate)
    {
        var deadline = Time.GetTicksMsec() + 15000;
        while (!predicate())
        {
            if (Time.GetTicksMsec() >= deadline) throw new TimeoutException("Timed out waiting for the asset browser.");
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
