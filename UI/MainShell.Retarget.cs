using Animanager.Core.Retarget;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private OptionButton _retargetTarget = null!, _retargetView = null!, _retargetSaved = null!, _retargetBone = null!;
    private CheckBox _retargetEnabled = null!, _retargetInPlace = null!, _retargetProps = null!;
    private SpinBox _retargetScale = null!, _retargetHeight = null!;
    private SpinBox[] _retargetAngles = [];
    private Label _retargetInfo = null!;
    private LineEdit _retargetName = null!;
    private CancellationTokenSource? _retargetCancellation;
    private Dictionary<string, float[]> _retargetOffsets = new();
    private RetargetPreset[] _retargetPresets = [];
    private string _retargetOffsetBone = "pelvis";
    private string _retargetSourceRig = "";
    private string _retargetTargetHash = "", _retargetSourceHash = "";
    internal Task PendingRetargetPreview { get; private set; } = Task.CompletedTask;
    private bool _settingRetarget;

    private Control BuildRetarget()
    {
        var column = new VBoxContainer();
        column.AddChild(Wrapped("角色库中的兼容角色可用于预览与批量导出。脚底接触 IK 尚未启用。"));
        _retargetEnabled = new CheckBox { Name = "RetargetEnabled", Text = "导出时启用重定向" }; column.AddChild(_retargetEnabled);
        _retargetEnabled.Toggled += _ => SyncExportControls();
        _retargetTarget = new OptionButton { Name = "RetargetTarget", FitToLongestItem = false };
        RefreshCharacterChoices(); _retargetTarget.Select(1);
        _retargetTarget.ItemSelected += _ => { _retargetTargetHash = ""; SyncExportControls(); };
        column.AddChild(Label("目标角色", true, 12)); column.AddChild(_retargetTarget);
        _retargetInPlace = new CheckBox { Name = "RetargetInPlace", Text = "原地（保留高度与转向）" }; column.AddChild(_retargetInPlace);
        SpinBox Number(string name, string title, double min, double max, double step, double value)
        {
            column.AddChild(Label(title, true, 12));
            var spin = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddChild(spin); return spin;
        }
        _retargetScale = Number("RetargetRootScale", "根位移倍率", 0, 10, .05, 1);
        _retargetHeight = Number("RetargetHipHeight", "骨盆高度偏移（米）", -2, 2, .01, 0);
        _retargetProps = new CheckBox { Name = "RetargetProps", Text = "包含独立武器轨道", ButtonPressed = true }; column.AddChild(_retargetProps);
        _retargetProps.TooltipText = "独立武器默认以 600 FPS 烘焙，减少快速转刀的帧间误差。导出面板可指定其他采样帧率。";
        column.AddChild(Label("局部旋转补偿", true, 12));
        _retargetBone = new OptionButton { Name = "RetargetOffsetBone", FitToLongestItem = false };
        foreach (var (name, title) in new[] { ("pelvis", "骨盆"), ("spine_01", "躯干"), ("head", "头部"), ("upperarm_l", "左上臂"), ("upperarm_r", "右上臂"), ("hand_l", "左手"), ("hand_r", "右手"), ("foot_l", "左脚"), ("foot_r", "右脚") })
        { _retargetBone.AddItem(title); _retargetBone.SetItemMetadata(_retargetBone.ItemCount - 1, name); }
        column.AddChild(_retargetBone);
        var angleRow = new VBoxContainer(); column.AddChild(angleRow);
        _retargetAngles = new[] { "X", "Y", "Z" }.Select(axis =>
        {
            var spin = new SpinBox { Name = "RetargetOffset" + axis, Prefix = axis, Suffix = "°", MinValue = -180, MaxValue = 180, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            angleRow.AddChild(spin); spin.ValueChanged += _ => StoreRetargetAngles(); return spin;
        }).ToArray();
        _retargetBone.ItemSelected += _ => { _retargetOffsetBone = _retargetBone.GetSelectedMetadata().AsString(); DisplayRetargetAngles(); };
        _retargetView = new OptionButton { Name = "RetargetView", FitToLongestItem = false };
        foreach (string label in new[] { "源动画", "目标角色", "并排对比" }) _retargetView.AddItem(label);
        _retargetView.Select(1); _retargetView.ItemSelected += i => _preview.SetRetargetView((int)i); column.AddChild(_retargetView);
        var actions = new HFlowContainer(); column.AddChild(actions);
        var apply = Button("应用并预览", () => PendingRetargetPreview = ApplyRetargetPreview(), true); apply.Name = "RetargetApply"; actions.AddChild(apply);
        actions.AddChild(Button("取消计算", () => _retargetCancellation?.Cancel()));
        var restore = Button("恢复源动画", RestoreRetargetSource); restore.Name = "RetargetRestore"; actions.AddChild(restore);
        _retargetInfo = Wrapped("选择动画后应用配置。脚底及双手接触 IK 尚未启用。"); _retargetInfo.Name = "RetargetInfo"; column.AddChild(_retargetInfo);
        column.AddChild(new HSeparator());
        _retargetSaved = new OptionButton { Name = "RetargetPresets", FitToLongestItem = false }; column.AddChild(_retargetSaved);
        _retargetSaved.ItemSelected += i => { try { if (i > 0) SetRetargetOptions(_retargetPresets[(int)i - 1].Options.Snapshot()); } catch (Exception e) { _retargetInfo.Text = e.Message; } };
        _retargetName = Input("预设名称"); _retargetName.Name = "RetargetPresetName"; column.AddChild(_retargetName);
        var save = Button("保存预设与当前资产配置", SaveRetargetPreset); save.Name = "RetargetSave"; column.AddChild(save);
        column.AddChild(Button("前往导出", () => _workspace.ShowPanel("export")));
        _preview.SourceLoaded += () => { _retargetCancellation?.Cancel(); _retargetEnabled.ButtonPressed = false; };
        RefreshRetargetPresets();
        return column;
    }
    private RetargetOptions ReadRetargetOptions()
    {
        var entry = RetargetCharacter;
        return new RetargetOptions
        {
            TargetRig = entry.Family, Character = entry.Reference, SourceRig = _retargetSourceRig,
            TargetHash = _retargetTargetHash, SourceHash = _retargetSourceHash,
            InPlace = _retargetInPlace.ButtonPressed, RootScale = (float)_retargetScale.Value, HipHeight = (float)_retargetHeight.Value,
            IncludeProps = _retargetProps.ButtonPressed, RotationOffsets = _retargetOffsets.ToDictionary(p => p.Key, p => p.Value.ToArray())
        }.Snapshot();
    }
    private void SetRetargetOptions(RetargetOptions options)
    {
        options = options.Snapshot(); SetRetargetCharacter(options.Character?.Id ?? options.TargetRig);
        _retargetTargetHash = options.TargetHash; _retargetSourceHash = options.SourceHash;
        _retargetInPlace.ButtonPressed = options.InPlace; _retargetScale.Value = options.RootScale; _retargetHeight.Value = options.HipHeight;
        _retargetProps.ButtonPressed = options.IncludeProps; _retargetSourceRig = options.SourceRig;
        _retargetOffsets = options.RotationOffsets; _retargetEnabled.ButtonPressed = true; DisplayRetargetAngles();
    }
    private void StoreRetargetAngles()
    {
        if (_settingRetarget || _retargetAngles.Length != 3) return;
        var values = _retargetAngles.Select(v => (float)v.Value).ToArray();
        if (values.All(v => v == 0)) _retargetOffsets.Remove(_retargetOffsetBone); else _retargetOffsets[_retargetOffsetBone] = values;
    }
    private void DisplayRetargetAngles()
    {
        _settingRetarget = true; var values = _retargetOffsets.GetValueOrDefault(_retargetOffsetBone) ?? new float[3];
        for (int i = 0; i < 3; i++) _retargetAngles[i].Value = values[i]; _settingRetarget = false;
    }
    private void RefreshRetargetPresets()
    {
        if (_retargetSaved == null) return;
        try { _retargetPresets = _api?.RetargetPresets.List() ?? []; }
        catch (Exception e) { _retargetPresets = []; _retargetInfo.Text = "预设读取失败：" + e.Message; }
        _retargetSaved.Clear(); _retargetSaved.AddItem("选择已保存预设…");
        foreach (var preset in _retargetPresets) _retargetSaved.AddItem(preset.Name);
    }
    private void LoadAssetRetargetConfiguration()
    {
        if (_retargetEnabled == null) return;
        _retargetSourceRig = ""; _retargetTargetHash = ""; _retargetSourceHash = ""; _retargetOffsets.Clear(); DisplayRetargetAngles(); _retargetEnabled.ButtonPressed = false;
        _retargetInPlace.ButtonPressed = false; _retargetScale.Value = 1; _retargetHeight.Value = 0; _retargetProps.ButtonPressed = true;
        _retargetInfo.Text = "选择动画后应用配置。脚底及双手接触 IK 尚未启用。"; _retargetInfo.TooltipText = "";
        _retargetName.Text = "";
        try { if (_selected != null && _api.RetargetPresets.ForAsset(_selected.Id) is { } options) SetRetargetOptions(options); }
        catch (Exception e) { _retargetInfo.Text = e.Message; }
        RefreshRetargetPresets();
    }
    private async Task ApplyRetargetPreview()
    {
        if (_api == null || _selected == null || _previewAssetId != _selected.Id) { SetStatus("请先预览一个动画资产。"); return; }
        _retargetCancellation?.Cancel(); using var cancellation = new CancellationTokenSource(); _retargetCancellation = cancellation;
        var api = _api; string id = _selected.Id;
        bool wasPlaying = _preview.Playing;
        try
        {
            var options = api.InspectRetarget(id, ReadRetargetOptions());
            _retargetInfo.Text = "正在计算目标动画，可取消…";
            _characterStatus.Text = "正在应用角色 · " + RetargetCharacter.Name;
            var result = await _characterCache.Get(api.Storage.Resolve(api.Find(id).VirtualPath), options,
                () => api.PreviewRetargetAsync(id, options, cancellation.Token), cancellation.Token);
            if (_closing || _api != api || _selected?.Id != id || cancellation.IsCancellationRequested) { result.Root.Free(); return; }
            _preview.AttachRetarget(result, options, api.Storage.Resolve(api.Find(id).VirtualPath), _retargetView.Selected);
            SetRetargetOptions(options);
            _previewCharacterId = options.Character?.Id ?? options.TargetRig;
            _characters.Select(_previewCharacterId); RefreshCharacterChoices();
            _characterStatus.Text = "Ready · " + RetargetCharacter.Name + $" · {result.Target.Names.Length} 骨骼";
            if (wasPlaying && !_preview.Playing) _preview.TogglePlay();
            _preview.SetSpeed(_speed.Value);
            _stage.SetLoadStatus("");
            SyncRetargetSummary();
            UpdateTransport(); SyncExportControls(); SetStatus("Retarget 预览已就绪；导出将使用同一配置。");
        }
        catch (OperationCanceledException) { if (_retargetCancellation == cancellation) _retargetInfo.Text = "已取消，保留当前预览。"; }
        catch (Exception e)
        {
            if (_retargetCancellation != cancellation || cancellation.IsCancellationRequested || _selected?.Id != id || _api != api) return;
            _retargetEnabled.ButtonPressed = false;
            _stage.SetLoadStatus("重定向失败，保留当前预览。" + e.Message);
            _retargetInfo.Text = "无法重定向：" + e.Message;
            _characterStatus.Text = "待修复 · " + e.Message.Replace("\n", "；");
            _characterStatus.TooltipText = e.Message;
            SyncExportControls(); UpdateTransport();
        }
        finally { if (_retargetCancellation == cancellation) _retargetCancellation = null; }
    }
    private void SyncRetargetSummary()
    {
        _retargetInfo.Text = _preview.RetargetSummary;
        _retargetInfo.TooltipText = _preview.RetargetDiagnostics;
    }
    private void SaveRetargetPreset()
    {
        try
        {
            if (_selected == null) throw new InvalidOperationException("请选择源动画。");
            using var activity = _api.BeginActivity();
            var options = _api.InspectRetarget(_selected.Id, ReadRetargetOptions());
            _api.RetargetPresets.Save(_retargetName.Text, options); _api.RetargetPresets.Associate(_selected.Id, options);
            SetRetargetOptions(options); RefreshRetargetPresets(); SetStatus("已保存重定向配置。");
        }
        catch (Exception e) { _retargetInfo.Text = e.Message; }
    }
    private void RestoreRetargetSource()
    {
        _retargetCancellation?.Cancel();
        _previewCharacterId = ""; _characters.Select(""); RefreshCharacterChoices();
        if (_selected == null) return;
        try { _preview.LoadFile(_api.Storage.Resolve(_selected.VirtualPath)); _retargetEnabled.ButtonPressed = false; _retargetInfo.Text = "已恢复源动画。"; UpdateTransport(); }
        catch (Exception e) { _retargetInfo.Text = e.Message; }
    }
}
