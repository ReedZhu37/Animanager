using Godot;

namespace Animanager.UI.Components;

public partial class Preview3DViewport
{
    public Basis CameraBasis => _camera.Basis;
    public Vector3 CameraTarget => _target;
    public float CameraDistance => _distance;
    public bool Orthographic => _camera.Projection == Camera3D.ProjectionType.Orthogonal;
    private AxisGizmo? _axisGizmo;
    private HBoxContainer _comparisonTools = null!;
    private CheckButton _showComparisonNames = null!;
    private HBoxContainer _selectedBoneTools = null!;
    private string _viewName = "用户视图";
    private MeshInstance3D? _grid;

    internal Button LocateAssetButton { get; } = new()
    {
        Name = "LocatePreviewAsset", Text = "定位", TooltipText = "在当前资产筛选结果中定位", Visible = false,
        FocusMode = FocusModeEnum.None
    };
    public event Action? SettingsRequested;
    public bool ClayEnabled => _clay;
    public bool GridVisible => _grid?.Visible == true;
    public void SetGridVisible(bool value) { if (_grid != null) _grid.Visible = value; Redraw(); }

    private void BuildNavigation()
    {
        var overlay = new Control { Name = "Navigation", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(overlay); overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var settings = new Button { Name = "ViewportSettings", Text = "⚙", TooltipText = "视口设置 · 预览角色、显示与导航", FocusMode = FocusModeEnum.None, CustomMinimumSize = new(28, 26), Position = new(8, 8), Visible = SettingsRequested != null };
        settings.Pressed += () => SettingsRequested?.Invoke(); overlay.AddChild(settings);
        BuildWeaponPreview(overlay);
        var assetTools = new HBoxContainer { Position = new(8, 40), MouseFilter = MouseFilterEnum.Ignore };
        overlay.AddChild(assetTools);
        assetTools.AddChild(LocateAssetButton);
        var comparisonTools = _comparisonTools = new HBoxContainer
        {
            Name = "ComparisonTools", MouseFilter = MouseFilterEnum.Ignore, Visible = false
        };
        assetTools.AddChild(comparisonTools);
        var exit = new Button { Name = "ExitAnimationComparison", Text = "退出对比" };
        exit.Pressed += () => ComparisonExitRequested?.Invoke();
        comparisonTools.AddChild(exit);
        _showComparisonNames = new CheckButton { Name = "ShowComparisonNames", Text = "名称", ButtonPressed = true };
        _showComparisonNames.Toggled += _ => UpdateComparisonNames();
        comparisonTools.AddChild(_showComparisonNames);
        BuildComparisonOrder(comparisonTools, overlay);
        var tools = new HBoxContainer { Name = "DragNavigation", GrowHorizontal = GrowDirection.Begin }; overlay.AddChild(tools);
        tools.SetAnchorsPreset(LayoutPreset.TopRight);
        tools.OffsetLeft = -144; tools.OffsetRight = -4; tools.OffsetTop = 104; tools.OffsetBottom = 132;
        foreach (var (tool, label, tip) in new[] { ("orbit", "旋转", "按住此按钮并拖动旋转 · 中键 / 右键"),
                     ("pan", "平移", "按住此按钮并拖动平移 · Shift + 中键"), ("zoom", "缩放", "按住此按钮并上下拖动缩放 · Ctrl + 中键 / 滚轮") })
            tools.AddChild(new ViewportDragTool(this, tool) { Name = "Tool_" + tool, Text = label, TooltipText = tip });
        _axisGizmo = new AxisGizmo(this) { Name = "AxisGizmo", TooltipText = "拖动旋转视图；点击 X / Y / Z 切换轴向视图（Y 向上）" };
        overlay.AddChild(_axisGizmo); _axisGizmo.SetAnchorsPreset(LayoutPreset.TopRight);
        _axisGizmo.OffsetLeft = -100; _axisGizmo.OffsetRight = -4; _axisGizmo.OffsetTop = 4; _axisGizmo.OffsetBottom = 100;
        var selectedTools = _selectedBoneTools = new HBoxContainer { Name = "SelectedBoneTools", Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        assetTools.AddChild(selectedTools);
        var boneName = new Label { Name = "SelectedBoneName", CustomMinimumSize = new(0, 24), TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        boneName.AddThemeFontSizeOverride("font_size", 12); selectedTools.AddChild(boneName);
        var trail = new CheckButton { Name = "SelectedBoneTrail", Text = "轨迹", FocusMode = FocusModeEnum.None, TooltipText = "显示选中骨骼的运动轨迹；选择其他骨骼后自动更新" };
        trail.AddThemeFontSizeOverride("font_size", 12); selectedTools.AddChild(trail);
        bool syncing = false;
        trail.Toggled += value =>
        {
            if (syncing) return;
            try { Bones.ShowTrail(value); }
            catch (Exception e) { trail.SetPressedNoSignal(Bones.TrailEnabled); trail.TooltipText = e.Message; }
        };
        void SelectionChanged()
        {
            syncing = true;
            UpdateComparisonNavigation();
            boneName.Text = Bones.Selected is { } id ? Bones.Name(id) : "";
            boneName.TooltipText = boneName.Text;
            trail.Disabled = CurrentClip.Length == 0;
            trail.SetPressedNoSignal(Bones.TrailEnabled);
            syncing = false;
        }
        Bones.Changed += SelectionChanged;
        overlay.Resized += () =>
        {
            float side = Math.Clamp(Size.Y - 76, 48, 96);
            _axisGizmo.OffsetLeft = -side - 4; _axisGizmo.OffsetBottom = side + 4;
            _axisGizmo.Visible = Size.X >= 180 && Size.Y >= 90;
            tools.OffsetTop = side + 8; tools.OffsetBottom = side + 36;
            boneName.CustomMinimumSize = new(Math.Clamp(Size.X - 270, 30, 140), 24);
            boneName.Size = new(Math.Clamp(Size.X - 270, 30, 140), 24);
            _axisGizmo.QueueRedraw();
        };
    }

    private void UpdateComparisonNavigation()
    {
        _comparisonTools.Visible = AssetComparison != null;
        _selectedBoneTools.Visible = AssetComparison == null && Bones.Selected != null;
    }

    private void UpdateComparisonNames()
    {
        if (AssetComparison == null) return;
        foreach (var label in AssetComparison.Labels) label.Visible = _showComparisonNames.ButtonPressed;
        Redraw();
    }

    public void Navigate(string tool, Vector2 relative)
    {
        if (tool == "orbit") { _yaw -= relative.X * .008f; _pitch = Mathf.Clamp(_pitch + relative.Y * .008f, -Mathf.Pi / 2 + .001f, Mathf.Pi / 2 - .001f); _viewName = "用户视图"; }
        if (tool == "pan") _target += (-_camera.Basis.X * relative.X + _camera.Basis.Y * relative.Y) * _distance * .0015f;
        if (tool == "zoom") _distance = Mathf.Clamp(_distance * Mathf.Exp(relative.Y * .01f), .05f, 5000);
        UpdateCamera();
    }
    public void ToggleProjection() { _camera.Projection = Orthographic ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal; UpdateCamera(); }
    public void SnapAxis(int axis, int sign = 1)
    {
        (_yaw, _pitch) = axis switch { 0 => (sign * Mathf.Pi / 2, 0f), 1 => (0f, sign * (Mathf.Pi / 2 - .001f)), _ => (sign > 0 ? 0f : Mathf.Pi, 0f) };
        _viewName = (sign > 0 ? "+" : "−") + new[] { "X", "Y", "Z" }[axis] + " 轴视图";
        _camera.Projection = Camera3D.ProjectionType.Orthogonal; UpdateCamera();
    }
    public bool HandleViewKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Kp1: SnapAxis(2, key.CtrlPressed ? -1 : 1); break;
            case Key.Kp3: SnapAxis(0, key.CtrlPressed ? -1 : 1); break;
            case Key.Kp7: SnapAxis(1, key.CtrlPressed ? -1 : 1); break;
            case Key.Kp5: ToggleProjection(); break;
            case Key.KpPeriod: FrameModel(); break;
            default: return false;
        }
        return true;
    }
}

/// <summary>Press-drag-release is one gesture; clicking never arms a tool for a later viewport drag.</summary>
public partial class ViewportDragTool : Button
{
    private readonly Preview3DViewport _preview;
    private readonly string _tool;
    private bool _dragging;
    public ViewportDragTool(Preview3DViewport preview, string tool)
    {
        _preview = preview; _tool = tool; FocusMode = FocusModeEnum.None;
        CustomMinimumSize = new(42, 26); AddThemeFontSizeOverride("font_size", 11);
        MouseDefaultCursorShape = CursorShape.Drag;
    }
    public override void _GuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        { _dragging = button.Pressed; if (!button.Pressed) _preview.GrabFocus(); AcceptEvent(); }
        if (ev is InputEventMouseMotion motion && _dragging && (motion.ButtonMask & MouseButtonMask.Left) != 0)
        { _preview.Navigate(_tool, motion.Relative); AcceptEvent(); }
    }
}

public partial class AxisGizmo : Control
{
    private readonly Preview3DViewport _preview;
    private Vector2 _pressedAt;
    private bool _dragging, _moved;
    private readonly Color[] _colors = [new("ec5f67"), new("8dcc65"), new("65a6f2")];
    public AxisGizmo(Preview3DViewport preview) { _preview = preview; MouseFilter = MouseFilterEnum.Stop; }
    private IEnumerable<(int Axis, int Sign, Vector2 Point, float Depth)> Ends()
    {
        var inverse = _preview.CameraBasis.Inverse();
        for (int axis = 0; axis < 3; axis++)
            foreach (int sign in new[] { -1, 1 })
            {
                var v = inverse * (Basis.Identity[axis] * sign);
                yield return (axis, sign, Size / 2 + new Vector2(v.X, -v.Y) * (Math.Min(Size.X, Size.Y) * .32f), v.Z);
            }
    }
    public override void _Draw()
    {
        DrawCircle(Size / 2, Math.Min(Size.X, Size.Y) * .46f, new Color(0.1f, .12f, .14f, .65f));
        foreach (var end in Ends().OrderBy(e => e.Depth))
        {
            var color = _colors[end.Axis]; if (end.Sign < 0) color = color.Darkened(.5f);
            DrawLine(Size / 2, end.Point, color, 2, true); DrawCircle(end.Point, end.Sign > 0 ? 9 : 6, color);
            if (end.Sign > 0) DrawString(ThemeDB.FallbackFont, end.Point + new Vector2(-4, 4), new[] { "X", "Y", "Z" }[end.Axis], fontSize: 12, modulate: Colors.Black);
        }
    }
    public override void _GuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            if (b.Pressed) { _pressedAt = b.Position; _dragging = true; _moved = false; }
            else if (_dragging)
            {
                _dragging = false;
                if (!_moved)
                {
                    var hit = Ends().Where(e => e.Point.DistanceTo(b.Position) <= 11).OrderByDescending(e => e.Depth).ToArray();
                    if (hit.Length > 0) _preview.SnapAxis(hit[0].Axis, hit[0].Sign);
                }
                _preview.GrabFocus();
            }
            AcceptEvent();
        }
        if (ev is InputEventMouseMotion m && _dragging)
        {
            _moved |= m.Position.DistanceTo(_pressedAt) > 3;
            if (_moved) _preview.Navigate("orbit", m.Relative);
            AcceptEvent();
        }
    }
}
