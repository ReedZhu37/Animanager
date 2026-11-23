using Animanager.Core;
using Animanager.Core.Models;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI.Components;

internal sealed partial class ImportReviewDialog : AcceptDialog
{
    private readonly WorkspaceApi _api;
    private readonly AssetItem[] _assets;
    private readonly TreeItem[] _rows;
    private readonly Dictionary<string, string[]> _draft = new();
    private readonly Tree _animations;
    private readonly CheckBox _selectAll;
    private readonly Label _selectionCount, _status, _previewStatus, _time;
    private readonly LineEdit _tags;
    private readonly Button _add, _clear, _play;
    private readonly OptionButton _clips;
    private readonly HSlider _timeline;
    private readonly Preview3DViewport _preview;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _previewRequest;
    private AssetItem? _previewedAsset;

    public Task Finished => _finished.Task;

    public ImportReviewDialog(WorkspaceApi api, AssetItem[] assets)
    {
        _api = api;
        _assets = assets;
        Name = "ImportReview";
        Title = "导入 · 预览与打标签";
        OkButtonText = "保存标签";
        DialogHideOnOk = false;
        Transient = true;
        Exclusive = true;
        AddCancelButton("跳过").Name = "SkipImportReview";
        GetOkButton().Name = "SaveImportTags";
        GetOkButton().ThemeTypeVariation = "PrimaryButton";
        Confirmed += SaveTags;

        var content = Column(this);
        content.AddChild(Label("点击名称预览，勾选要添加标签的动画。", true, 13));
        var split = Row(content);
        split.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        split.AddThemeConstantOverride("separation", 12);
        var list = Column(split);
        var selection = Row(list);
        _selectAll = new CheckBox { Name = "SelectAllImportAnimations", Text = "全选" };
        selection.AddChild(_selectAll);
        _selectionCount = Label("", true, 12);
        _selectionCount.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        selection.AddChild(_selectionCount);

        _animations = new Tree
        {
            Name = "ImportAnimations", Columns = 3, ColumnTitlesVisible = true, HideRoot = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(172, 100)
        };
        _animations.SetColumnExpand(0, false);
        _animations.SetColumnCustomMinimumWidth(0, 28);
        _animations.SetColumnTitle(1, "动画");
        _animations.SetColumnTitle(2, "待添加标签");
        _animations.SetColumnCustomMinimumWidth(2, 65);
        list.AddChild(_animations);
        var root = _animations.CreateItem();
        _rows = new TreeItem[assets.Length];
        for (int index = 0; index < assets.Length; index++)
        {
            var asset = assets[index];
            var row = _rows[index] = _animations.CreateItem(root);
            row.SetCellMode(0, TreeItem.TreeCellMode.Check);
            row.SetEditable(0, true);
            row.SetSelectable(0, false);
            row.SetText(1, asset.Name);
            row.SetTooltipText(1, asset.Tags.Length > 0 ? $"{asset.Name}\n已有标签  {asset.Tags}" : asset.Name);
            row.SetMetadata(1, index);
        }
        _selectAll.Toggled += all =>
        {
            foreach (var row in _rows) row.SetChecked(0, all);
            UpdateActions();
        };
        _animations.ItemEdited += UpdateActions;
        _animations.ItemSelected += () =>
        {
            if (_animations.GetSelected() is { } row) _ = PreviewAsync(_assets[row.GetMetadata(1).AsInt32()]);
        };

        var previewPanel = Column(split);
        previewPanel.SizeFlagsStretchRatio = 1.25f;
        var previewHeader = Row(previewPanel);
        _previewStatus = CompactLabel("");
        _previewStatus.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _previewStatus.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        previewHeader.AddChild(_previewStatus);
        _preview = new Preview3DViewport { Name = "ImportPreviewViewport", ResetCameraOnAnimationChange = true };
        previewPanel.AddChild(_preview);
        _clips = new OptionButton { Name = "ImportPreviewClips", FitToLongestItem = false, Visible = false,
            CustomMinimumSize = new(80, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _clips.ItemSelected += index =>
        {
            bool playing = _preview.Playing;
            _preview.SelectClip(_clips.GetItemText((int)index));
            if (playing) _preview.TogglePlay();
            UpdateTransport();
        };
        previewHeader.AddChild(_clips);
        var transport = Row(previewPanel);
        _play = Button("播放", () => { _preview.TogglePlay(); UpdateTransport(); });
        _play.Name = "PlayImportAnimation";
        transport.AddChild(_play);
        _timeline = new HSlider { Name = "ImportPreviewTimeline", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Step = .001 };
        _timeline.ValueChanged += value => { _preview.Seek(value); UpdateTransport(); };
        transport.AddChild(_timeline);
        _time = Label("", true, 12);
        _time.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        transport.AddChild(_time);

        var tagging = Row(content);
        _tags = Input("标签（空格或逗号分隔）");
        _tags.Name = "ImportTags";
        tagging.AddChild(_tags);
        _add = Button("添加到勾选", StageTags);
        _add.Name = "AddImportTags";
        tagging.AddChild(_add);
        _clear = Button("清空待添加", ClearDraft);
        _clear.Name = "ClearImportTags";
        _clear.TooltipText = "清空勾选动画的待添加标签。";
        tagging.AddChild(_clear);
        _tags.TextChanged += _ => UpdateActions();
        _tags.TextSubmitted += _ => { if (!_add.Disabled) StageTags(); };
        _status = CompactLabel("");
        content.AddChild(_status);

        VisibilityChanged += () =>
        {
            if (Visible) return;
            _previewRequest?.Cancel();
            if (_preview.IsNodeReady()) _preview.Clear();
            _finished.TrySetResult();
        };
        UpdateActions();
    }

    public void SelectFirst()
    {
        _rows[0].Select(1);
        _ = PreviewAsync(_assets[0]);
    }

    public override void _Ready() => _preview.CustomMinimumSize = new(148, 96);

    public override void _Process(double delta)
    {
        if (_preview.Playing) UpdateTransport();
    }

    public override void _ExitTree() => _previewRequest?.Cancel();

    private static Label CompactLabel(string text)
    {
        var label = Label(text, true, 12);
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return label;
    }

    private async Task PreviewAsync(AssetItem asset)
    {
        if (!Visible || _previewedAsset == asset) return;
        _previewedAsset = asset;
        _previewRequest?.Cancel();
        using var request = new CancellationTokenSource();
        _previewRequest = request;
        var ct = request.Token;
        _preview.Clear();
        _clips.Clear();
        _clips.Hide();
        UpdateTransport();
        SetPreviewStatus("正在加载 · " + asset.Name);
        try
        {
            var path = await _api.Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
            using var source = await PreviewSource.LoadAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            _preview.AttachSource(source, notify: false);
            _preview.Bones.ShowSkeleton(_preview.MeshCount == 0);
            foreach (var clip in _preview.Clips) _clips.AddItem(clip);
            _clips.Visible = _clips.ItemCount > 1;
            _clips.Select(Array.IndexOf(_preview.Clips, _preview.CurrentClip));
            SetPreviewStatus(_preview.Clips.Length > 0 ? asset.Name : "没有可播放的片段 · " + asset.Name);
            UpdateTransport();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) return;
            _preview.Clear();
            SetPreviewStatus("无法预览 · " + error.Message);
            UpdateTransport();
        }
        finally { if (_previewRequest == request) _previewRequest = null; }
    }

    private void SetPreviewStatus(string text) => _previewStatus.Text = _previewStatus.TooltipText = text;

    private void UpdateTransport()
    {
        _play.Text = _preview.Playing ? "暂停" : "播放";
        _play.Disabled = _preview.CurrentClip.Length == 0;
        _timeline.Editable = !_play.Disabled;
        _timeline.SetValueNoSignal(_preview.Playhead);
        _timeline.MaxValue = Math.Max(.001, _preview.Duration);
        _timeline.SetValueNoSignal(_preview.Playhead);
        _time.Text = $"{_preview.Playhead:0.0} / {_preview.Duration:0.0} 秒";
    }

    private IEnumerable<int> CheckedRows() => Enumerable.Range(0, _rows.Length).Where(index => _rows[index].IsChecked(0));

    private void UpdateActions()
    {
        var checkedRows = CheckedRows().ToArray();
        _selectAll.SetPressedNoSignal(checkedRows.Length == _assets.Length);
        _selectionCount.Text = $"已勾选 {checkedRows.Length} / {_assets.Length}";
        bool hasInput = WorkspaceApi.ParseTags(_tags.Text).Any();
        _add.Disabled = checkedRows.Length == 0 || !hasInput;
        _clear.Disabled = !checkedRows.Any(index => _draft.ContainsKey(_assets[index].Id));
        GetOkButton().Disabled = _draft.Count == 0 || hasInput;
        SetStatus(hasInput ? "将输入的标签添加到勾选项后即可保存。" : $"{_draft.Count} 个动画有待保存的标签");
    }

    private void StageTags()
    {
        var tags = WorkspaceApi.ParseTags(_tags.Text).ToArray();
        foreach (int index in CheckedRows())
        {
            var asset = _assets[index];
            var draft = _draft.GetValueOrDefault(asset.Id) ?? [];
            var additions = draft.Union(tags, StringComparer.OrdinalIgnoreCase)
                .Except(WorkspaceApi.ParseTags(asset.Tags), StringComparer.OrdinalIgnoreCase).ToArray();
            if (additions.Length > 0) _draft[asset.Id] = additions;
            SetDraftLabel(index, string.Join(" ", additions));
        }
        _tags.Clear();
        UpdateActions();
    }

    private void ClearDraft()
    {
        foreach (int index in CheckedRows())
        {
            _draft.Remove(_assets[index].Id);
            SetDraftLabel(index, "");
        }
        UpdateActions();
    }

    private void SetDraftLabel(int index, string text)
    {
        _rows[index].SetText(2, text);
        _rows[index].SetTooltipText(2, text);
    }

    private void SaveTags()
    {
        if (GetOkButton().Disabled) return;
        try
        {
            _api.AddAssetTags(_draft);
            Hide();
        }
        catch (Exception error) { SetStatus("标签保存失败 · " + error.Message); }
    }

    private void SetStatus(string text) => _status.Text = _status.TooltipText = text;
}
