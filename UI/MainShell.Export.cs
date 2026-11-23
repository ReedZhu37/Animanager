using System.Globalization;
using Animanager.Core.Jobs;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private const int CollectionExportScope = 3;
    private OptionButton _exportScope = null!, _exportClipMode = null!;
    private OptionButton _exportCollectionChoice = null!;
    private CheckBox _exportMesh = null!;
    private ItemList _exportClipList = null!;
    private Label _exportClipHelp = null!;
    private LineEdit _exportStart = null!, _exportEnd = null!, _exportFps = null!;
    private string[]? _exportAvailableClips;

    private Control BuildConfiguredExport()
    {
        var column = new VBoxContainer();
        column.AddChild(Label("导出范围", true, 12));
        _exportScope = new OptionButton { Name = "ExportScope", FitToLongestItem = false };
        _exportScope.AddItem("已选资产（含其他页）");
        _exportScope.AddItem("当前资产");
        _exportScope.AddItem("当前筛选结果");
        _exportScope.AddItem("集合（含子集合）");
        _exportScope.ItemSelected += _ => SyncExportControls();
        column.AddChild(_exportScope);
        _exportCollectionChoice = new OptionButton { Name = "ExportCollectionChoice", FitToLongestItem = false, Visible = false };
        _exportCollectionChoice.ItemSelected += _ => SyncExportControls();
        column.AddChild(_exportCollectionChoice);
        RefreshExportCollectionChoices();
        _exportSummary = Wrapped("选择要导出的资产。");
        column.AddChild(_exportSummary);

        column.AddChild(Label("输出格式", true, 12));
        _exportType = new OptionButton { Name = "ExportFormat", FitToLongestItem = false };
        _exportType.AddItem("Godot 动画库 · .res", (int)JobKind.ExportRes);
        _exportType.AddItem("通用场景 · .glb", (int)JobKind.ExportGlb);
        _exportType.AddItem("FBX · Blender", (int)JobKind.ExportFbx);
        _exportType.Select(2);
        _exportType.ItemSelected += _ => SyncExportControls();
        column.AddChild(_exportType);
        _exportMesh = new CheckBox { Name = "ExportIncludeMesh", Text = "包含网格", ButtonPressed = true };
        column.AddChild(_exportMesh);
        column.AddChild(Button("配置 Retarget…", () => _workspace.ShowPanel("retarget")));

        column.AddChild(Label("动画片段", true, 12));
        _exportClipMode = new OptionButton { Name = "ExportClipMode", FitToLongestItem = false };
        _exportClipMode.AddItem("每项全部片段");
        _exportClipMode.AddItem("指定片段");
        _exportClipMode.ItemSelected += _ => SyncExportControls();
        column.AddChild(_exportClipMode);
        _exportClipHelp = Wrapped("");
        column.AddChild(_exportClipHelp);
        _exportClipList = new ItemList
        {
            Name = "ExportClips", SelectMode = ItemList.SelectModeEnum.Multi, CustomMinimumSize = new(0, 88),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _exportClipList.MultiSelected += (_, _) => SyncExportControls();
        column.AddChild(_exportClipList);

        LineEdit Number(string name, string label, string placeholder)
        {
            var edit = Input(placeholder);
            edit.Name = name;
            column.AddChild(Label(label, true, 12));
            column.AddChild(edit);
            return edit;
        }
        _exportStart = Number("ExportStart", "起始时间（秒）", "默认从 0 开始");
        _exportEnd = Number("ExportEnd", "结束时间（秒）", "默认到片段末尾");
        _exportFps = Number("ExportSampleFps", "采样帧率（FPS）", "默认使用源采样");

        column.AddChild(Label("目标目录", true, 12));
        _exportPath = Input("绝对路径");
        _exportPath.Name = "ExportFolder";
        _exportPath.Text = ProjectSettings.GlobalizePath("user://Exports");
        column.AddChild(_exportPath);
        var actions = new HFlowContainer();
        column.AddChild(actions);
        actions.AddChild(Button("浏览…", () => _output.PopupCenteredRatio(.7f)));
        _export = Button("排队导出", () => Export(_exportPath.Text), true);
        _export.Name = "QueueExport";
        actions.AddChild(_export);
        actions.AddChild(Button("查看任务", () => _workspace.ShowPanel("jobs")));
        column.AddChild(Button("打开最近输出目录", () =>
        {
            if (_lastExport.Length > 0) OS.ShellOpen(Path.GetDirectoryName(_lastExport)!);
            else SetStatus("还没有导出结果。");
        }));
        SyncExportControls();
        return column;
    }

    private void RefreshExportCollectionChoices()
    {
        string selectedId = _exportCollectionChoice.GetSelectedMetadata().AsString();
        _exportCollectionChoice.Clear();
        var byId = _collections.ToDictionary(item => item.Id);
        foreach (var collection in _collections)
        {
            var names = new Stack<string>();
            for (AppCollection? item = collection; item != null; item = byId.GetValueOrDefault(item.ParentId ?? ""))
                names.Push(item.Name);
            _exportCollectionChoice.AddItem(string.Join(" / ", names));
            _exportCollectionChoice.SetItemMetadata(_exportCollectionChoice.ItemCount - 1, collection.Id);
        }
        int index = Array.FindIndex(_collections, item => item.Id == selectedId);
        if (_collections.Length > 0) _exportCollectionChoice.Select(Math.Max(0, index));
    }

    private AppCollection? SelectedExportCollection() =>
        _collections.FirstOrDefault(item => item.Id == _exportCollectionChoice.GetSelectedMetadata().AsString());

    private void OpenCollectionExport(AppCollection collection)
    {
        _exportScope.Select(CollectionExportScope);
        _exportCollectionChoice.Select(Array.FindIndex(_collections, item => item.Id == collection.Id));
        SyncExportControls();
        _workspace.ShowPanel("export");
    }

    private void SyncExportControls()
    {
        if (_export == null || _closing) return;
        int scope = _exportScope.Selected;
        bool collectionScope = scope == CollectionExportScope;
        _exportCollectionChoice.Visible = collectionScope;
        var collection = collectionScope ? SelectedExportCollection() : null;
        var members = collection == null ? [] : ExportCollectionMembers(collection, _collections);
        int availableMembers = members.Count(member => _libraries.FindOpenLibrary(member.LibraryId) != null);
        int count = _libraryLoading || (!collectionScope && _category == "Trash") ? 0 : scope switch
        {
            1 => _selectedLibrary != null ? 1 : 0,
            2 => _onlineFilteredAssetCount,
            CollectionExportScope => availableMembers,
            _ => CanEditAssetSelection() ? _assetSelection.Count : 0
        };
        _exportSummary.Text = scope == 1 && count > 0 ? _selected!.Name
            : count == 0 ? "此范围内没有可导出的资产。" : $"{count} 个资产";
        if (availableMembers < members.Length) _exportSummary.Text += $"\n来源库未打开 · {members.Length - availableMembers} 项";
        if (_retargetEnabled?.ButtonPressed == true) _exportSummary.Text += "\nRetarget → " + RetargetCharacter.Name;
        if (_exportFps != null) _exportFps.PlaceholderText = _retargetEnabled?.ButtonPressed == true ? "默认自动选择；独立武器 600 FPS" : "默认使用源采样";
        _exportMesh.Visible = (JobKind)_exportType.GetSelectedId() != JobKind.ExportRes;

        var preview = _selectedLibrary != null && _previewAssetId == _selectedKey ? _preview : null;
        var available = preview?.Clips;
        if (!ReferenceEquals(_exportAvailableClips, available))
        {
            _exportAvailableClips = available;
            var previous = _exportClipList.GetSelectedItems().Select(_exportClipList.GetItemText).ToHashSet(StringComparer.Ordinal);
            _exportClipList.Clear();
            foreach (string clip in available ?? [])
            {
                int index = _exportClipList.AddItem(clip);
                if (preview?.Player is { } player)
                    _exportClipList.SetItemTooltip(index, $"{clip} · {player.GetAnimation(clip).Length:G7} 秒");
                if (previous.Contains(clip)) _exportClipList.Select(index, false);
            }
            if (preview != null && _exportClipList.ItemCount > 0 && _exportClipList.GetSelectedItems().Length == 0)
            {
                int current = Array.IndexOf(available!, preview.CurrentClip);
                _exportClipList.Select(Math.Max(0, current), false);
            }
        }
        bool chooseClips = _exportClipMode.Selected == 1;
        _exportClipList.Visible = chooseClips && _exportClipList.ItemCount > 0;
        _exportClipHelp.Visible = chooseClips && (_exportClipList.ItemCount == 0 || scope != 1);
        _exportClipHelp.Text = _exportClipList.ItemCount == 0 ? "请先预览含动画的资产。" : "每项应用所选片段名。";
        _export.Disabled = count == 0 || _exportCancellation != null || (chooseClips && _exportClipList.GetSelectedItems().Length == 0);
    }

    private async void Export(string folder)
    {
        if (_libraryLoading || _export.Disabled || _exportCancellation != null) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SyncExportControls();
        var activities = new List<IDisposable>();
        try
        {
            int scope = _exportScope.Selected;
            int skipped = 0;
            string selection = scope == 2 ? "当前筛选结果" : "已选资产";
            var (browseScopes, query, filter, tags) = _publishedBrowseScope;
            var selected = scope == 1
                ? [(_selectedLibrary!, Ids: new[] { _selected!.Id })]
                : _assetSelection.GroupBy(SelectionLibrary).Select(group => (group.Key!, Ids: group.Select(SelectionAssetId).ToArray())).ToArray();
            if (scope == CollectionExportScope)
            {
                var collection = SelectedExportCollection() ?? throw new InvalidOperationException("请选择要导出的集合。");
                selection = "集合 · " + collection.Name;
                var members = ExportCollectionMembers(collection, _collections);
                selected = members.GroupBy(member => member.LibraryId)
                    .Select(group => (_libraries.FindOpenLibrary(group.Key), Ids: group.Select(member => member.AssetId).ToArray()))
                    .Where(group => group.Item1 != null)
                    .Select(group => (group.Item1!, group.Ids)).ToArray();
                skipped = members.Length - selected.Sum(group => group.Ids.Length);
            }
            var sources = scope == 2 ? browseScopes.Select(source => source.Library) : selected.Select(group => group.Item1);
            foreach (var library in sources) activities.Add(library.Api.BeginActivity());
            var kind = (JobKind)_exportType.GetSelectedId();
            var options = new ExportOptions
            {
                IncludeMesh = _exportMesh.ButtonPressed,
                Clips = _exportClipMode.Selected == 1 ? _exportClipList.GetSelectedItems().Select(_exportClipList.GetItemText).ToArray() : null,
                StartSeconds = ExportNumber(_exportStart, "起始时间"),
                EndSeconds = ExportNumber(_exportEnd, "结束时间"),
                SampleFps = ExportNumber(_exportFps, "采样帧率"),
                Retarget = _retargetEnabled?.ButtonPressed == true ? ReadRetargetOptions() : null
            }.Snapshot(kind == JobKind.ExportRes);

            if (scope == 2)
            {
                selected = _publishedMotionReference != null
                    ? _motionRows.GroupBy(m => m.Row.Library).Select(group => (group.Key, group.Select(m => m.Row.Asset.Id).ToArray())).ToArray()
                    : await Task.WhenAll(browseScopes.Select(async source => (source.Library, Ids:
                    (await source.Library.Api.GetMatchingAssetsAsync(query, source.Category, filter, cancellation.Token, tags, source.MemberIdsJson))
                    .Select(asset => asset.Id).ToArray())));
            }

            if (scope == CollectionExportScope)
            {
                int before = selected.Sum(group => group.Ids.Length);
                selected = selected.Select(group => (group.Item1, Ids: group.Item1.Api.Database.ExistingIds(group.Ids, trash: false))).ToArray();
                skipped += before - selected.Sum(group => group.Ids.Length);
            }

            if (_closing) return;
            var plans = selected.Where(group => group.Ids.Length > 0).Select(group =>
            {
                var api = group.Item1.Api;
                var plan = scope == 1 ? api.Planner.ExportSingle(group.Ids[0], folder, kind, options)
                    : api.Planner.ExportSelection(group.Ids, folder, kind, options, selection);
                return (api.Jobs, Plan: plan);
            }).ToArray();
            if (plans.Length == 0) throw new InvalidOperationException(skipped > 0
                ? $"集合中的 {skipped} 个资产不可用。请检查来源库和回收站。" : "没有可导出的匹配资产。");

            int succeeded = 0, failed = 0, cancelled = 0;
            foreach (var (jobs, plan) in plans)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var id = jobs.Enqueue(plan);
                SetStatus($"{JobKindName(kind)}已加入队列 · {plan.Items.Count} 项 · {id[..8]}");
                using var cancelJob = cancellation.Token.Register(() => jobs.Cancel(id));
                var result = await jobs.WaitAsync(id);
                if (_closing) return;
                if (result.Items.FirstOrDefault(item => item.State == ItemState.Succeeded) is { } item) _lastExport = item.Output;
                succeeded += result.Succeeded;
                failed += result.Failed;
                cancelled += result.Cancelled;
            }
            SetStatus($"{JobKindName(kind)} · 成功 {succeeded} · 失败 {failed}" + (cancelled > 0 ? $" · 取消 {cancelled}" : "") +
                (skipped > 0 ? $" · 跳过 {skipped} 个不可用资产" : ""));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing) ShowError("导出失败", error.Message);
        }
        finally
        {
            foreach (var activity in activities) activity.Dispose();
            _exportCancellation = null;
            if (!_closing) SyncExportControls();
        }
    }

    private static double? ExportNumber(LineEdit input, string label)
    {
        if (string.IsNullOrWhiteSpace(input.Text)) return null;
        if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return value;
        throw new ArgumentException("请输入有效的" + label + "。");
    }
}
