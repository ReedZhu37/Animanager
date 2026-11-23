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
        const string longAssetName = "Browse_Fast_Animations__Female_TwinKatana__Attack__Root__Female_TwinKatana_SP_Attack05_Root";
        var clips = new[] { (longAssetName, 2f, (double?)60, "browse-rig"), ("Browse_Unknown", 2f, (double?)null, "browse-rig"),
            ("Browse_Long", 4f, (double?)60, "browse-rig"), ("Browse_Fast_Second", 3f, (double?)60, "browse-rig"),
            ("Browse_Other_Skeleton", 2f, (double?)60, "other-rig") };
        var paths = clips.Select(clip => CreateClip(output, clip.Item1, clip.Item2, clip.Item3, clip.Item4)).ToArray();
        var fbxPath = RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx");
        var imported = await api.ImportAsync([.. paths, fbxPath]);
        check(imported.Errors.Count == 0 && imported.Added == paths.Length + 1, "fixture GLBs and the Epic FBX are imported");

        var fast = api.Find(longAssetName);
        var second = api.Find("Browse_Fast_Second");
        var unknown = api.Find("Browse_Unknown");
        var timings = api.Database.ReadDurations();
        check(timings.Count == paths.Length + 1 && timings[fast.FileHash].Seconds == 2,
            "imports persist timing in the library index");
        await CheckDurationSnapshots(api, output, check);
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
        var durationSort = await api.BrowseAssetsAsync("Browse", "All", new(), new AssetSort(AssetSortField.Duration), pageSize: 1);
        check(durationSort.Items.Single().Asset.Name == "Browse_Long" && durationSort.Total == 5,
            "duration sorting orders all matches before limiting the page");
        foreach (bool descending in new[] { false, true })
        {
            var sorted = await api.BrowseAssetsAsync("Browse", "All", new(), new AssetSort(AssetSortField.Fps, descending));
            check(sorted.Items.Last().Asset.Id == unknown.Id, "unknown FPS sorts last in direction " + descending);
        }
        var metadataPage = await api.BrowseAssetsAsync("Browse", "All", new());
        check(metadataPage.Items.All(r => r.Attributes != null), "unfiltered browsing supplies card metadata");
        var byName = await api.BrowseAssetsAsync("Browse", "All", new(), new AssetSort(AssetSortField.Name, false));
        check(byName.Items.Select(r => r.Asset.Name).SequenceEqual(clips.Select(c => c.Item1).Order(StringComparer.Ordinal)),
            "database sorting uses the requested name order");

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
        var tagMode = Control<OptionButton>(owner, "AssetTagMatchMode");
        var tagPicker = Control<MenuButton>(owner, "AssetTagPicker");
        var rows = Control<Animanager.UI.Shell.AssetBrowser>(owner, "AssetBrowser");
        var search = Control<LineEdit>(owner, "AssetSearch");
        search.Text = "Browse";
        commands.Load(fast.Id);
        await commands.PendingTask;
        await WaitUntil(owner,()=>rows.Count==4);
        check(owner.SelectedAssetIds.SequenceEqual([fast.Id]) && rows.Count == 4, "the app selects an asset and shows the unfiltered search results");
        api.SetTags(unknown.Id, "SET_WALK，SET_WALK");
        api.SetTags("Browse_Long", "中文标签");
        api.SetTags("Browse_Other_Skeleton", "set_walk_extra 中文标签扩展");
        api.SetTags("UE5_Manny_Idle", "set_walk");
        commands.Tags(fast.Id, "set_walk,中文标签");
        await owner.PendingBrowse;
        toggle.EmitSignal(BaseButton.SignalName.Pressed);
        var groupMenu = Control<MenuButton>(owner, "AssetTagGroupMenu");
        var groupDialog = (AcceptDialog)owner.FindChild("AssetTagGroupEditor", true, false)!;
        var groupName = Control<LineEdit>(owner, "AssetTagGroupName");
        var groupMembers = Control<Tree>(owner, "AssetTagGroupMembers");
        void OpenGroupEditor(int index)
        {
            groupMenu.ShowPopup();
            var menu = groupMenu.GetPopup();
            menu.Hide();
            menu.EmitSignal(PopupMenu.SignalName.IndexPressed, index);
        }
        TreeItem GroupMember(string name) => groupMembers.GetRoot().GetChildren().Single(item =>
            item.GetMetadata(0).AsString().Equals(name, StringComparison.OrdinalIgnoreCase));

        OpenGroupEditor(0);
        groupName.Text = "Name";
        GroupMember("set_walk").Select(0);
        var walkChecked = groupMembers.EditSelected();
        GroupMember("中文标签").Select(0);
        var chineseChecked = groupMembers.EditSelected();
        groupDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await owner.PendingBrowse;
        var reopened = new AssetDatabase(api.Storage.Resolve(AssetDatabase.IndexName), repairProjection: false);
        var savedGroup = owner.AppTags.Read().Groups.Single();
        check(walkChecked && chineseChecked && !groupDialog.Visible && savedGroup.Name == "Name" &&
            savedGroup.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["set_walk", "中文标签"]) &&
            reopened.Find(fast.Id)!.Tags == "set_walk,中文标签" && reopened.Snapshot().TagGroups.Length == 0,
            "creating a group saves its members in app settings without editing the asset library");

        OpenGroupEditor(2);
        check(groupDialog.Visible && groupName.Text == "Name" && GroupMember("set_walk").IsChecked(0) && GroupMember("中文标签").IsChecked(0),
            "editing a tag group restores its name and checked members");
        groupDialog.Hide();
        var navigation = owner.Workspace.Panel("navigation");
        var tagButtons = Preview3DViewport.Descendants(navigation).OfType<Button>().Where(b => b.Text == "中文标签 2").ToArray();
        check(tagButtons.Length == 2, "a grouped tag also appears in the built-in All group");
        var disclosure = Control<Button>(owner, "TagGroupDisclosure" + savedGroup.Id);
        disclosure.EmitSignal(BaseButton.SignalName.Pressed);
        check(tagButtons.Count(button => button.GetParent<Godot.Control>().Visible) == 1,
            "only the folded group's copy of a tag is hidden");
        disclosure.EmitSignal(BaseButton.SignalName.Pressed);
        var tagButton = tagButtons[0];
        tagButton.EmitSignal(BaseButton.SignalName.Pressed);
        await owner.PendingBrowse;
        check(search.Text == "Browse" && tagButton.ButtonPressed && rows.Count == 2 && rows.Rows.Any(row => row.Id == fast.Id),
            "sidebar tag toggles match whole tags and preserve the search query");

        toggle.EmitSignal(BaseButton.SignalName.Pressed);
        tagPicker.ShowPopup();
        var tagMenu = tagPicker.GetPopup();
        int TagIndex(string name) => Enumerable.Range(0, tagMenu.ItemCount).Single(index =>
            tagMenu.GetItemMetadata(index).AsString().Equals(name, StringComparison.OrdinalIgnoreCase));
        check(tagMenu.IsItemSeparator(0) && tagMenu.GetItemText(0) == "全部" &&
            TagIndex("set_walk") > 0 && TagIndex("中文标签") > 0,
            "the tag picker lists each tag once under All");
        var checkedTag = tagMenu.IsItemChecked(TagIndex("中文标签"));
        tagMenu.EmitSignal(PopupMenu.SignalName.IndexPressed, TagIndex("set_walk"));
        await owner.PendingBrowse;
        check(checkedTag && tagMenu.IsItemChecked(TagIndex("set_walk")) && rows.Count == 1 && rows.Row(0)!.Id == fast.Id,
            "intersection requires every selected tag and syncs selections across the sidebar and picker");
        tagMenu.Hide();

        Control<Button>(owner, "TagGroupUnion" + savedGroup.Id).EmitSignal(BaseButton.SignalName.Pressed);
        await owner.PendingBrowse;
        check(tagMode.Selected == 1 && rows.Count == 3 &&
            rows.Rows.Select(row => row.Name).ToHashSet().SetEquals([longAssetName, "Browse_Long", "Browse_Unknown"]),
            "one-click group filtering returns the union of its exact tags and composes with search");
        var anchor = rows.Selection.Anchor;
        rows.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);
        rows.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.Grid);
        check(rows.Selection.Anchor == anchor && rows.Selection.Contains(fast.Id), "grid/list toggles preserve selection and range anchor");
        await WaitUntil(owner, () => skeleton.ItemCount > 3);
        skeleton.Select(Enumerable.Range(0, skeleton.ItemCount).Single(i => skeleton.GetItemText(i) == "browse-rig"));
        duration.Text = "2..3";
        fps.Text = "60";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        fps.Text = "未知";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(owner, () => rows.Count == 1 && rows.Row(0)!.Name.Contains(unknown.Name));
        check(owner.SelectedAssetIds.SequenceEqual([fast.Id]) && rows.Rows.Count(r=>rows.Selection.Contains(r.Id)) == 0,
            "the latest attribute filter combines with tags and preserves a selection outside the result");
        clear.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(owner,()=>rows.Count==4);
        check(rows.Count == 4 && !tagButton.ButtonPressed && tagMode.Selected == 0 &&
            rows.Rows.Count(r=>rows.Selection.Contains(r.Id)) == 1 && owner.SelectedAssetIds.SequenceEqual([fast.Id]),
            "clearing both filters restores the search rows and their selected state");

        api.SetTags("Browse_Long", "");
        commands.Tags(fast.Id, "set_walk");
        await owner.PendingBrowse;
        tagPicker.ShowPopup();
        check(Preview3DViewport.Descendants(owner.Workspace.Panel("navigation")).OfType<Button>().Any(b => b.Text == "中文标签 0") &&
            tagMenu.GetItemText(TagIndex("中文标签")) == "中文标签 · 0",
            "removing a grouped tag from its last assets leaves it visible with a zero count in both tag lists");
        tagMenu.Hide();
        api.SetTags("Browse_Long", "中文标签");
        commands.Tags(fast.Id, "set_walk,中文标签");
        await owner.PendingBrowse;

        owner.AppTags.SaveGroup(null, "Action", ["SET_WALK"]);
        var groups = owner.AppTags.Read().Groups;
        var action = groups.Single(group => group.Name == "Action");
        check(groups.Single(group => group.Id == savedGroup.Id).Tags.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["set_walk", "中文标签"]) && action.Tags.SequenceEqual(["SET_WALK"]),
            "the same tag can belong to multiple groups");
        owner.AppTags.DeleteGroup(action.Id);
        check(owner.AppTags.Read().Groups.Length == 1 && api.Find(fast.Id).Tags == "set_walk,中文标签",
            "deleting a tag group leaves its asset tags available");

        Button SidebarGroupEdit(string name) => Preview3DViewport.Descendants(owner.Workspace.Panel("navigation"))
            .OfType<Button>().Single(button => button.TooltipText == $"编辑标签组「{name}」");
        SidebarGroupEdit("Name").EmitSignal(BaseButton.SignalName.Pressed);
        check(groupDialog.Visible && groupName.Text == "Name" && GroupMember("中文标签").IsChecked(0),
            "the sidebar edit button opens the saved group with its current members");
        groupName.Text = "Renamed";
        GroupMember("set_walk").Select(0);
        var uncheckedWalk = groupMembers.EditSelected();
        GroupMember("中文标签").Select(0);
        var uncheckedMember = groupMembers.EditSelected();
        groupDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await owner.PendingBrowse;
        var editedGroup = owner.AppTags.Read().Groups.Single();
        check(uncheckedWalk && uncheckedMember && editedGroup.Id == savedGroup.Id && editedGroup.Name == "Renamed" && editedGroup.Tags.Length == 0,
            "editing from the sidebar persists the renamed group and removed member under the same ID");
        SidebarGroupEdit("Renamed").EmitSignal(BaseButton.SignalName.Pressed);
        check(groupDialog.Visible && groupName.Text == "Renamed" && !GroupMember("中文标签").IsChecked(0),
            "a group with no members stays editable from the sidebar");
        groupDialog.Hide();

        void SubmitTag(string title, string name)
        {
            var dialog = owner.GetChildren().OfType<ConfirmationDialog>().Single(item => item.Visible && item.Title == title);
            Preview3DViewport.Descendants(dialog).OfType<LineEdit>().First().Text = name;
            dialog.Hide();
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        }

        const string customTag = "自定义标签";
        Control<Button>(owner, "CreateAppTag").EmitSignal(BaseButton.SignalName.Pressed);
        SubmitTag("新建标签", customTag);
        await owner.PendingBrowse;
        check(owner.AppTags.Read().Tags.Contains(customTag) && api.Find(fast.Id).Tags == "set_walk,中文标签" &&
            Preview3DViewport.Descendants(navigation).OfType<Button>().Any(button => button.Text == customTag + " 0"),
            "New Tag in the sidebar creates a reusable tag and shows it under All");
        var tagEditor = (AcceptDialog)owner.FindChild("AssetTagEditor", true, false)!;
        var tagInput = Control<LineEdit>(owner, "AssetTagEditorInput");
        var tagSuggestions = Control<ItemList>(owner, "AssetTagSuggestions");
        var window = owner.GetWindow();
        var originalSize = window.Size;
        var originalScale = owner.DisplayScale.RequestedScale;
        owner.AppTags.AddTags(Enumerable.Range(0, 48).Select(index => $"LayoutTag{index}"));
        owner.DisplayScale.SetScale(2);
        window.Size = new(1024, 700);
        rows.SelectIndex(rows.IndexOf(fast.Id));
        Control<Button>(owner, "AssetAddTag").EmitSignal(BaseButton.SignalName.Pressed);
        for (int frame = 0; frame < 4; frame++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        tagSuggestions.ForceUpdateListSize();
        var bounds = new Rect2(Vector2.Zero, (Vector2)tagEditor.Size / tagEditor.ContentScaleFactor);
        var scroll = tagSuggestions.GetVScrollBar();
        check(tagEditor.Visible && tagEditor.Size.X <= window.Size.X && tagEditor.Size.Y <= window.Size.Y &&
            bounds.Encloses(tagInput.GetGlobalRect()) && bounds.Encloses(tagSuggestions.GetGlobalRect()) &&
            bounds.Encloses(tagEditor.GetOkButton().GetGlobalRect()) && scroll.MaxValue > scroll.Page,
            "Add Tag fits on first opening at 200 percent with a long animation name and scrollable suggestions");
        tagEditor.Hide();
        owner.DisplayScale.SetScale(originalScale);
        window.Size = originalSize;

        rows.Selection.SetAll([fast.Id, unknown.Id]);
        owner.ShowAssetContextMenu(fast.Id);
        var context = (PopupMenu)owner.FindChild("AssetContextMenu", true, false)!;
        int addTag = Enumerable.Range(0, context.ItemCount).Single(index => context.GetItemText(index) == "添加标签…");
        check(context.Visible && !context.IsItemDisabled(addTag), "the asset context menu exposes Add Tag");
        context.Hide();
        context.EmitSignal(PopupMenu.SignalName.IdPressed, context.GetItemId(addTag));
        void TypeTags(string text)
        {
            tagInput.Text = text;
            tagInput.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        TypeTags("中文");
        check(tagEditor.Visible && Enumerable.Range(0, tagSuggestions.ItemCount).Any(index =>
            tagSuggestions.GetItemMetadata(index).AsString() == "中文标签" && tagSuggestions.GetItemText(index).Contains("2 个资产")),
            "tag suggestions filter as the user types and show how many open assets use each tag");
        TypeTags("自定义");
        check(tagSuggestions.ItemCount == 1 && tagSuggestions.GetItemMetadata(0).AsString() == customTag &&
            tagSuggestions.GetItemText(0).Contains("0 个资产"), "unused tags can be found and reused");
        tagSuggestions.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
        await WaitUntil(owner, () => tagInput.Text.Contains(customTag));
        check(tagEditor.GetOkButton().Text == "添加标签", "choosing an existing tag completes its name");
        tagEditor.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await owner.PendingBrowse;
        check(WorkspaceApi.ParseTags(api.Find(fast.Id).Tags).SequenceEqual(["set_walk", "中文标签", customTag]) &&
            WorkspaceApi.ParseTags(api.Find(unknown.Id).Tags).Contains(customTag),
            "committing a suggested tag adds it to all selected animations and keeps their existing tags");
        Control<Button>(owner, "AssetAddTag").EmitSignal(BaseButton.SignalName.Pressed);
        TypeTags("SET_WALK，属性标签");
        check(tagEditor.Visible && tagEditor.GetOkButton().Text == "新建并添加", "typing a new tag offers creation in the same dialog");
        tagEditor.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await owner.PendingBrowse;
        check(WorkspaceApi.ParseTags(api.Find(fast.Id).Tags).SequenceEqual(["set_walk", "中文标签", customTag, "属性标签"]) &&
            owner.AppTags.Read().Tags.Contains("属性标签") && !WorkspaceApi.ParseTags(api.Find(unknown.Id).Tags).Contains("属性标签"),
            "new tags become reusable, without duplicating an existing tag or editing other selected animations from the inspector");
        rows.SelectIndex(rows.IndexOf(fast.Id));

        var legacy = Path.Combine(output, "legacy-catalog.json");
        File.WriteAllText(legacy, "{\"version\":1,\"revision\":\"0123456789abcdef0123456789abcdef\",\"assets\":[],\"collections\":[]}");
        check(AssetDatabase.ReadCatalog(legacy).TagGroups.Length == 0, "catalogs written before tag groups still open");

        var alternate = Path.Combine(Path.GetDirectoryName(api.Storage.Root)!, "asset-browse-switch-" + Guid.NewGuid().ToString("N"));
        var entry = owner.Libraries.Create(alternate, "Browse alternate library");
        var legacyPath = Path.Combine(alternate, AssetDatabase.CatalogName);
        var legacyGroup = new AssetTagGroup(Guid.NewGuid().ToString("N"), "Combat", ["Attack"]);
        var parent = new AssetCollection(Guid.NewGuid().ToString("N"), "Legacy parent", []);
        var child = new AssetCollection(Guid.NewGuid().ToString("N"), "Legacy child", [])
        {
            ParentId = parent.Id,
            LinkedAssets = [new(owner.Libraries.Active!.Identity.Id, fast.Id)]
        };
        MotionJson.Atomic(legacyPath, AssetDatabase.ReadCatalog(legacyPath) with
        {
            TagGroups = [legacyGroup],
            Collections = [parent, child]
        });
        owner.AppCollections.ImportRegisteredLibraries([entry]);
        owner.AppCollections.ImportRegisteredLibraries([entry]);
        check(owner.AppCollections.Read().Collections.Single(collection => collection.Id == child.Id) is { } migratedChild &&
            migratedChild.ParentId == parent.Id && migratedChild.Members.SequenceEqual(child.LinkedAssets),
            "legacy collections migrate once before their source library opens and retain their hierarchy and links");
        var migrated = owner.AppTags.ImportRegisteredLibraries([entry]);
        check(migrated.Groups.Single(group => group.Name == "Combat").Tags.SequenceEqual(["Attack"]),
            "legacy tag groups migrate from registered libraries before those libraries are opened");
        fps.Text = "60";
        apply.EmitSignal(BaseButton.SignalName.Pressed);
        commands.AssetFilter("Browse", "All", "*", "*", "60", 0);
        var pending = commands.PendingTask;
        commands.LibraryOpen(entry.Id);
        await pending;
        await commands.PendingTask;
        check(owner.Libraries.Active?.Identity.Id == entry.Id && rows.Count == 0 && fps.Text.Length == 0 && owner.SelectedAssetIds.Length == 0,
            "switching libraries cancels older browse results and resets their attribute controls");
        var sidebarLabels = Preview3DViewport.Descendants(owner.Workspace.Panel("navigation")).OfType<Button>()
            .Select(button => button.Text).ToHashSet();
        check(sidebarLabels.Contains("Attack 0") && sidebarLabels.Contains("中文标签 0") &&
            owner.AppTags.Read().Groups.Any(group => group.Id == editedGroup.Id),
            "switching to an empty library retains app-wide groups and tags with zero counts");
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

    private static async Task CheckDurationSnapshots(WorkspaceApi working, string output, Action<bool, string> check)
    {
        LibrarySession Open(string name)
        {
            var storage = new LocalStorageProvider(Path.Combine(output, name));
            var database = new AssetDatabase(storage.Resolve(AssetDatabase.IndexName));
            return new(new(1, name, name), new(database, storage, working.Blender),
                File.Open(storage.Resolve("test.lock"), FileMode.OpenOrCreate));
        }
        using var first = Open("B");
        using var second = Open("A");
        var sources = new[] { first, second };
        var range = AssetAttributeFilter.Parse("", "2", "");
        var sort = new AssetSort(AssetSortField.Duration, true);
        foreach (var lib in sources)
        {
            var dir = lib.Api.Storage.Resolve("assets");
            Directory.CreateDirectory(dir);
            var path = CreateClip(dir, "Tie", 1.9999999f, 24, "rig");
            var asset = new Animanager.Core.Models.AssetItem { Id = "same", Name = "Tie", VirtualPath = "assets/Tie.glb",
                SourceFormat = ".glb", FileHash = MotionJson.Hash(File.ReadAllBytes(path)) };
            lib.Api.Database.Insert(asset); // Old catalog, no timing record.
            File.WriteAllText(Path.Combine(dir, "Static.gltf"), """{"asset":{"version":"2.0"}}""");
            lib.Api.Database.Insert(asset with { Id = "static", Name = "Static", VirtualPath = "assets/Static.gltf",
                SourceFormat = ".gltf", FileHash = "static" });
        }
        Task<LibraryAssetPage> Page(AssetAttributeFilter filter, int page, bool descending) =>
            WorkspaceApi.BrowseLibrariesAsync(sources, "", "All", filter, sort with { Descending = descending }, page, 1, default);
        Task<LibraryAssetPage> MixedPage(int page) => WorkspaceApi.BrowseLibrariesAsync(
            sources, "", "All", new(), new AssetSort(AssetSortField.LibraryMix), page, 1, default);
        var mixedFirst = await MixedPage(0);
        var mixedSecond = await MixedPage(1);
        mixedFirst.Items[0].Asset.Tags = "Changed by caller";
        check(mixedFirst.Total == 4 && mixedFirst.Items[0].Library == first && mixedSecond.Items[0].Library == second &&
            (await MixedPage(0)).Items[0].Asset.Tags.Length == 0,
            "paging a mixed result preserves order and callers cannot mutate cached rows");
        foreach (bool descending in new[] { false, true })
        {
            var top = await Page(range, 0, descending);
            var next = await Page(range, 1, descending);
            var unknown = await Page(new(), 2, descending);
            check(top.Total == 2 && top.Items.Single().Library == second &&
                next.Items.Single().Library == first && unknown.Items.Single().Attributes!.Duration == null,
                "cross-library duration ties, inclusive float boundaries and unknowns page correctly, descending=" + descending);
        }
        var legacy = new AssetDatabase(first.Api.Storage.Resolve(AssetDatabase.IndexName), repairProjection: false);
        var stored = legacy.ReadDurations();
        check(stored.Count == 2 && stored["static"].Seconds == null,
            "legacy backfill persists both measured and explicitly unknown durations across reopen");
        int revision = first.Api.DurationRevision.Files;
        CreateClip(first.Api.Storage.Resolve("assets"), "Tie", 8, 24, "rig");
        var reopened = new WorkspaceApi(legacy, first.Api.Storage, working.Blender);
        try
        {
            var updated = await reopened.BrowseAssetsAsync("", "All", AssetAttributeFilter.Parse("", "8", ""), sort);
            check(updated.Items.Single().Asset.Id == "same", "reopening verifies a changed source against the saved timing");
        }
        finally { reopened.Retire(); }
        await Task.Run(() => SpinWait.SpinUntil(() => first.Api.DurationRevision.Files != revision, TimeSpan.FromSeconds(3)));
        var after = await Page(range, 0, true);
        check(after.Total == 1 && after.Items.Single().Library == second,
            "a source edit evicts the combined duration snapshot and updates the working-library count");
        second.Api.SetTrash(["same"], true);
        check((await Page(range, 0, false)).Total == 0, "catalog edits in a mounted library invalidate a duration result");
        check((await MixedPage(0)).Total == 3, "catalog edits in a mounted library invalidate the ordinary paged result");
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
        owner.Workspace.RestorePanels();
        owner.Workspace.ShowPanel("assets");
        var list = Control<Animanager.UI.Shell.AssetBrowser>(owner, "AssetBrowser");
        await WaitUntil(owner, () => list.IsVisibleInTree() && list.Size.Y > 80);
        var candidates = api.Database.SearchAll();
        if (candidates.Count == 0)
        {
            await api.ImportAsync([RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Walk.glb")]);
            candidates = api.Database.SearchAll();
        }
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
        var preview = ((Animanager.UI.Shell.PreviewStage)owner.Workspace.Panel("preview")).Viewport;
        var previewModel = preview.Model!.GetInstanceId();
        double playhead = Math.Min(.2, preview.Duration * .5);
        preview.Seek(playhead);
        preview.SetSpeed(2);
        await WaitUntil(owner, () => api.ReadAssetThumbnail(request)?.Length > 64);
        using var saved = new Image();
        check(saved.LoadPngFromBuffer(api.ReadAssetThumbnail(request)!) == Error.Ok && HasSubject(saved),
            "visible asset row generates a thumbnail with a rendered subject and replaces a corrupt cache file");
        var row = Enumerable.Range(0, list.Count).First(i => list.Row(i)!.Name.Contains(asset.Name));
        var texture = list.Row(row)!.Thumbnail!;
        using var assigned = texture.GetImage();
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

        var warmupLibrary = owner.Libraries.Create(Path.Combine(output, "thumbnail-warmup"), "Thumbnail warmup");
        var warmupSource = RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Run.glb");
        var relative = "assets/StudioBot_Run.glb";
        string warmupFile = Path.Combine(warmupLibrary.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(warmupFile)!);
        File.Copy(warmupSource, warmupFile);
        var hiddenAsset = new Core.Models.AssetItem
        {
            Id = Guid.NewGuid().ToString("N"), Name = "StudioBot_Run", VirtualPath = relative, SourceFormat = ".glb",
            Type = "Animation", FileHash = MotionJson.Hash(File.ReadAllBytes(warmupFile)), Size = new FileInfo(warmupFile).Length
        };
        var database = new AssetDatabase(Path.Combine(warmupLibrary.Path, AssetDatabase.IndexName));
        check(database.Insert(hiddenAsset), "warmup test has a previously uncached animation");
        var mounted = await owner.Libraries.OpenAsync(warmupLibrary.Path, browse: true);
        search.Text = "NoMatchingAnimation";
        search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
        await WaitUntil(owner, () => list.Count == 0);
        var warmupRequest = mounted.Api.PrepareAssetThumbnail(hiddenAsset);
        await WaitUntil(owner, () => mounted.Api.ReadAssetThumbnail(warmupRequest) != null);
        check(list.Count == 0, "opening a library preloads thumbnails for assets outside the displayed results");
        search.Text = asset.Name;
        search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
        await WaitUntil(owner, () => list.Rows.Any(card => card.Name.Contains(asset.Name) && card.Thumbnail != null));
        check(list.Rows.First(card => card.Name.Contains(asset.Name)).Thumbnail == texture,
            "returning to a result reuses its existing thumbnail texture");
        api.InvalidateAssetBrowseCache();
        search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
        await owner.PendingBrowse;
        await WaitUntil(owner, () => list.Rows.Any(card => card.Name.Contains(asset.Name) && card.Thumbnail != null));
        check(list.Rows.First(card => card.Name.Contains(asset.Name)).Thumbnail != texture,
            "invalidating a library cache drops its previous thumbnail textures from use");
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
