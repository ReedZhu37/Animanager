using Animanager.Core;
using Animanager.Core.Models;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Godot;
using Animanager.UI.Shell;
using Animanager.UI.Workspace;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private LibraryManager _libraries = null!;
    private AcceptDialog _libraryDialog = null!;
    private Tree _libraryRows = null!;
    private MenuButton _openLibrariesMenu = null!;
    private Label _openLibraryCount = null!, _emptyLibraries = null!;
    private Label _libraryLoadStatus = null!;
    private bool _libraryLoading;
    private Button _toggleBrowseLibrary = null!;
    private ConfirmationDialog _entryDialog = null!, _approvalDialog = null!;
    private LineEdit _entryFirst = null!, _entrySecond = null!;
    private Label _entryFirstLabel = null!, _entrySecondLabel = null!;
    private Button _entryBrowse = null!;
    private FileDialog _entryFolder = null!;
    private RichTextLabel _approvalText = null!;
    private Action? _approved;
    private Action<string, string>? _submitEntry;
    private LibraryEntry[] _entries = [];
    private string _libraryRegistryError = "";
    private Task _libraryUiTask = Task.CompletedTask;
    private readonly HashSet<string> _unavailableLibraryIds = new(StringComparer.Ordinal);

    private AssetSelection _assetSelection => _browser.Selection;
    private int _browseLibraryRevision;
    private int _visibleLibraryRevision = -1;
    private string? _assetAnchor { get=>_assetSelection.Anchor; set=>_assetSelection.SetAnchor(value); }
    private MenuButton _assetActions = null!;
    private PopupMenu _assetContextMenu = null!;

    private const int RestoreAssetsAction = 2, PurgeAssetsAction = 3, TrashAssetsAction = 9, CompareAssetsAction = 13, LocateAssetAction = 14;
    private const int AddAssetTagsAction = 5, RemoveAssetTagsAction = 6;
    private const int RemoveCurrentCollectionMembersAction = 15;
    private const int OpenCollectionSourceAction = 16, RemoveClosedCollectionMemberAction = 17;
    private const int OpenInCurrentPreviewAction = 18, OpenInNewPreviewAction = 19;
    private string? _assetContextKey;
    private const string TrashAssetsLabel = "删除（移入回收站）…";
    private const string RemoveCurrentCollectionMembersLabel = "从当前集合移出…";

    internal LibraryManager Libraries => _libraries;
    internal Task PendingLibraryOperation => _libraryUiTask;
    internal string[] SelectedAssetIds => _assetSelection.ToArray();

    private const string LibraryKeyPrefix = "library:";
    private LibrarySession? _plainAssetKeyLibrary;

    private string AssetKey(LibrarySession library, string id) => library == _plainAssetKeyLibrary && !id.StartsWith(LibraryKeyPrefix, StringComparison.Ordinal)
        ? id : LibraryKeyPrefix + library.Identity.Id + ":" + id;

    private LibrarySession? SelectionLibrary(string key)
    {
        if (!key.StartsWith(LibraryKeyPrefix, StringComparison.Ordinal)) return _plainAssetKeyLibrary;
        int end = key.IndexOf(':', LibraryKeyPrefix.Length);
        if (end < 0) return null;
        var id = key[LibraryKeyPrefix.Length..end];
        return _libraries.FindOpenLibrary(id);
    }

    private static string SelectionAssetId(string key) => key.StartsWith(LibraryKeyPrefix, StringComparison.Ordinal)
        ? key[(key.IndexOf(':', LibraryKeyPrefix.Length) + 1)..] : key;

    private LibraryAssetRow? FindLiveAsset(string key)
    {
        var library = SelectionLibrary(key);
        var id = SelectionAssetId(key);
        return library?.Api.Database.Find(id) is { Trashed: false } asset && asset.Id == id ? new(library, new(asset, null)) : null;
    }

    private bool CanEditAssetSelection() => !_libraryLoading && _assetSelection.Count > 0 &&
        _assetSelection.All(key => SelectionLibrary(key) != null);

    private LibrarySession? DragSourceLibrary(string key) => !_closing && !_libraryLoading && _category != "Trash" &&
        (_browser.Row(_browser.IndexOf(key)) is { Trashed: false } || _assetSelection.Contains(key)) ? SelectionLibrary(key) : null;

    private (LibrarySession Library, string[] Ids)[] DraggedAssetGroups(string[] keys) =>
        keys.Distinct().GroupBy(DragSourceLibrary).Where(group => group.Key != null)
            .Select(group => (Library: group.Key!, Ids: group.Key!.Api.Database.ExistingIds(group.Select(SelectionAssetId), trash: false)))
            .Where(group => group.Ids.Length > 0).ToArray();

    private void DropAssetsOnFavorites(string[] keys)
    {
        var groups = DraggedAssetGroups(keys);
        if (groups.Length == 0) return;
        ApplyAssetEdit(groups, (api, ids) => api.SetFavorites(ids, true));
        SetStatus($"已将 {groups.Sum(group => group.Ids.Length)} 个资产加入「我的收藏」");
    }

    private LibrarySession? ScopedLibrary => _assetLibraryFilter is { } id
        ? _libraries.FindOpenLibrary(id) : null;

    private async Task<(bool Bootstrap, string Error)> OpenStartupLibraryAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        var explicitRoot = RuntimePaths.ExplicitLibraryRoot();
        var defaultRoot = RuntimePaths.DefaultLibraryRoot();
        var uniqueTests = args.Any(a => a is "--shell-visual-test" or "--pipeline-test" or "--batch-test" or "--interface-test" or "--smoke-test"
            or "--bone-test" or "--bone-ui-test" or "--multi-bone-test" or "--multi-bone-ui-test" or "--library-p0-test"
            or "--export-options-test" or "--asset-browse-test" or "--retarget-test" or "--character-test" or "--third-feedback-test" or "--motion-similarity-test");
        var probe = args.Any(a => a.StartsWith("--retarget-probe=") || a.StartsWith("--retarget-demo="));
        var test = uniqueTests || probe || args.Any(a => a.StartsWith("--") && (a.Contains("-test") || a.Contains("-render") || a.StartsWith("--cue-")));
        var registry = test
            ? Path.Combine(Path.GetTempPath(), "Animanager-test-registries", Guid.NewGuid().ToString("N"), "libraries.json")
            : ProjectSettings.GlobalizePath("user://libraries.json");
        _preferencesPath = Path.Combine(Path.GetDirectoryName(registry)!, "workspace.json");
        _libraries = new(registry, new BlenderPipeline(RuntimePaths.Bundled("BuiltinAssets/Scripts/blender_convert.py")));
        if ((args.Contains("--retarget-test") || probe) && explicitRoot == null)
            explicitRoot = RuntimePaths.Bundled(".runtime/retarget-test-library");
        if (uniqueTests || probe) explicitRoot = Path.Combine(explicitRoot ?? defaultRoot, Guid.NewGuid().ToString("N"));
        var preferences = WorkspacePreferences.Load(_preferencesPath, out _);
        LibraryStartup startup;
        if (explicitRoot == null && preferences.OpenLibraryIds is { } ids)
        {
            var errors = new List<string>();
            if (ids.Length > 0)
            {
                try
                {
                    var entries = _libraries.Entries().ToDictionary(entry => entry.Id);
                    foreach (var id in ids)
                    {
                        if (_closing) break;
                        if (!entries.TryGetValue(id, out var entry)) continue;
                        try { await _libraries.OpenAsync(entry.Path, browse: true); }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            _unavailableLibraryIds.Add(id);
                            errors.Add($"无法打开 {entry.Name} · {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex.Message); }
            }
            startup = new(_libraries.Active, false, string.Join("\n", errors));
        }
        else startup = await _libraries.OpenStartupAsync(explicitRoot, defaultRoot, ProjectSettings.GlobalizePath("user://Library"));
        if (_closing) return (false, "");
        if (startup.Session is { } session)
        {
            _plainAssetKeyLibrary = session;
            _api = session.Api;
            _dataRoot = session.Root;
            GD.Print($"Asset library: {_dataRoot}");
        }
        var settingsDirectory = Path.GetDirectoryName(registry)!;
        AppCollections = new(Path.Combine(settingsDirectory, "collections.json"));
        try { await Task.Run(() => AppCollections.ImportRegisteredLibraries(_libraries.Entries())); }
        catch (Exception error) when (IsAppSettingsError(error)) { GD.PushWarning("无法迁移应用集合：" + error.Message); }
        await InitializeAppTagsAsync(settingsDirectory);
        return (startup.Bootstrap, startup.Message);
    }

    private void BeforeLibrarySwitch()
    {
        EndAnimationComparison();
        _retargetCancellation?.Cancel();
        StopRequest(ref _retargetSourceRequest);
        if (_closing) throw new InvalidOperationException("应用正在关闭。");
    }

    private Task SwitchLibraryAsync(Func<Task<LibrarySession>> open) => RunLibraryLoadAsync("正在打开资产库…", async () =>
    {
        BeforeLibrarySwitch();
        await Task.WhenAll(CancelPreviewLoads(), PendingRetargetPreview, PendingAnimationComparison, _pendingRetargetSource, StopLibraryPreviewWarmupAsync());
        if (_closing) return;
        var session = await open();
        if (_closing) return;
        ActivateLibrary(session);
        await PendingBrowse;
    });

    internal void ActivateLibrary(LibrarySession session)
    {
        if (_closing) return;
        if (_api == session.Api)
        {
            QueueLibraryPreviewWarmup(session);
            _libraryDialog.Hide();
            SetStatus("资产库已打开 · " + session.Identity.Name);
            return;
        }
        CancelAssetBrowse(resetFilters: true);
        while (_jobUpdates.TryDequeue(out _)) { }
        while (_progress.TryDequeue(out _)) { }
        _jobIds.Clear();
        _jobsList.Clear();
        _jobDetails.Text = "";
        _lastExport = "";
        ClearAssetSelection();
        _collectionBrowseReturn = null;
        _category = "All";
        _assetLibraryFilter = null;
        _page = 0;
        _browser.Scroll.ScrollVertical = 0;
        _searchTimer.Stop();
        _search.Text = "";
        _assetSearch.Text = "";
        _assetContextMenu.Hide();
        foreach (var stage in _previewTabs.Stages) stage.Viewport.EndAnimationComparison();
        _plainAssetKeyLibrary = session;
        SyncConsoleLibrary();
        RefreshPreviewTabs();
        SyncFocusedPreviewSelection();
        _browseLibraryRevision++;
        RememberOpenLibraries();
        LoadAssetRetargetConfiguration();
        SyncLibraryJobs();
        _workspace.CloseDrawer();
        _libraryDialog.Hide();
        Refresh();
        RefreshJobs();
        QueueLibraryPreviewWarmup(session);
        SetStatus("已打开 " + session.Identity.Name + " · " + session.Root);
    }

    private void SyncConsoleLibrary()
    {
        var session = _libraries.Active;
        if (_api == session?.Api) return;
        _api = session?.Api!;
        _dataRoot = session?.Root ?? "";
        _commands.ChangeLibrary(_api);
    }

    private void ChooseOpenLibrary(string title, Action<LibrarySession> use)
    {
        var libraries = LoadedLibraries();
        if (libraries.Length == 0) { ShowLibraryDialog(); return; }
        var scoped = ScopedLibrary;
        if (scoped != null || libraries.Length == 1)
        {
            use(scoped ?? libraries[0]);
            return;
        }

        var dialog = new ConfirmationDialog { Title = title, OkButtonText = "确认", CancelButtonText = "取消" };
        var choices = new OptionButton { FitToLongestItem = false };
        foreach (var library in libraries)
        {
            choices.AddItem(library.Identity.Name);
            choices.SetItemTooltip(choices.ItemCount - 1, library.Root);
        }
        dialog.AddChild(choices);
        dialog.Confirmed += () =>
        {
            var chosen = libraries[choices.Selected];
            if (_libraries.FindOpenLibrary(chosen.Identity.Id) == chosen) LibraryAction(() => use(chosen));
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        PopupFit(dialog, new(420, 145));
    }

    private void BuildLibraryDialogs()
    {
        BuildLibraryManagerDialog();
        BuildLibraryTagDialogs();

        _entryDialog = new ConfirmationDialog { OkButtonText = "确认", CancelButtonText = "取消" };
        AddChild(_entryDialog);
        var fields = Column(_entryDialog);
        _entryFirstLabel = Wrapped(""); fields.AddChild(_entryFirstLabel);
        var firstRow = Row(fields);
        _entryFirst = Input(""); firstRow.AddChild(_entryFirst);
        _entryBrowse = Button("浏览…", () => _entryFolder.PopupCenteredRatio(.7f));
        firstRow.AddChild(_entryBrowse);
        _entrySecondLabel = Wrapped(""); fields.AddChild(_entrySecondLabel);
        _entrySecond = Input(""); fields.AddChild(_entrySecond);
        _entryFolder = Dialog(FileDialog.FileModeEnum.OpenDir, "选择目录");
        _entryFolder.DirSelected += path => _entryFirst.Text = path;
        _entryDialog.Confirmed += () => LibraryAction(() => _submitEntry?.Invoke(_entryFirst.Text, _entrySecond.Text));
        _approvalDialog = new ConfirmationDialog { Name = "OperationApproval", OkButtonText = "确认", CancelButtonText = "取消" };
        AddChild(_approvalDialog);
        _approvalText = new RichTextLabel { CustomMinimumSize = new(220, 96), SelectionEnabled = true };
        _approvalDialog.AddChild(_approvalText);
        _approvalDialog.Confirmed += () => LibraryAction(() => _approved?.Invoke());
    }

    private void BuildLibraryManagerDialog()
    {
        _libraryDialog = new AcceptDialog { Name = "LibraryDialog", Title = "资产库管理" };
        AddChild(_libraryDialog);
        _libraryDialog.GetOkButton().Hide();
        var content = Column(_libraryDialog);
        content.AddThemeConstantOverride("separation", Tokens.Space.Md);

        var summary = new PanelContainer();
        summary.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control,
            Tokens.Space.Md, Tokens.Space.Sm, Tokens.Line));
        content.AddChild(summary);
        _openLibraryCount = Shell.Type.CaptionLabel("", Tokens.Text2);
        summary.AddChild(_openLibraryCount);
        _libraryLoadStatus = Shell.Type.CaptionLabel("", Tokens.Info);
        _libraryLoadStatus.Name = "LibraryLoadStatus";
        _libraryLoadStatus.Hide();
        content.AddChild(_libraryLoadStatus);
        BuildLibraryFilters(content);

        _libraryRows = new Tree
        {
            Name = "LibraryRows", Columns = 5, ColumnTitlesVisible = true, HideRoot = true, HideFolding = true,
            SelectMode = Tree.SelectModeEnum.Row, ScrollHorizontalEnabled = false,
            CustomMinimumSize = new(0, 74), SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _libraryRows.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, border: Tokens.LineStrong));
        _libraryRows.AddThemeStyleboxOverride("focus", Surfaces.Outline(Tokens.Accent.Alpha(.38f)));
        var heading = Surfaces.Fill(Tokens.Hover, 0, Tokens.Space.Md, Tokens.Space.Sm);
        foreach (var state in new[] { "normal", "hover", "pressed" })
            _libraryRows.AddThemeStyleboxOverride("title_button_" + state, heading);
        _libraryRows.AddThemeFontOverride("title_button_font", Typography.Semi);
        _libraryRows.AddThemeFontSizeOverride("title_button_font_size", Shell.Type.Caption);
        _libraryRows.AddThemeColorOverride("title_button_color", Tokens.Text2);
        _libraryRows.AddThemeConstantOverride("inner_item_margin_left", Tokens.Space.Sm);
        _libraryRows.AddThemeConstantOverride("inner_item_margin_right", Tokens.Space.Sm);
        ConfigureLibraryColumn(0, "库名称", 88, 1);
        ConfigureLibraryColumn(1, "状态", 72);
        ConfigureLibraryColumn(2, "库目录", 48, 2);
        ConfigureLibraryColumn(3, "最近打开", 118);
        ConfigureLibraryColumn(4, "标签", 68, 2);
        content.AddChild(_libraryRows);
        _libraryRows.ItemActivated += () =>
        {
            if (!_toggleBrowseLibrary.Disabled && LibrarySelection is { } entry && _libraries.FindOpenLibrary(entry.Id) == null)
                AddBrowseLibrary(entry.Path);
        };
        _libraryRows.ItemSelected += UpdateLibrarySelection;
        _libraryRows.NothingSelected += UpdateLibrarySelection;

        _emptyLibraries = Shell.Type.BodyLabel("", Tokens.Text2);
        _emptyLibraries.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyLibraries.VerticalAlignment = VerticalAlignment.Center;
        _emptyLibraries.MouseFilter = MouseFilterEnum.Pass;
        _libraryRows.AddChild(_emptyLibraries);
        _emptyLibraries.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var footer = Row(content);
        footer.Name = "LibraryFooter";
        var actions = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddThemeConstantOverride("h_separation", Tokens.Space.Md);
        footer.AddChild(actions);

        var selectionActions = new HBoxContainer();
        actions.AddChild(selectionActions);
        _toggleBrowseLibrary = Button("打开库", ToggleRegisteredBrowseLibrary, true);
        _toggleBrowseLibrary.Name = "ToggleBrowseLibrary";
        selectionActions.AddChild(_toggleBrowseLibrary);

        var secondaryActions = new HBoxContainer();
        actions.AddChild(secondaryActions);
        AddLibraryMenu(secondaryActions, "LibraryDirectoryActions", "目录操作",
            ("新建库…", CreateLibraryForm, null),
            ("打开目录…", () => PickLibraryFolder(path => AddBrowseLibrary(path)), null),
            ("仅登记目录…", () => PickLibraryFolder(path => LibraryLoadAction(() => RegisterLibraryAsync(path))), null),
            ("重命名所选库…", RenameSelectedLibrary, () => LibrarySelection is { } entry && LibraryManager.HasLibrary(entry.Path)),
            ("重新定位所选库…", () =>
            {
                var entry = SelectedLibrary();
                PickLibraryFolder(path => { _libraries.Relocate(entry.Id, path); ShowLibraryDialog(entry.Id); });
            }, () => LibrarySelection != null));

        bool HasSelectedOpenLibrary() => LibrarySelection is { } entry && _libraries.FindOpenLibrary(entry.Id) != null;
        AddLibraryMenu(secondaryActions, "LibraryMaintenanceActions", "库维护",
            ("检查所选库完整性", () =>
            {
                var maintenance = SelectedLibraryMaintenance();
                StartLibraryTask(() => CheckLibrary(maintenance));
            }, HasSelectedOpenLibrary),
            ("查找无动画资产…", () =>
            {
                var library = _libraries.FindOpenLibrary(SelectedLibrary().Id) ?? throw new InvalidOperationException("请先打开所选库。");
                StartLibraryTask(() => ReviewStaticAssets(library));
            }, HasSelectedOpenLibrary),
            ("备份所选库…", () =>
            {
                var maintenance = SelectedLibraryMaintenance();
                ShowEntry("备份库", "新备份目录的完整路径", "", null, "", false,
                    (path, _) => StartLibraryTask(() => BackupLibrary(maintenance, path)));
            }, HasSelectedOpenLibrary),
            ("从备份恢复…", () => ShowEntry("从备份恢复", "现有备份目录", "", "恢复到新的完整目录路径", "", true,
                (source, target) => StartLibraryTask(() => RestoreLibrary(source, target))), null),
            ("重建所选库索引…", () =>
            {
                var chosen = SelectedLibrary();
                ShowApproval("重建索引", chosen.Name + "\n" + chosen.Path + "\n现有索引会保存在 recovery 目录。",
                    () => StartLibraryTask(() => RebuildLibrary(chosen.Path)));
            }, () => LibrarySelection != null),
            ("清理所选库缓存…", () =>
            {
                var maintenance = SelectedLibraryMaintenance();
                StartLibraryTask(() => PreviewCacheCleanup(maintenance));
            }, HasSelectedOpenLibrary));

        var close = Button("关闭", _libraryDialog.Hide);
        close.Name = "CloseLibraryDialog";
        close.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        footer.AddChild(close);
    }

    private void ConfigureLibraryColumn(int column, string title, int minimumWidth, int expandRatio = 0)
    {
        _libraryRows.SetColumnTitle(column, title);
        _libraryRows.SetColumnTitleAlignment(column, HorizontalAlignment.Left);
        _libraryRows.SetColumnClipContent(column, true);
        _libraryRows.SetColumnCustomMinimumWidth(column, minimumWidth);
        _libraryRows.SetColumnExpand(column, expandRatio > 0);
        if (expandRatio > 0) _libraryRows.SetColumnExpandRatio(column, expandRatio);
    }

    private void AddLibraryMenu(HBoxContainer parent, string name, string title,
        params (string Label, Action Run, Func<bool>? Enabled)[] items)
    {
        var button = new MenuButton
        {
            Name = name, Text = title + "  ▾", Flat = false, FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new(0, 32), MouseDefaultCursorShape = CursorShape.PointingHand
        };
        parent.AddChild(button);
        var popup = button.GetPopup();
        foreach (var item in items) popup.AddItem(item.Label);
        popup.AboutToPopup += () =>
        {
            for (int i = 0; i < items.Length; i++)
                popup.SetItemDisabled(i, _libraryLoading || items[i].Enabled?.Invoke() == false);
        };
        popup.IndexPressed += index => { if (!_libraryLoading) LibraryAction(items[(int)index].Run); };
    }

    private void ShowLibraryDialog() => ShowLibraryDialog(_assetLibraryFilter);

    private MenuButton BuildOpenLibrariesMenu()
    {
        _openLibrariesMenu = new MenuButton
        {
            Name = "OpenLibrariesMenu", Text = "库管理 · 0 ▾", TooltipText = "查看已打开的库",
            Flat = false, FocusMode = FocusModeEnum.All, MouseDefaultCursorShape = CursorShape.PointingHand
        };
        var popup = _openLibrariesMenu.GetPopup();
        popup.IdPressed += id =>
        {
            var index = popup.GetItemIndex((int)id);
            if (index < 0) return;
            var libraryId = popup.GetItemMetadata(index);
            if (libraryId.VariantType == Variant.Type.String) ShowLibraryDialog(libraryId.AsString());
            else ShowLibraryDialog();
        };
        popup.AddItem("管理资产库…");
        return _openLibrariesMenu;
    }

    private void SyncOpenLibraries(LibrarySession[] libraries)
    {
        if (_visibleLibraryRevision == _browseLibraryRevision) return;
        _visibleLibraryRevision = _browseLibraryRevision;
        var entries = libraries.Select(library => (library.Identity.Id, library.Identity.Name,
            Tooltip: $"{library.Identity.Name}\n{library.Root}")).ToArray();
        _sideBar.SetOpenLibraries(entries);
        _openLibrariesMenu.Text = $"库管理 · {entries.Length} ▾";
        _openLibrariesMenu.TooltipText = $"查看已打开的 {entries.Length} 个库";

        var popup = _openLibrariesMenu.GetPopup();
        popup.Clear();
        foreach (var (id, name, tooltip) in entries)
        {
            popup.AddItem(Typography.Fit(Typography.Regular, name, Shell.Type.Body, 260));
            popup.SetItemMetadata(popup.ItemCount - 1, id);
            popup.SetItemTooltip(popup.ItemCount - 1, tooltip);
        }
        if (entries.Length > 0) popup.AddSeparator();
        popup.AddItem("管理资产库…");
    }

    private void ShowLibraryDialog(string? selectedId)
    {
        if (_libraryDialog == null) return;
        _libraryRegistryError = "";
        try { (_entries, _libraryTagCatalog, _libraryTagGroups) = _libraries.BrowseCatalog(); }
        catch (Exception ex)
        {
            _entries = [];
            _libraryTagCatalog = [];
            _libraryTagGroups = [];
            _libraryRegistryError = ex.Message;
        }

        SyncLibraryTagFilters();
        RenderLibraryRows(selectedId, selectFirst: selectedId == null || !HasLibraryFilters);
        if (!_libraryDialog.Visible)
        {
            PopupFit(_libraryDialog, new(880, 495));
            _libraryRows.GrabFocus();
        }
    }

    private void RenderLibraryRows(string? selectedId, bool selectFirst = false)
    {
        var openLibraries = _libraries.OpenLibraries();
        var entries = FilterLibraryEntries();
        _openLibraryCount.Text = HasLibraryFilters
            ? $"显示 {entries.Length} / {_entries.Length} 个库 · 已打开 {openLibraries.Length} 个"
            : $"已登记 {_entries.Length} 个库 · 已打开 {openLibraries.Length} 个";
        _emptyLibraries.Text = _libraryRegistryError.Length > 0 ? "无法读取库登记" :
            _entries.Length == 0 ? "暂无登记的资产库" : "没有符合当前筛选条件的库";
        _emptyLibraries.TooltipText = _libraryRegistryError;
        _libraryRows.Clear();
        var root = _libraryRows.CreateItem();
        TreeItem? selected = null;
        foreach (var entry in entries)
        {
            var row = root.CreateChild();
            row.CustomMinimumHeight = Tokens.Size.Row;
            var (status, color) = openLibraries.Any(library => library.Identity.Id == entry.Id) ? ("已打开", Tokens.Info) :
                !LibraryManager.HasLibrary(entry.Path) ? ("路径失效", Tokens.Warn) :
                ("已登记", Tokens.Text2);
            row.SetText(0, entry.Name);
            row.SetText(1, status);
            row.SetText(2, entry.Path);
            row.SetText(3, entry.LastOpenedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未打开");
            var tags = entry.Tags ?? [];
            row.SetText(4, tags.Length == 0 ? "—" : string.Join(" · ", tags.Select(tag => tag.Name)));
            row.SetCustomColor(0, Tokens.Text);
            row.SetCustomColor(1, color);
            row.SetCustomColor(2, Tokens.Text2);
            row.SetCustomColor(3, Tokens.Text2);
            row.SetCustomColor(4, Tokens.Text2);
            row.SetTooltipText(0, entry.Name);
            row.SetTooltipText(2, entry.Path);
            row.SetTooltipText(4, tags.Length == 0 ? "未标记" : string.Join("、", tags.Select(tag => tag.Name)));
            row.SetMetadata(0, entry.Id);
            if (entry.Id == selectedId) selected = row;
        }
        _emptyLibraries.Visible = entries.Length == 0;
        (selected ?? (selectFirst ? root.GetFirstChild() : null))?.Select(0);
        UpdateLibrarySelection();
        _libraryRows.CallDeferred(Tree.MethodName.EnsureCursorIsVisible);
    }

    private LibraryEntry? LibrarySelection
    {
        get
        {
            var id = _libraryRows.GetSelected()?.GetMetadata(0).AsString();
            return id == null ? null : Array.Find(_entries, entry => entry.Id == id);
        }
    }

    private LibraryEntry SelectedLibrary() => LibrarySelection ?? throw new InvalidOperationException("请先选择库。");

    private void UpdateLibrarySelection()
    {
        var entry = LibrarySelection;
        bool available = entry != null && LibraryManager.HasLibrary(entry.Path);
        bool opened = entry != null && _libraries.FindOpenLibrary(entry.Id) != null;
        _assignLibraryTags.Disabled = _libraryLoading || _entries.Length == 0;
        _libraryTagManagement.Disabled = _libraryLoading || _libraryRegistryError.Length > 0;
        _toggleBrowseLibrary.Disabled = _libraryLoading || (!available && !opened);
        _toggleBrowseLibrary.Text = opened ? "移出浏览" : "打开库";
        _toggleBrowseLibrary.TooltipText = opened ? "从当前浏览中移除，保留库目录和登记记录" : "";
    }

    private void ToggleRegisteredBrowseLibrary() => LibraryAction(() =>
    {
        if (_libraryLoading) return;
        var entry = SelectedLibrary();
        if (_libraries.FindOpenLibrary(entry.Id) != null) RequestCloseLibrary(entry.Id);
        else AddBrowseLibrary(entry.Path);
    });

    private void AddBrowseLibrary(string path, string? name = null) => LibraryLoadAction(() =>
        RunLibraryLoadAsync("正在打开资产库…", async () =>
        {
            var library = await _libraries.OpenAsync(NormalizeFolder(path), browse: true, newName: name);
            if (!_closing) await ShowOpenedLibrary(library);
        }));

    private async Task ShowOpenedLibrary(LibrarySession library)
    {
        if (_closing) return;
        if (_api == null) _plainAssetKeyLibrary = library;
        BrowseLibrariesChanged();
        _workspace.ShowPanel("assets");
        if (_libraryDialog.Visible) ShowLibraryDialog(library.Identity.Id);
        QueueLibraryPreviewWarmup(library);
        await PendingBrowse;
        if (!_closing) SetStatus("已打开资产库 · " + library.Identity.Name);
    }

    private Task RegisterLibraryAsync(string path) => RunLibraryLoadAsync("正在登记资产库…", async () =>
    {
        var entry = await _libraries.RegisterAsync(path);
        if (_closing) return;
        await Task.Run(() => AppCollections.ImportRegisteredLibraries([entry]));
        if (_closing) return;
        Refresh();
        ShowLibraryDialog(entry.Id);
        SetStatus("已登记 · " + entry.Name);
    });

    private void RequestCloseLibrary(string id)
    {
        if (_libraries.FindOpenLibrary(id) is { } library)
            LibraryLoadAction(() => RunLibraryLoadAsync("正在移出资产库…", () => CloseLibraryAsync(library)));
    }

    private async Task CloseLibraryAsync(LibrarySession library)
    {
        if (library.Api.Jobs.Busy)
            throw new InvalidOperationException("请等待此库的任务完成，或在任务面板取消后再移出。");
        var comparison = PendingAnimationComparison;
        if (_comparisonLoadingLibrary == library) _comparisonRequest?.Cancel();
        var loads = CancelPreviewLoads(library);
        if (_activePreviewTab.Library == library || _activePreviewTab.DisplayedLibrary == library)
        {
            BeforeLibrarySwitch();
            await Task.WhenAll(loads, PendingRetargetPreview, comparison, _pendingRetargetSource);
        }
        else await Task.WhenAll(loads, comparison);
        if (_libraryPreviewWarmup?.Library == library) await StopLibraryPreviewWarmupAsync();
        else await _previewWarmupCleanup;
        if (_closing) return;

        string id = library.Identity.Id;
        if (_motionReference?.Library == library) ClearMotionFilter();
        else if (_motionStart?.Library == library) _motionStart = null;
        foreach (var tab in _previewTabsByStage.Values.Where(t => t.Library == library || t.DisplayedLibrary == library).ToArray())
            ClosePreviewTab(tab);
        _libraries.CloseLibrary(id);
        if (_plainAssetKeyLibrary == library) _plainAssetKeyLibrary = null;
        if (_assetLibraryFilter == id) _assetLibraryFilter = null;
        BrowseLibrariesChanged();
        if (_libraryDialog.Visible) ShowLibraryDialog(id);
        await PendingBrowse;
        if (!_closing) SetStatus("已移出浏览 · " + library.Identity.Name);
    }

    private void BrowseLibrariesChanged()
    {
        _browseLibraryRevision++;
        SyncConsoleLibrary();
        SyncFocusedPreviewSelection();
        SyncLibraryJobs();
        RememberOpenLibraries();
        if (CurrentCollection == null)
        {
            _page = 0;
            _browser.Scroll.ScrollVertical = 0;
        }
        Refresh();
    }

    private void RememberOpenLibraries()
    {
        var ids = _libraries.OpenLibraries().Select(library => library.Identity.Id).ToArray();
        _unavailableLibraryIds.ExceptWith(ids);
        _preferences.OpenLibraryIds = [.. ids, .. _unavailableLibraryIds];
        _preferences.BrowseLibraryIds = [];
        SaveSoon();
    }

    private Task RestoreBrowseLibrariesAsync() => RunLibraryLoadAsync("正在加载上次打开的浏览库…", async () =>
    {
        foreach (var entry in _libraries.Entries().Where(entry => _preferences.BrowseLibraryIds.Contains(entry.Id)))
        {
            if (_closing) return;
            try { await _libraries.OpenAsync(entry.Path, browse: true); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _unavailableLibraryIds.Add(entry.Id);
                _workspaceError += $"\n无法加载 {entry.Name} · {ex.Message}";
            }
        }
        if (_closing) return;
        // Keep the saved list when one of the previous folders is temporarily unavailable.
        BrowseLibrariesChanged();
        if (_libraryDialog.Visible) ShowLibraryDialog();
        await PendingBrowse;
    });

    private void OpenLibraryFolder()
    {
        ChooseOpenLibrary("打开库目录", library => OS.ShellOpen(library.Root));
    }

    private void PickLibraryFolder(Action<string> chosen) =>
        ShowEntry("选择库目录", "库目录的完整路径", "", null, "", true, (path, _) => chosen(path));

    private void CreateLibraryForm() =>
        ShowEntry("新建空库", "空目录或新目录的完整路径", "", "库名称", "", true,
            AddBrowseLibrary);

    private void RenameSelectedLibrary()
    {
        var entry = SelectedLibrary();
        ShowEntry("重命名资产库", "库名称", entry.Name, null, "", false, (name, _) =>
        {
            var renamed = _libraries.Rename(entry.Id, name);
            _browseLibraryRevision++;
            Refresh();
            ShowLibraryDialog(renamed.Id);
            SetStatus("已重命名资产库 · " + renamed.Name);
        });
    }

    private void ShowEntry(string title, string firstLabel, string first, string? secondLabel, string second,
        bool browse, Action<string, string> submit)
    {
        _libraryDialog.Hide();
        _entryDialog.Title = title;
        _entryFirstLabel.Text = firstLabel;
        _entryFirst.Text = first;
        _entrySecondLabel.Text = secondLabel ?? "";
        _entrySecondLabel.Visible = _entrySecond.Visible = secondLabel != null;
        _entrySecond.Text = second;
        _entryBrowse.Visible = browse;
        _submitEntry = submit;
        PopupFit(_entryDialog, new(540, secondLabel == null ? 155 : 220));
        _entryFirst.GrabFocus();
        _entryFirst.SelectAll();
    }

    private void ShowApproval(string title, string text, Action apply)
    {
        _libraryDialog.Hide();
        _approvalDialog.Title = title;
        _approvalText.Text = text;
        _approved = apply;
        PopupFit(_approvalDialog, new(580, 280));
    }

    private void LibraryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { _libraryDialog.Hide(); ShowError("操作失败", ex.Message); }
    }

    private async void LibraryLoadAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (!_closing) ShowError("库操作失败", ex.Message); }
    }

    private Task RunLibraryLoadAsync(string status, Func<Task> load)
    {
        if (_libraryLoading || !_libraryUiTask.IsCompleted) throw new InvalidOperationException("请等待当前库操作完成。");
        return _libraryUiTask = TrackLoadAsync();

        async Task TrackLoadAsync()
        {
            _libraryLoading = true;
            try
            {
                _libraryLoadStatus.Text = status;
                _libraryLoadStatus.Show();
                UpdateLibrarySelection();
                SetStatus(status);
                await load();
            }
            catch (Exception ex)
            {
                if (!_closing) SetStatus("库操作失败 · " + ex.Message);
                throw;
            }
            finally
            {
                _libraryLoading = false;
                if (!_closing)
                {
                    _libraryLoadStatus.Hide();
                    UpdateLibrarySelection();
                    SyncAssetSelection();
                }
            }
        }
    }

    private static string NormalizeFolder(string path)
    {
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请输入完整目录路径。");
        return Path.GetFullPath(path);
    }

    private LibraryMaintenance SelectedLibraryMaintenance() =>
        new(_libraries.FindOpenLibrary(SelectedLibrary().Id)?.Api ?? throw new InvalidOperationException("请先打开所选库。"));

    private void StartLibraryTask(Func<Task> action)
    {
        if (!_libraryUiTask.IsCompleted) { SetStatus("请等待当前库维护操作完成。"); return; }
        _libraryDialog.Hide();
        _libraryUiTask = RunLibraryTask(action);
    }

    private async Task RunLibraryTask(Func<Task> action)
    {
        try
        {
            BeforeLibrarySwitch();
            await StopLibraryPreviewWarmupAsync();
            SetStatus("正在处理库维护操作…");
            await action();
        }
        catch (Exception ex) { if (!_closing) ShowError("库维护失败", ex.Message); }
    }

    private async Task CheckLibrary(LibraryMaintenance maintenance)
    {
        var issues = await maintenance.CheckAsync();
        ShowError("完整性检查", issues.Length == 0 ? "检查通过。" : string.Join("\n", issues.Select(LibraryMaintenance.FormatIssue)));
    }

    private async Task ReviewStaticAssets(LibrarySession library)
    {
        var (assets, errors) = await library.Api.FindAssetsWithoutAnimationAsync();
        if (_closing) return;
        var skipped = errors.Length == 0 ? "" : $"\n\n有 {errors.Length} 个文件无法检查。\n" + string.Join("\n", errors);
        if (assets.Length == 0)
        {
            ShowError("无动画资产", $"{library.Identity.Name}\n未发现无动画资产。" + skipped);
            return;
        }
        ShowApproval("移入回收站", $"{library.Identity.Name}\n发现 {assets.Length} 个无动画资产。\n\n"
            + string.Join("\n", assets.Select(asset => asset.Name)) + skipped,
            () => StartLibraryTask(() =>
            {
                if (_libraries.FindOpenLibrary(library.Identity.Id) != library)
                    throw new InvalidOperationException("所选库已关闭，请重新检查。");
                var ids = assets.Select(asset => asset.Id).Intersect(
                    library.Api.Database.Snapshot().Assets.Where(asset => !asset.Trashed).Select(asset => asset.Id)).ToArray();
                if (ids.Length != 0) library.Api.SetTrash(ids, true);
                Refresh();
                SetStatus($"已将 {ids.Length} 个无动画资产移入回收站。");
                return Task.CompletedTask;
            }));
    }

    private async Task BackupLibrary(LibraryMaintenance maintenance, string destination) =>
        ShowError("备份完成", await maintenance.BackupAsync(NormalizeFolder(destination)));

    private async Task RestoreLibrary(string source, string target) =>
        await ShowOpenedLibrary(await _libraries.RestoreAsync(NormalizeFolder(source), NormalizeFolder(target), browse: true));

    private async Task RebuildLibrary(string root)
    {
        await Task.Run(() => _libraries.RebuildAt(root));
        Refresh();
        SetStatus("索引重建完成 · " + root);
    }

    private async Task PreviewCacheCleanup(LibraryMaintenance maintenance)
    {
        var plan = await maintenance.PreviewCacheAsync();
        var summary = $"持久文件 {FormatSize(plan.PersistentBytes)}\n转换目录 {FormatSize(plan.ConversionBytes)}\n缩略图与属性缓存 {FormatSize(plan.PreviewDiskBytes)}\n"
            + $"可清理 {plan.Files.Length} 个文件 · {FormatSize(plan.EligibleBytes)}";
        if (plan.Files.Length == 0) { ShowError("缓存清理", summary); return; }
        ShowApproval("清理转换与预览缓存", summary + "\n\n" + string.Join("\n", plan.Files.Select(f => f.Path)),
            () => StartLibraryTask(async () => { var result = await maintenance.ClearCacheAsync(plan); Refresh(); ShowCleanup(result); }));
    }

    private void ShowCleanup(CleanupResult result) =>
        ShowError("清理结果", $"已清理 {result.Removed} 个文件 · {FormatSize(result.Bytes)}" +
            (result.Retained.Length == 0 ? "" : "\n\n保留：\n" + string.Join("\n", result.Retained)));

    private void ShowAssetCategory(string category)
    {
        if (category == _collectionBrowseReturn?.Category)
        {
            CloseCollection();
            return;
        }
        _collectionBrowseReturn = null;
        SetAssetCategory(category, 0);
        if (category == "All" && LoadedLibraries().Length > 1) ShowAllBrowseLibraries();
        else Refresh();
        _workspace.ShowPanel("assets");
    }

    private void SetAssetCategory(string category, int page)
    {
        _searchTimer.Stop();
        if (_category == "Trash" ^ category == "Trash") ClearAssetSelection();
        _category = category;
        _page = page;
        _browser.Scroll.ScrollVertical = 0;
    }

    private void SelectOnly(int index)
    {
        var row = _browseRows[index];
        _assetSelection.Select(AssetKey(row.Library, row.Asset.Id));
    }

    internal void ShowAssetContextMenu(string id)
    {
        int index = _browser.IndexOf(id);
        if (index < 0) return;
        _assetContextKey = id;
        _assetContextMenu.Clear(freeSubmenus: false);
        if (_browser.Row(index) is { ClosedLibraryId: not null } closed)
        {
            _assetContextMenu.AddItem(AssetBrowser.SourceActionLabel(closed), OpenCollectionSourceAction);
            _assetContextMenu.AddItem(RemoveCurrentCollectionMembersLabel, RemoveClosedCollectionMemberAction);
            PopupMenuAtPointer(_assetContextMenu);
            return;
        }
        if (!_assetSelection.Contains(id)) SelectOnly(index);
        bool selected = CanEditAssetSelection();
        void Add(string text, int action, bool enabled = true)
        {
            _assetContextMenu.AddItem(text, action);
            _assetContextMenu.SetItemDisabled(_assetContextMenu.GetItemIndex(action), !selected || !enabled);
        }
        if (_category == "Trash")
        {
            Add("恢复所选…", RestoreAssetsAction);
            Add("永久删除所选…", PurgeAssetsAction);
        }
        else
        {
            if (index < _browseRows.Length && !_browseRows[index].Asset.Trashed)
            {
                _assetContextMenu.AddItem("在当前标签打开", OpenInCurrentPreviewAction);
                _assetContextMenu.AddItem("在新标签打开", OpenInNewPreviewAction);
                _assetContextMenu.AddSeparator();
            }
            if (CurrentCollection is { } collection)
                Add(RemoveCurrentCollectionMembersLabel, RemoveCurrentCollectionMembersAction, SelectedCollectionLinks(collection).Any());
            Add(TrashAssetsLabel, TrashAssetsAction);
            Add("添加标签…", AddAssetTagsAction);
            if (_assetSelection.Any(CanCompareAsset)) Add("对比所选", CompareAssetsAction);
            AddPluginAssetMenu(id, index);
        }
        Add("在来源库中定位", LocateAssetAction);
        PopupMenuAtPointer(_assetContextMenu);
    }
    private void SyncAssetSelection() { UpdateAssetSelectionCount(); }

    private void UpdateAssetSelectionCount()
    {
        _selectionBar?.SetTrashMode(_category=="Trash");
        _selectionBar?.SetCount(_assetSelection.Count);
        _selectionBar?.SetEditingEnabled(CanEditAssetSelection());
        if (_locateAsset != null) _locateAsset.Disabled = _assetSelection.Count != 1 || !CanEditAssetSelection();
        _workspace.Status.ContextLabel.Text=$"{FilteredAssetCount:N0} 个资产 · 已选 {_assetSelection.Count}";
        SyncFilterChips();SyncExportControls();
    }

    private void ClearAssetSelection()
    {
        _assetSelection.Clear();
        _assetAnchor = null;
    }

    private void RefreshCurrentAsset()
    {
        RefreshPreviewTabs();
        if (_activePreviewTab.Asset is not { } current) return;
        if (current.Tags != _selected?.Tags) _tags.Text = current.Tags;
        _selected = current;
        _title.Text = current.Name;
        _inspectorStamp="";SyncInspector();
        if (_previewAssetId == _selectedKey)
        {
            _stage.TitleLabel.Text = current.Name;
            _exportSummary.Text = "导出资产：" + current.Name;
        }
    }

    private MenuButton BuildAssetActions()
    {
        _assetActions = new MenuButton
        {
            Text = "整理 ▾", TooltipText = "选择、编辑资产和管理集合",
            Flat = false, FocusMode = FocusModeEnum.All, MouseDefaultCursorShape = CursorShape.PointingHand
        };
        _assetActions.AboutToPopup += FillAssetMenu;
        _assetActions.GetPopup().IdPressed += id => LibraryAction(() => AssetAction((int)id));
        _assetContextMenu = new PopupMenu { Name = "AssetContextMenu" };
        AddChild(_assetContextMenu);
        _assetContextMenu.IdPressed += id => LibraryAction(() => AssetAction((int)id));
        BuildPluginAssetMenu();
        return _assetActions;
    }

    private void FillAssetMenu()
    {
        var menu = _assetActions.GetPopup();
        menu.Clear();
        void Add(string text, int id, bool enabled) { menu.AddItem(text, id); menu.SetItemDisabled(menu.GetItemIndex(id), !enabled); }
        bool selected = CanEditAssetSelection();
        var currentCollection = CurrentCollection;
        if (currentCollection != null)
            Add(RemoveCurrentCollectionMembersLabel, RemoveCurrentCollectionMembersAction,
                selected && SelectedCollectionLinks(currentCollection).Any());
        Add("清空选择", 0, _assetSelection.Count > 0);
        Add("选择当前页", 1, _browseRows.Length > 0);
        if (_category == "Trash")
        {
            Add("恢复所选…", RestoreAssetsAction, selected);
            Add("永久删除所选…", PurgeAssetsAction, selected);
            return;
        }
        Add("重命名…", 4, selected && _assetSelection.Count == 1);
        Add("添加标签…", AddAssetTagsAction, selected);
        Add("移除标签…", RemoveAssetTagsAction, selected);
        Add("收藏所选…", 7, selected);
        Add("取消所选收藏…", 8, selected);
        Add("对比所选", CompareAssetsAction, _assetSelection.Any(CanCompareAsset));
        Add(TrashAssetsLabel, TrashAssetsAction, selected);
        menu.AddSeparator();
        Add("新建集合…", 10, true);
        Add("重命名当前集合…", 11, currentCollection != null);
        Add("删除当前集合…", 12, currentCollection != null);
        foreach (var (collection, index) in _collections.Select((c, i) => (c, i)))
        {
            Add("加入 " + collection.Name + "…", 100 + 2 * index, selected);
            if (collection != currentCollection)
                Add("移出 " + collection.Name + "…", 101 + 2 * index, selected);
        }
    }

    private void AssetAction(int action)
    {
        if (action is OpenInCurrentPreviewAction or OpenInNewPreviewAction)
        {
            if (!_closing && !_libraryLoading && _category != "Trash" &&
                _assetContextKey is { } assetKey && FindLiveAsset(assetKey) is { } row)
                OpenAssetInPreview(row.Library, row.Asset, inNewTab: action == OpenInNewPreviewAction);
            return;
        }
        if (action is OpenCollectionSourceAction or RemoveClosedCollectionMemberAction)
        {
            if (_assetContextKey is { } key && CurrentCollection is { } collection &&
                _browser.Row(_browser.IndexOf(key)) is { ClosedLibraryId: { } libraryId } card)
            {
                var link = new CollectionAssetLink(libraryId, SelectionAssetId(key));
                if (!collection.Members.Contains(link)) return;
                if (action == OpenCollectionSourceAction) OpenCollectionSourceLibrary(libraryId, card.LocateClosedLibrary);
                else ConfirmCollectionMemberRemoval(collection, [(key, link)]);
            }
            return;
        }
        if (action is not (0 or 1 or 10 or 11 or 12) && !CanEditAssetSelection())
            throw new InvalidOperationException("请重新选择要整理的资产。");
        var groups = _assetSelection.GroupBy(SelectionLibrary).Where(group => group.Key != null)
            .Select(group => (Library: group.Key!, Ids: group.Select(SelectionAssetId).ToArray())).ToArray();
        int hidden = _assetSelection.Count - _browseRows.Count(row => _assetSelection.Contains(AssetKey(row.Library, row.Asset.Id)));
        string scope = $"已选 {_assetSelection.Count}" + (hidden > 0 ? $" · 其他页或筛选外 {hidden}" : "");
        void Apply(Action<WorkspaceApi, string[]> edit, bool reset = false) => ApplyAssetEdit(groups, edit, reset);
        switch (action)
        {
            case 0:
                ClearAssetSelection(); SyncAssetSelection();
                break;
            case 1: _assetSelection.UnionWith(_browseRows.Select(row => AssetKey(row.Library, row.Asset.Id))); SyncAssetSelection(); break;
            case RestoreAssetsAction: ShowApproval("恢复资产", scope, () => Apply((api, ids) => api.SetTrash(ids, false), true)); break;
            case PurgeAssetsAction:
                ShowApproval("永久删除资产", scope + "\n此操作无法从回收站恢复。共享对象及保留修订继续保存。",
                    () => StartLibraryTask(async () =>
                    {
                        try
                        {
                            int removed = 0;
                            long bytes = 0;
                            var retained = new List<string>();
                            foreach (var (library, ids) in groups)
                            {
                                var result = await new LibraryMaintenance(library.Api).PurgeAsync(ids);
                                removed += result.Removed;
                                bytes += result.Bytes;
                                retained.AddRange(result.Retained);
                            }
                            ShowCleanup(new(removed, bytes, retained.ToArray()));
                        }
                        finally { ClearAssetSelection(); Refresh(); }
                    }));
                break;
            case 4:
                var rename = groups.Single();
                var asset = rename.Library.Api.Find(rename.Ids.Single());
                ShowEntry("重命名资产", "显示名称", asset.Name, null, "", false, (name, _) => Apply((api, _) => api.RenameAsset(asset.Id, name)));
                break;
            case AddAssetTagsAction:
            case RemoveAssetTagsAction:
                ShowAssetTagChange(groups, scope, add: action == AddAssetTagsAction);
                break;
            case 7:
            case 8:
                ShowApproval(action == 7 ? "收藏所选" : "取消所选收藏", scope, () => Apply((api, ids) => api.SetFavorites(ids, action == 7)));
                break;
            case TrashAssetsAction: ShowApproval("移入回收站", scope, () => Apply((api, ids) => api.SetTrash(ids, true), true)); break;
            case 10: ShowCollectionEditor(); break;
            case 11: RunCollectionAction(_category, CollectionMenuAction.Rename); break;
            case 12: RunCollectionAction(_category, CollectionMenuAction.Delete); break;
            case CompareAssetsAction: CompareAssets(_assetSelection); break;
            case LocateAssetAction:
                if (_assetContextKey != null) LocateAssetInLibrary(_assetContextKey);
                break;
            case RemoveCurrentCollectionMembersAction:
                ConfirmCollectionMemberRemoval(CurrentCollection ?? throw new InvalidOperationException("请先打开集合。"));
                break;
            default:
                var membership = _collections[(action - 100) / 2];
                if (action % 2 == 0)
                    ShowApproval("加入 " + membership.Name, scope, () => AddAssetsToCollection(membership, groups));
                else
                    ConfirmCollectionMemberRemoval(membership);
                break;
        }
    }

    private void ApplyAssetEdit((LibrarySession Library, string[] Ids)[] groups, Action<WorkspaceApi, string[]> edit, bool reset = false)
    {
        var activities = new List<IDisposable>();
        try
        {
            foreach (var group in groups) activities.Add(group.Library.Api.BeginActivity());
            foreach (var (library, ids) in groups) edit(library.Api, ids);
            if (reset) ClearAssetSelection();
        }
        finally
        {
            foreach (var activity in activities) activity.Dispose();
            Refresh();
        }
        SetStatus("资产整理已保存");
    }
}
