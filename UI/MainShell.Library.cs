using Animanager.Core;
using Animanager.Core.Models;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private LibraryManager _libraries = null!;
    private AcceptDialog _libraryDialog = null!;
    private ItemList _libraryRows = null!;
    private Label _activeLibrary = null!;
    private ConfirmationDialog _entryDialog = null!, _approvalDialog = null!;
    private LineEdit _entryFirst = null!, _entrySecond = null!;
    private Label _entryFirstLabel = null!, _entrySecondLabel = null!;
    private Button _entryBrowse = null!;
    private FileDialog _entryFolder = null!;
    private RichTextLabel _approvalText = null!;
    private Action? _approved;
    private Action<string, string>? _submitEntry;
    private LibraryEntry[] _entries = [];
    private Task _libraryUiTask = Task.CompletedTask;

    private readonly HashSet<string> _assetSelection = [];
    private OptionButton _collectionFilter = null!;
    private AssetCollection[] _collections = [];
    private string? _assetAnchor;
    private MenuButton _assetActions = null!;
    private PopupMenu _assetContextMenu = null!;
    private int _selectionEpoch, _mouseEpoch = -1;
    private Label _selectionCount = null!;

    private const int RestoreAssetsAction = 2, PurgeAssetsAction = 3, TrashAssetsAction = 9;
    private const string TrashAssetsLabel = "删除（移入回收站）…";

    internal LibraryManager Libraries => _libraries;
    internal string[] SelectedAssetIds => _assetSelection.ToArray();

    private (bool Bootstrap, string Error) OpenStartupLibrary()
    {
        var args = OS.GetCmdlineUserArgs();
        var explicitRoot = RuntimePaths.ExplicitLibraryRoot();
        var defaultRoot = RuntimePaths.DefaultLibraryRoot();
        var uniqueTests = args.Any(a => a is "--pipeline-test" or "--batch-test" or "--interface-test" or "--smoke-test"
            or "--bone-test" or "--bone-ui-test" or "--multi-bone-test" or "--multi-bone-ui-test" or "--library-p0-test");
        var test = uniqueTests || args.Any(a => a.StartsWith("--") && (a.Contains("-test") || a.Contains("-render") || a.StartsWith("--cue-")));
        var registry = test
            ? Path.Combine(Path.GetTempPath(), "Animanager-test-registries", Guid.NewGuid().ToString("N"), "libraries.json")
            : ProjectSettings.GlobalizePath("user://libraries.json");
        _preferencesPath = Path.Combine(Path.GetDirectoryName(registry)!, "workspace.json");
        _libraries = new(registry, new BlenderPipeline(RuntimePaths.Bundled("BuiltinAssets/Scripts/blender_convert.py")));
        if (uniqueTests) explicitRoot = Path.Combine(explicitRoot ?? defaultRoot, Guid.NewGuid().ToString("N"));
        var startup = _libraries.OpenStartup(explicitRoot, defaultRoot, ProjectSettings.GlobalizePath("user://Library"));
        if (startup.Session is { } session)
        {
            _api = session.Api;
            _database = session.Api.Database;
            _dataRoot = session.Root;
            GD.Print($"Asset library: {_dataRoot}");
        }
        return (startup.Bootstrap, startup.Message);
    }

    private void BeforeLibrarySwitch()
    {
        if (_closing) throw new InvalidOperationException("应用正在关闭。");
    }

    private void SwitchLibrary(Func<LibrarySession> open)
    {
        BeforeLibrarySwitch();
        ActivateLibrary(open());
    }

    private void ActivateLibrary(LibrarySession session)
    {
        if (_api == session.Api) { _libraryDialog.Hide(); return; }
        if (_api != null) _api.Jobs.Changed -= OnWorkspaceJob;
        while (_jobUpdates.TryDequeue(out _)) { }
        while (_progress.TryDequeue(out _)) { }
        _jobIds.Clear();
        _jobsList.Clear();
        _jobDetails.Text = "";
        _lastExport = "";
        ClearAssetSelection();
        _category = "All";
        _page = 0;
        _searchTimer.Stop();
        _search.Text = "";
        _assetContextMenu.Hide();
        ClearAssetPreview();
        _api = session.Api;
        _database = session.Api.Database;
        _dataRoot = session.Root;
        _commands.ChangeLibrary(_api);
        _api.Jobs.Changed += OnWorkspaceJob;
        _workspace.CancelDrag();
        _libraryDialog.Hide();
        Refresh();
        RefreshJobs();
        SetStatus("已打开 " + session.Identity.Name + " · " + session.Root);
    }

    private void BuildLibraryDialogs()
    {
        _libraryDialog = new AcceptDialog { Name = "LibraryDialog", Title = "Library", OkButtonText = "关闭" };
        AddChild(_libraryDialog);
        var content = Column(_libraryDialog);
        _activeLibrary = Wrapped("");
        content.AddChild(_activeLibrary);
        _libraryRows = new ItemList { Name = "LibraryRows", CustomMinimumSize = new(240, 90), SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddChild(_libraryRows);
        _libraryRows.ItemActivated += _ => OpenRegisteredLibrary();
        var libraryActions = new HFlowContainer();
        content.AddChild(libraryActions);
        var open = Button("打开所选", OpenRegisteredLibrary, true);
        open.Name = "OpenLibraryChoice";
        libraryActions.AddChild(open);
        libraryActions.AddChild(Button("新建库…", CreateLibraryForm));
        libraryActions.AddChild(Button("登记或打开目录…", () => PickLibraryFolder(path => SwitchLibrary(() => _libraries.Open(path)))));
        libraryActions.AddChild(Button("仅登记…", () => PickLibraryFolder(path => { _libraries.Register(path); ShowLibraryDialog(); })));
        libraryActions.AddChild(Button("重新定位…", () => LibraryAction(() =>
        {
            var entry = SelectedLibrary();
            PickLibraryFolder(path => { _libraries.Relocate(entry.Id, path); ShowLibraryDialog(); });
        })));
        var maintenance = new HFlowContainer();
        content.AddChild(maintenance);
        maintenance.AddChild(Button("完整性检查", () => StartLibraryTask(CheckLibrary)));
        maintenance.AddChild(Button("备份…", () => ShowEntry("备份库", "新备份目录的完整路径", "", null, "", false,
            (path, _) => StartLibraryTask(() => BackupLibrary(path)))));
        maintenance.AddChild(Button("从备份恢复…", () => ShowEntry("从备份恢复", "现有备份目录", "", "恢复到新的完整目录路径", "", true,
            (source, target) => StartLibraryTask(() => RestoreLibrary(source, target)))));
        maintenance.AddChild(Button("重建索引…", () => LibraryAction(() =>
        {
            var chosen = SelectedLibrary();
            ShowApproval("重建索引", chosen.Name + "\n" + chosen.Path + "\n现有索引会保存在 recovery 目录。",
                () => StartLibraryTask(() => RebuildLibrary(chosen.Path)));
        })));
        maintenance.AddChild(Button("转换缓存…", () => StartLibraryTask(PreviewCacheCleanup)));

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

    private void ShowLibraryDialog()
    {
        if (_libraryDialog == null) return;
        _libraryRows.Clear();
        var current = _libraries.Active;
        _activeLibrary.Text = current == null ? "请选择或新建资产库。" : current.Identity.Name + "\n" + current.Root;
        try { _entries = _libraries.Entries(); }
        catch (Exception ex) { _entries = []; _activeLibrary.Text += "\n" + ex.Message; }
        for (int i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            var last = entry.LastOpenedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未打开";
            var availability = LibraryManager.HasLibrary(entry.Path) ? "" : " · 路径失效";
            _libraryRows.AddItem($"{entry.Name} · {entry.Path} · {last}{availability}");
            _libraryRows.SetItemTooltip(i, entry.Path + "\n" + entry.Id);
            if (entry.Id == current?.Identity.Id) _libraryRows.Select(i);
        }
        if (_entries.Length > 0 && !_libraryRows.IsAnythingSelected()) _libraryRows.Select(0);
        PopupFit(_libraryDialog, new(740, 420));
    }

    private LibraryEntry SelectedLibrary()
    {
        var index = _libraryRows.GetSelectedItems().FirstOrDefault(-1);
        if (index < 0 || index >= _entries.Length) throw new InvalidOperationException("请先选择库。");
        return _entries[index];
    }

    private void OpenRegisteredLibrary() => LibraryAction(() => SwitchLibrary(() => _libraries.OpenById(SelectedLibrary().Id)));

    private void OpenLibraryFolder()
    {
        if (_dataRoot.Length > 0) OS.ShellOpen(_dataRoot);
        else ShowLibraryDialog();
    }

    private void PickLibraryFolder(Action<string> chosen) =>
        ShowEntry("选择库目录", "库目录的完整路径", "", null, "", true, (path, _) => chosen(path));

    private void CreateLibraryForm() =>
        ShowEntry("新建空库", "空目录或新目录的完整路径", "", "库名称", "", true,
            (path, name) => SwitchLibrary(() => _libraries.CreateAndOpen(NormalizeFolder(path), name)));

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

    private static string NormalizeFolder(string path)
    {
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请输入完整目录路径。");
        return Path.GetFullPath(path);
    }

    private LibraryMaintenance CurrentMaintenance() =>
        new(_libraries.Active?.Api ?? throw new InvalidOperationException("请先打开一个库。"));

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
            SetStatus("正在处理库维护操作…");
            await action();
        }
        catch (Exception ex) { if (!_closing) ShowError("库维护失败", ex.Message); }
    }

    private async Task CheckLibrary()
    {
        var issues = await CurrentMaintenance().CheckAsync();
        ShowError("完整性检查", issues.Length == 0 ? "检查通过。" : string.Join("\n", issues.Select(LibraryMaintenance.FormatIssue)));
    }

    private async Task BackupLibrary(string destination) =>
        ShowError("备份完成", await CurrentMaintenance().BackupAsync(NormalizeFolder(destination)));

    private async Task RestoreLibrary(string source, string target) =>
        ActivateLibrary(await _libraries.RestoreAsync(NormalizeFolder(source), NormalizeFolder(target)));

    private async Task RebuildLibrary(string root)
    {
        await Task.Run(() => _libraries.RebuildAt(root));
        Refresh();
        SetStatus("索引重建完成 · " + root);
    }

    private async Task PreviewCacheCleanup()
    {
        var maintenance = CurrentMaintenance();
        var plan = await maintenance.PreviewCacheAsync();
        var summary = $"持久文件 {FormatSize(plan.PersistentBytes)}\n转换目录 {FormatSize(plan.ConversionBytes)}\n预览缓存仅使用内存\n"
            + $"可清理 {plan.Files.Length} 个文件 · {FormatSize(plan.EligibleBytes)}";
        if (plan.Files.Length == 0) { ShowError("转换缓存", summary); return; }
        ShowApproval("清理转换缓存", summary + "\n\n" + string.Join("\n", plan.Files.Select(f => f.Path)),
            () => StartLibraryTask(async () => ShowCleanup(await maintenance.ClearCacheAsync(plan))));
    }

    private void ShowCleanup(CleanupResult result) =>
        ShowError("清理结果", $"已清理 {result.Removed} 个文件 · {FormatSize(result.Bytes)}" +
            (result.Retained.Length == 0 ? "" : "\n\n保留：\n" + string.Join("\n", result.Retained)));

    private void ShowAssetCategory(string category)
    {
        if (_category == "Trash" ^ category == "Trash") { ClearAssetSelection(); ClearAssetPreview(); }
        _category = category;
        _page = 0;
        Refresh();
        _workspace.ShowPanel("assets");
    }

    private void RefreshCollections()
    {
        var next = _database.Collections().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!_collections.Select(c => (c.Id, c.Name)).SequenceEqual(next.Select(c => (c.Id, c.Name))))
        {
            _collectionFilter.Clear();
            _collectionFilter.AddItem("查看集合…");
            foreach (var collection in next) _collectionFilter.AddItem(collection.Name);
        }
        if (_collectionFilter.ItemCount == 0) _collectionFilter.AddItem("查看集合…");
        var index = Array.FindIndex(next, c => c.Id == _category);
        if (index < 0 && _collections.Any(c => c.Id == _category)) { _category = "All"; _page = 0; }
        _collectionFilter.Select(index + 1);
        _collectionFilter.Disabled = next.Length == 0;
        _collections = next;
    }

    private void SelectOnly(int index)
    {
        _assetSelection.Clear();
        _assetAnchor = _items[index].Id;
        _assetSelection.Add(_assetAnchor);
        SyncAssetSelection();
        SelectAsset(index);
    }

    private void HandleAssetInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } context)
        {
            ShowAssetContextMenu(context.Position);
            return;
        }
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse) return;
        var index = _list.GetItemAtPosition(mouse.Position, false);
        if (index < 0 || index >= _items.Count) return;
        var epoch = _selectionEpoch;
        _mouseEpoch = epoch;
        var control = mouse.CtrlPressed || mouse.MetaPressed;
        var shift = mouse.ShiftPressed;
        Callable.From(() =>
        {
            if (epoch != _selectionEpoch) return;
            ApplyAssetGesture((int)index, control, shift);
            _mouseEpoch = -1;
        }).CallDeferred();
    }

    private void ShowAssetContextMenu(Vector2 localPosition)
    {
        var index = _list.GetItemAtPosition(localPosition, false);
        if (index < 0 || index >= _items.Count) return;
        _list.AcceptEvent();
        if (!_assetSelection.Contains(_items[index].Id)) SelectOnly(index);

        _assetContextMenu.Clear();
        if (_category == "Trash")
        {
            _assetContextMenu.AddItem("恢复所选…", RestoreAssetsAction);
            _assetContextMenu.AddItem("永久删除所选…", PurgeAssetsAction);
        }
        else _assetContextMenu.AddItem(TrashAssetsLabel, TrashAssetsAction);

        var parentPosition = GetWindow().GetFinalTransform() * (_list.GetGlobalTransformWithCanvas() * localPosition);
        _assetContextMenu.ResetSize();
        _assetContextMenu.PopupOnParent(new Rect2I((Vector2I)parentPosition, Vector2I.One));
    }

    private void ApplyAssetGesture(int index, bool control, bool shift)
    {
        var asset = _items[index];
        if (shift && _items.FindIndex(a => a.Id == _assetAnchor) is var anchor && anchor >= 0)
        {
            foreach (var a in _items.Skip(Math.Min(anchor, index)).Take(Math.Abs(anchor - index) + 1)) _assetSelection.Add(a.Id);
        }
        else
        {
            if (!control) _assetSelection.Clear();
            if (!control || !_assetSelection.Remove(asset.Id)) _assetSelection.Add(asset.Id);
            _assetAnchor = asset.Id;
        }
        SyncAssetSelection();
        if (_assetSelection.Contains(asset.Id) && !asset.Trashed) SelectAsset(index);
        else if (_selected?.Id == asset.Id) ClearAssetPreview();
    }

    private void QueueNativeAssetSelection()
    {
        var epoch = _selectionEpoch;
        Callable.From(() =>
        {
            if (epoch != _selectionEpoch || _mouseEpoch == epoch) return;
            foreach (var item in _items) _assetSelection.Remove(item.Id);
            var indices = _list.GetSelectedItems();
            foreach (var index in indices) _assetSelection.Add(_items[index].Id);
            if (indices.Length > 0)
            {
                var primary = indices[^1];
                _assetAnchor = _items[primary].Id;
                if (!_items[primary].Trashed) SelectAsset(primary);
            }
            else if (_selected != null && !_assetSelection.Contains(_selected.Id)) ClearAssetPreview();
            UpdateAssetSelectionCount();
        }).CallDeferred();
    }

    private void SyncAssetSelection()
    {
        _selectionEpoch++;
        _list.DeselectAll();
        for (int i = 0; i < _items.Count; i++)
            if (_assetSelection.Contains(_items[i].Id)) _list.Select(i, false);
        UpdateAssetSelectionCount();
    }

    private void UpdateAssetSelectionCount()
    {
        var hidden = _assetSelection.Count - _items.Count(a => _assetSelection.Contains(a.Id));
        _selectionCount.Text = $"已选 {_assetSelection.Count}" + (hidden > 0 ? $" · 其他页或筛选外 {hidden}" : "");
    }

    private void ClearAssetSelection()
    {
        _assetSelection.Clear();
        _assetAnchor = null;
        _mouseEpoch = -1;
        _selectionEpoch++;
    }

    private void ClearAssetPreview()
    {
        _selected = null;
        _previewAssetId = "";
        _preview.Clear();
        _clips.Clear();
        _clips.Disabled = true;
        _title.Text = _previewName.Text = "选择一个资产";
        _detail.Text = "格式、骨架与动画信息显示在这里。";
        _tags.Text = "";
        _exportSummary.Text = "导出当前选中的资产。";
        _commands?.ForgetSelection();
        UpdateTransport();
    }

    private void RefreshCurrentAsset()
    {
        if (_selected == null) return;
        var current = _database.Find(_selected.Id);
        if (current == null) { ClearAssetPreview(); return; }
        if (current.Tags != _selected.Tags) _tags.Text = current.Tags;
        _selected = current;
        _title.Text = current.Name;
        _favorite.Text = current.Favorite ? "已收藏" : "收藏";
        if (_previewAssetId == current.Id)
        {
            _previewName.Text = current.Name;
            _exportSummary.Text = "导出资产：" + current.Name;
        }
    }

    private void BuildAssetActions(Container parent)
    {
        _selectionCount = Label("已选 0", true, 12);
        parent.AddChild(_selectionCount);
        _assetActions = new MenuButton { Text = "整理", FocusMode = FocusModeEnum.All };
        parent.AddChild(_assetActions);
        _assetActions.AboutToPopup += FillAssetMenu;
        _assetActions.GetPopup().IdPressed += id => LibraryAction(() => AssetAction((int)id));
        _assetContextMenu = new PopupMenu { Name = "AssetContextMenu" };
        AddChild(_assetContextMenu);
        _assetContextMenu.IdPressed += id => LibraryAction(() => AssetAction((int)id));
    }

    private void FillAssetMenu()
    {
        var menu = _assetActions.GetPopup();
        menu.Clear();
        void Add(string text, int id, bool enabled) { menu.AddItem(text, id); menu.SetItemDisabled(menu.GetItemIndex(id), !enabled); }
        bool selected = _assetSelection.Count > 0;
        Add("清空选择", 0, selected);
        Add("选择当前页", 1, _items.Count > 0);
        if (_category == "Trash")
        {
            Add("恢复所选…", RestoreAssetsAction, selected);
            Add("永久删除所选…", PurgeAssetsAction, selected);
            return;
        }
        Add("重命名…", 4, _assetSelection.Count == 1);
        Add("批量添加标签…", 5, selected);
        Add("批量移除标签…", 6, selected);
        Add("收藏所选…", 7, selected);
        Add("取消所选收藏…", 8, selected);
        Add(TrashAssetsLabel, TrashAssetsAction, selected);
        menu.AddSeparator();
        Add("新建集合…", 10, _api != null);
        Add("重命名当前集合…", 11, _collections.Any(c => c.Id == _category));
        Add("删除当前集合…", 12, _collections.Any(c => c.Id == _category));
        foreach (var (collection, index) in _collections.Select((c, i) => (c, i)))
        {
            Add("加入 " + collection.Name + "…", 100 + 2 * index, selected);
            Add("移出 " + collection.Name + "…", 101 + 2 * index, selected);
        }
    }

    private void AssetAction(int action)
    {
        var ids = _assetSelection.ToArray();
        var api = _libraries.Active?.Api ?? throw new InvalidOperationException("请先打开一个库。");
        string scope = _selectionCount.Text;
        void Apply(Action edit, bool reset = false)
        {
            if (_api != api) throw new InvalidOperationException("已切换库，请重新选择资产。");
            edit();
            if (reset) ClearAssetSelection();
            Refresh();
            SetStatus("资产整理已保存");
        }
        switch (action)
        {
            case 0: ClearAssetSelection(); SyncAssetSelection(); ClearAssetPreview(); break;
            case 1: _assetSelection.UnionWith(_items.Select(a => a.Id)); SyncAssetSelection(); break;
            case RestoreAssetsAction: ShowApproval("恢复资产", scope, () => Apply(() => api.SetTrash(ids, false), true)); break;
            case PurgeAssetsAction:
                ShowApproval("永久删除资产", scope + "\n此操作无法从回收站恢复。共享对象及保留修订继续保存。",
                    () => StartLibraryTask(async () =>
                    {
                        var result = await new LibraryMaintenance(api).PurgeAsync(ids);
                        ClearAssetSelection(); Refresh(); ShowCleanup(result);
                    }));
                break;
            case 4:
                var asset = api.Find(ids.Single());
                ShowEntry("重命名资产", "显示名称", asset.Name, null, "", false, (name, _) => Apply(() => api.RenameAsset(asset.Id, name)));
                break;
            case 5:
            case 6:
                ShowEntry(action == 5 ? "添加标签" : "移除标签", scope + " · 多个标签用空格或逗号分隔", "", null, "", false,
                    (tags, _) => Apply(() => api.ChangeTags(ids, tags, action == 5)));
                break;
            case 7:
            case 8:
                ShowApproval(action == 7 ? "收藏所选" : "取消所选收藏", scope, () => Apply(() => api.SetFavorites(ids, action == 7)));
                break;
            case TrashAssetsAction: ShowApproval("移入回收站", scope, () => Apply(() => api.SetTrash(ids, true), true)); break;
            case 10:
                ShowEntry("新建集合", "集合名称", "", null, "", false, (name, _) => Apply(() => api.CreateCollection(name)));
                break;
            case 11:
                var collection = _collections.Single(c => c.Id == _category);
                ShowEntry("重命名集合", "集合名称", collection.Name, null, "", false, (name, _) => Apply(() => api.RenameCollection(collection.Id, name)));
                break;
            case 12:
                var removing = _collections.Single(c => c.Id == _category);
                ShowApproval("删除集合", removing.Name, () => Apply(() => api.DeleteCollection(removing.Id)));
                break;
            default:
                var membership = _collections[(action - 100) / 2];
                var add = action % 2 == 0;
                ShowApproval((add ? "加入 " : "移出 ") + membership.Name, scope,
                    () => Apply(() => api.SetCollectionMembership(membership.Id, ids, add)));
                break;
        }
    }
}
