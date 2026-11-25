using Godot;
using Animanager.UI.Components;

namespace Animanager.UI.Shell;

/// <summary>
/// Preview stage: viewport, clip selection and two-row transport. Character/display
/// controls live in a separate settings window.
/// </summary>
public partial class PreviewStage : Control
{
    public Preview3DViewport Viewport { get; }
    public Label TitleLabel => _title;
    public Label TimeLabel => _time;
    public Button PlayButton => _play;
    public HSlider Timeline => _timeline;
    public HSlider SpeedControl => _speed;
    public VBoxContainer CharacterSlot { get; } = new();
    internal VBoxContainer WeaponSlot { get; } = new();
    public AcceptDialog SettingsDialog { get; } = new() { Name = "ViewportSettingsPage", Title = "视口设置", OkButtonText = "关闭" };
    public CheckButton ResetCameraToggle { get; } = new()
    {
        Name = "ResetCameraOnAnimationChange", Text = "切换动画时重置镜头",
        TooltipText = "切换动画资产或片段时重新聚焦角色。"
    };
    private readonly Label _loadStatus = new() { Name = "PreviewLoadStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2, Visible = false };
    public event Action? SettingsPressed;
    private readonly Button _retry = new() { Name = "RetryPreview", Text = "重试", Visible = false };
    private readonly PanelContainer _paneHeader = new() { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _paneTitle = Type.CaptionLabel("", Tokens.Text);
    private bool _showPaneHeader;
    private bool _paneActive;
    private string[]? _transportClips;

    public void SetLoadStatus(string text, Action? retry = null)
    {
        _loadStatus.Text = text; _loadStatus.TooltipText = text; _loadStatus.Visible = text.Length > 0;
        _retryAction = retry;
        _retry.Visible = retry != null;
        _loadStatus.OffsetRight = retry == null ? -16 : -82;
    }

    private Action? _retryAction;

    public event Action? PlayPressed;
    public event Action<double>? SpeedChanged;
    public event Action<string>? ClipSelected;
    public event Action<double>? SeekRequested;
    public event Action? LoopToggled;
    public event Action? ClayToggled;
    public event Action? BonesPressed;
    public event Action? FramePressed;
    public event Action<int>? StepRequested;

    private readonly Label _title;
    private readonly Label _badges;
    private readonly Label _time;
    private readonly IconButton _play;
    private readonly IconButton _loop;
    private readonly CheckButton _clay = new() { Text = "粘土材质", ButtonPressed = true };
    private readonly CheckButton _grid = new() { Text = "地面网格", ButtonPressed = true };
    private readonly HSlider _timeline;
    private readonly HSlider _speed;
    private readonly VBoxContainer _clipList;
    private readonly Control _clipPanel;
    private PanelContainer _transport = null!;

    private bool _syncing;
    private bool _comparing;
    private int _clipCount;
    private readonly OptionButton _compactClip = new() { Name = "CompactClip", FitToLongestItem = false, CustomMinimumSize = new(90, 24), Visible = false };

    public PreviewStage()
    {
        CustomMinimumSize = new Vector2(Tokens.Size.StageMin, 160);
        ClipContents = true;

        Viewport = new Preview3DViewport { Name = "Viewport" };
        AddChild(Viewport);
        Viewport.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Viewport.OffsetRight = -152;
        Viewport.OffsetTop = 0;
        Viewport.OffsetBottom = -92;

        // Display/character controls live in a separate settings page, outside the stage area.
        AddChild(SettingsDialog);
        var settingsScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        SettingsDialog.AddChild(settingsScroll);
        var settingsColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        settingsColumn.AddThemeConstantOverride("separation", 10); settingsScroll.AddChild(settingsColumn);
        _title = Type.Build("未加载资产", Typography.Semi, Type.Body, Tokens.Text);
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _badges = Type.Build("", Typography.Regular, Type.Caption, Tokens.Text3);
        _badges.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        settingsColumn.AddChild(_title); settingsColumn.AddChild(_badges); settingsColumn.AddChild(CharacterSlot);
        settingsColumn.AddChild(WeaponSlot);
        settingsColumn.AddChild(new HSeparator());
        _clay.Toggled += _ => ClayToggled?.Invoke(); settingsColumn.AddChild(_clay);
        _grid.Toggled += Viewport.SetGridVisible; settingsColumn.AddChild(_grid);
        settingsColumn.AddChild(ResetCameraToggle);
        var projection = new Button { Text = "切换透视 / 正交" };
        projection.Pressed += Viewport.ToggleProjection; settingsColumn.AddChild(projection);
        var frame = new Button { Text = "聚焦角色 · F" }; frame.Pressed += () => FramePressed?.Invoke(); settingsColumn.AddChild(frame);
        var bones = new Button { Text = "骨骼与轨迹…" };
        bones.Pressed += () => { SettingsDialog.Hide(); BonesPressed?.Invoke(); }; settingsColumn.AddChild(bones);
        var help = Type.Build("按住视口导航按钮直接拖动。中键旋转，Shift+中键平移，滚轮缩放。", Typography.Regular, Type.Caption, Tokens.Text3);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart; settingsColumn.AddChild(help);
        Viewport.SettingsRequested += () => SettingsPressed?.Invoke();
        AddChild(_loadStatus); _loadStatus.SetAnchorsPreset(LayoutPreset.BottomWide);
        _loadStatus.OffsetLeft = 16; _loadStatus.OffsetRight = -16; _loadStatus.OffsetTop = -120; _loadStatus.OffsetBottom = -88;
        _loadStatus.AddThemeColorOverride("font_color", Tokens.Warn);
        AddChild(_retry); _retry.SetAnchorsPreset(LayoutPreset.BottomRight);
        _retry.OffsetLeft = -76; _retry.OffsetRight = -12;
        _retry.Pressed += () => _retryAction?.Invoke();
        AddChild(_compactClip); _compactClip.SetAnchorsPreset(LayoutPreset.BottomRight);
        _compactClip.OffsetLeft = -152; _compactClip.OffsetRight = -12; _compactClip.OffsetTop = -114; _compactClip.OffsetBottom = -88;
        _compactClip.ItemSelected += index => ClipSelected?.Invoke(_compactClip.GetItemText((int)index));

        // Clip column
        _clipPanel = new PanelContainer();
        AddChild(_clipPanel);
        _clipPanel.SetAnchorsPreset(LayoutPreset.RightWide);
        _clipPanel.OffsetLeft = -146;
        _clipPanel.OffsetRight = -10;
        _clipPanel.OffsetTop = 8;
        _clipPanel.OffsetBottom = -92;
        _clipPanel.CustomMinimumSize = new Vector2(136, 0);
        _clipPanel.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel.Alpha(.92f), Tokens.Radius.Card, 0, 0,
            Tokens.Line));

        var clipColumn = new VBoxContainer();
        clipColumn.AddThemeConstantOverride("separation", 2);
        _clipPanel.AddChild(clipColumn);

        var clipHeader = new MarginContainer();
        clipHeader.AddThemeConstantOverride("margin_left", 10);
        clipHeader.AddThemeConstantOverride("margin_top", 8);
        clipHeader.AddThemeConstantOverride("margin_bottom", 4);
        clipHeader.AddChild(Type.Build("动画片段", Typography.Semi, Type.Micro, Tokens.Text3));
        clipColumn.AddChild(clipHeader);

        var clipScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var clipPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        clipPad.AddThemeConstantOverride("margin_left", 4);
        clipPad.AddThemeConstantOverride("margin_right", 4);
        clipPad.AddThemeConstantOverride("margin_bottom", 6);
        _clipList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _clipList.AddThemeConstantOverride("separation", 1);
        clipPad.AddChild(_clipList);
        clipScroll.AddChild(clipPad);
        clipColumn.AddChild(clipScroll);

        BuildTransport(out _play, out _timeline, out _time, out _speed, out _loop);
        AddChild(_paneHeader);
        _paneHeader.SetAnchorsPreset(LayoutPreset.TopWide);
        _paneHeader.OffsetBottom = 24;
        _paneTitle.MouseFilter = MouseFilterEnum.Ignore;
        _paneTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _paneHeader.AddChild(_paneTitle);
        Resized += AdaptHeight;
    }

    private void AdaptHeight()
    {
        bool compact = Size.Y < 240;
        Viewport.CustomMinimumSize = new(160, compact ? 24 : 120);
        Viewport.OffsetTop = _showPaneHeader ? 24 : 0;
        Viewport.OffsetBottom = compact ? -64 : -84;
        _clipPanel.OffsetTop = _showPaneHeader ? 32 : 8;
        _clipPanel.OffsetBottom = compact ? -64 : -84;
        _transport.OffsetTop = compact ? -58 : -76;
        _transport.OffsetBottom = compact ? -4 : -8;
        _loadStatus.OffsetTop = compact ? -110 : -120;
        _loadStatus.OffsetBottom = compact ? -64 : -84;
        _retry.OffsetTop = compact ? -96 : -116;
        _retry.OffsetBottom = compact ? -68 : -88;
        AdaptClips();
    }

    internal void SetPaneTitle(string title) => _paneTitle.Text = title;

    internal void ResetPreviewSettings()
    {
        _clay.SetPressedNoSignal(true);
        _grid.SetPressedNoSignal(true);
        Viewport.SetClay(true);
        Viewport.SetGridVisible(true);
        Viewport.SetLoop(true);
        SetLoop(true);
        Viewport.SetSpeed(1);
        _speed.Value = 1;
        if (Viewport.Orthographic) Viewport.ToggleProjection();
        Viewport.FrameModel();
    }

    internal void SetPanePresentation(bool multiple, bool active)
    {
        if (_showPaneHeader == multiple && _paneActive == active) return;
        _showPaneHeader = multiple;
        _paneActive = active;
        _paneHeader.Visible = multiple;
        _paneHeader.AddThemeStyleboxOverride("panel", Surfaces.Fill(active ? Tokens.AccentWash : Tokens.Panel,
            padX: 8, border: active ? Tokens.Accent : Tokens.Line));
        AdaptHeight();
    }
    private void AdaptClips()
    {
        bool column = !_comparing && _clipCount > 1 && Size.X >= 550;
        _clipPanel.Visible = column; Viewport.OffsetRight = column ? -152 : 0;
        _compactClip.Visible = !_comparing && _clipCount > 1 && !column;
        _compactClip.OffsetTop = Size.Y < 420 ? -100 : -114;
        _compactClip.OffsetBottom = Size.Y < 420 ? -74 : -88;
    }

    public void SetComparing(bool comparing)
    {
        if (_comparing == comparing) return;
        _comparing = comparing;
        CharacterSlot.Visible = !comparing;
        AdaptClips();
    }

    private void BuildTransport(out IconButton play, out HSlider timeline, out Label time, out HSlider speed,
        out IconButton loop)
    {
        var panel = new PanelContainer(); _transport = panel;
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.BottomWide);
        panel.OffsetLeft = 12;
        panel.OffsetRight = -12;
        panel.OffsetTop = -84;
        panel.OffsetBottom = -12;
        panel.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel.Alpha(.94f), Tokens.Radius.Card, 0, 0,
            Tokens.Line));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 2);
        panel.AddChild(column);

        // Row 1: fixed-size controls only, so the row never wraps.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 6);
        pad.AddThemeConstantOverride("margin_right", 8);
        pad.AddThemeConstantOverride("margin_top", 4);
        pad.AddChild(row);
        column.AddChild(pad);

        var start = new IconButton(Icons.SkipStart, 14, "上一帧");
        start.Pressed += () => StepRequested?.Invoke(-1);
        row.AddChild(start);

        play = new IconButton(Icons.Play, 16, "播放 / 暂停（空格）");
        play.CustomMinimumSize = new Vector2(30, 26);
        play.Pressed += () => PlayPressed?.Invoke();
        row.AddChild(play);

        var end = new IconButton(Icons.SkipEnd, 14, "下一帧");
        end.Pressed += () => StepRequested?.Invoke(1);
        row.AddChild(end);

        time = Type.MonoLabel("0 / 0 帧", Tokens.Text2);
        time.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        time.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(time);

        speed = new HSlider { Name = "PlaybackSpeed", MinValue = .1, MaxValue = 3, Step = .05, Value = 1,
            CustomMinimumSize = new Vector2(62, 24), FocusMode = FocusModeEnum.None, TooltipText = "播放速率 · 0.1–3×；点击数值恢复 1×" };
        var speedValue = new Button { Text = "1.00×", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "恢复 1×" };
        speedValue.AddThemeFontSizeOverride("font_size", Type.Micro);
        var slider = speed;
        speedValue.Pressed += () => slider.Value = 1;
        speed.ValueChanged += value => { speedValue.Text = $"{value:0.00}×"; SpeedChanged?.Invoke(value); };
        row.AddChild(speed);
        row.AddChild(speedValue);

        loop = new IconButton(Icons.Repeat, 14, "循环播放");
        loop.Pressed += () => LoopToggled?.Invoke();
        row.AddChild(loop);

        // Row 2: the scrubber owns the full width.
        var scrub = new MarginContainer();
        scrub.AddThemeConstantOverride("margin_left", 10);
        scrub.AddThemeConstantOverride("margin_right", 10);
        scrub.AddThemeConstantOverride("margin_bottom", 6);
        timeline = new HSlider
        {
            MinValue = 0, MaxValue = 1, Step = .001, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(40, 14), FocusMode = FocusModeEnum.None
        };
        timeline.ValueChanged += value =>
        {
            if (!_syncing) SeekRequested?.Invoke(value);
        };
        scrub.AddChild(timeline);
        column.AddChild(scrub);
    }

    public void SetClips(IReadOnlyList<string> clips, string? current)
    {
        _clipCount = clips.Count; _compactClip.Clear();
        foreach (string clip in clips) _compactClip.AddItem(clip);
        foreach (var child in _clipList.GetChildren())
        {
            _clipList.RemoveChild(child);
            child.QueueFree();
        }

        if (clips.Count == 0)
        {
            AdaptClips(); _play.Disabled = true;
            return;
        }
        AdaptClips(); _play.Disabled = false;
        foreach (var clip in clips)
        {
            var row = new ClipButton(clip);
            row.Pressed += () => ClipSelected?.Invoke(clip);
            _clipList.AddChild(row);
        }
        SetCurrentClip(current);
    }

    public void SetCurrentClip(string? current)
    {
        for (int i = 0; i < _compactClip.ItemCount; i++) if (_compactClip.GetItemText(i) == current) _compactClip.Select(i);
        foreach (var child in _clipList.GetChildren())
            if (child is ClipButton button) button.SetActive(button.Clip == current);
    }

    internal void SyncTransport()
    {
        if (!Mathf.IsEqualApprox(_speed.Value, Viewport.PlaybackSpeed)) _speed.Value = Viewport.PlaybackSpeed;
        if (!ReferenceEquals(_transportClips, Viewport.Clips))
        {
            _transportClips = Viewport.Clips;
            SetClips(Viewport.Clips, Viewport.CurrentClip);
        }
        SetCurrentClip(Viewport.CurrentClip);
        SetComparing(Viewport.AssetComparison != null);
        SetPlaying(Viewport.TransportPlaying);
        _play.Disabled = Viewport.TransportDuration <= 0;
        _timeline.Editable = Viewport.TransportDuration > 0;
        if (Viewport.AssetComparison is { } comparison) SetComparisonTime(comparison.Playhead, comparison.Duration);
        else SetTime(Viewport.Playhead, Viewport.Duration, Viewport.DisplayFps);
    }

    internal void SyncSettings()
    {
        _clay.SetPressedNoSignal(Viewport.ClayEnabled);
        _grid.SetPressedNoSignal(Viewport.GridVisible);
    }

    public void SetTitle(string title, string badges)
    {
        _title.Text = title;
        _title.TooltipText = title;
        _badges.Text = badges;
    }
    public void SetReadiness(bool ready, string summary)
    {
        _badges.Text = summary;
        _badges.TooltipText = summary;
        _badges.AddThemeColorOverride("font_color", ready ? Tokens.Success : Tokens.Warn);
    }

    public void SetTime(double playhead, double duration, double fps = 30)
    {
        _syncing = true;
        _timeline.MaxValue = Math.Max(duration, .001);
        _timeline.SetValueNoSignal(Math.Min(playhead, duration));
        int total = duration > 0 ? (int)Math.Ceiling(duration * fps - 1e-5) + 1 : 0;
        int current = total == 0 ? 0 : Math.Clamp((int)Math.Round(playhead * fps) + 1, 1, total);
        _time.Text = $"{current} / {total} 帧";
        _time.TooltipText = $"{playhead:0.000} / {duration:0.000} s · {fps:0.###} FPS\n帧从 1 开始，包含结束采样点；播放倍速不改变帧数。";
        _syncing = false;
    }

    public void SetComparisonTime(double playhead, double duration)
    {
        _syncing = true;
        _timeline.MaxValue = Math.Max(duration, .001);
        _timeline.SetValueNoSignal(playhead);
        _time.Text = $"{playhead:0.00} / {duration:0.00} s";
        _time.TooltipText = "对比时间轴 · 较短的动画在结束后保持最后一帧；循环时所有动画一起从头播放。";
        _syncing = false;
    }

    public void SetPlaying(bool playing) => _play.SetIcon(playing ? Icons.Pause : Icons.Play);

    public void SetLoop(bool loop) => _loop.AddThemeStyleboxOverride("normal",
        loop
            ? Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control, 0, 0, Tokens.Accent.Alpha(.5f))
            : Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));

    private sealed partial class ClipButton : Button
    {
        public readonly string Clip;

        public ClipButton(string clip)
        {
            Clip = clip;
            Text = clip;
            Alignment = HorizontalAlignment.Left;
            Flat = true;
            CustomMinimumSize = new Vector2(0, 26);
            FocusMode = FocusModeEnum.None;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            AddThemeFontSizeOverride("font_size", Type.Caption);
            AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Chip, 8, 3));
            AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Chip, 8, 3));
            AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Active, Tokens.Radius.Chip, 8, 3));
            AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, 0));
            AddThemeColorOverride("font_color", Tokens.Text2);
            AddThemeColorOverride("font_hover_color", Tokens.Text);
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }

        public void SetActive(bool active)
        {
            AddThemeStyleboxOverride("normal",
                Surfaces.Fill(active ? Tokens.AccentWash : Colors.Transparent, Tokens.Radius.Chip, 8, 3));
            AddThemeColorOverride("font_color", active ? Tokens.Accent : Tokens.Text2);
            AddThemeFontOverride("font", active ? Typography.Semi : Typography.Regular);
        }
    }
}
