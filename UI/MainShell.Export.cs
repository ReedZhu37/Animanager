using System.Globalization;
using Animanager.Core.Jobs;
using Animanager.Core.Pipeline;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private OptionButton _exportScope = null!, _exportClipMode = null!;
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
        _exportScope.ItemSelected += _ => SyncExportControls();
        column.AddChild(_exportScope);
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

    private void SyncExportControls()
    {
        if (_export == null || _closing) return;
        int scope = _exportScope.Selected;
        int count = _api == null || _category == "Trash" ? 0 : scope switch
        {
            1 => _selected == null ? 0 : 1,
            2 => FilteredAssetCount,
            _ => _assetSelection.Count
        };
        _exportSummary.Text = scope == 1 && count > 0 ? _selected!.Name
            : count == 0 ? "此范围内没有可导出的资产。" : $"{count} 个资产";
        _exportMesh.Visible = (JobKind)_exportType.GetSelectedId() != JobKind.ExportRes;

        var preview = _previewAssetId.Length > 0 && _previewAssetId == _selected?.Id ? _preview : null;
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
        if (_api == null || _export.Disabled || _exportCancellation != null) return;
        var api = _api;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SyncExportControls();
        try
        {
            using var activity = api.BeginActivity();
            int scope = _exportScope.Selected;
            var kind = (JobKind)_exportType.GetSelectedId();
            var options = new ExportOptions
            {
                IncludeMesh = _exportMesh.ButtonPressed,
                Clips = _exportClipMode.Selected == 1 ? _exportClipList.GetSelectedItems().Select(_exportClipList.GetItemText).ToArray() : null,
                StartSeconds = ExportNumber(_exportStart, "起始时间"),
                EndSeconds = ExportNumber(_exportEnd, "结束时间"),
                SampleFps = ExportNumber(_exportFps, "采样帧率")
            }.Snapshot(kind == JobKind.ExportRes);

            BatchPlan plan;
            if (scope == 1) plan = api.Planner.ExportSingle(_selected!.Id, folder, kind, options);
            else
            {
                var ids = scope == 2 ? (await GetAssetsForCurrentFilterAsync(cancellation.Token)).Select(asset => asset.Id).ToArray()
                    : _assetSelection.ToArray();
                if (_closing || _api != api) return;
                plan = api.Planner.ExportSelection(ids, folder, kind, options, scope == 2 ? "当前筛选结果" : "已选资产");
            }

            var id = api.Jobs.Enqueue(plan);
            SetStatus($"{JobKindName(kind)}已加入队列 · {plan.Items.Count} 项 · {id[..8]}");
            using var cancelJob = cancellation.Token.Register(() => api.Jobs.Cancel(id));
            var result = await api.Jobs.WaitAsync(id);
            if (_closing || _api != api) return;
            if (result.Items.FirstOrDefault(item => item.State == ItemState.Succeeded) is { } item) _lastExport = item.Output;
            SetStatus($"{JobKindName(kind)} · {JobStateName(result.State)} · 成功 {result.Succeeded} · 失败 {result.Failed} · {id[..8]}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing && _api == api) ShowError("导出失败", error.Message);
        }
        finally
        {
            _exportCancellation = null;
            if (!_closing && _api == api) SyncExportControls();
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
