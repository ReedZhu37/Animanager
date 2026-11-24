using Godot;
using Animanager.UI.Workspace;

namespace Animanager.UI.Shell;

/// <summary>Component shell around the persistent docking workspace and temporary drawers.</summary>
public partial class ShellLayout : Control
{
    public AppHeader Header { get; } = new();
    public StatusBar Status { get; } = new();
    public DockWorkspace Docking { get; } = new();
    public WorkspaceLayout Layout => Docking.Layout;
    public bool Compact => Docking.Compact;
    public string FocusedPanel => Docking.MaximizedPanel ?? "";
    public string ActiveTool { get; private set; } = "inspector";
    public string ActiveDrawer { get; private set; } = "";
    public event Action? LayoutChanged;
    private readonly Dictionary<string, Control> _panels = [];
    private readonly Dictionary<string, Control> _drawers = [];
    private readonly PanelContainer _drawer = new() { Visible = false };
    private readonly PanelContainer _filterDock = new() { Name = "FilterSidebar", Visible = false };
    private readonly VBoxContainer _drawerBody = new();
    private readonly Label _drawerTitle = Type.Build("", Typography.Semi, Type.SectionTitle, Tokens.Text);
    private readonly HFlowContainer _switcher = new() { Visible = false };
    private readonly Label _toast = new() { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
    private Godot.Timer _toastTimer = null!;

    public ShellLayout()
    {
        var background = new ColorRect { Color = Tokens.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background); background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 0);
        AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(Header);
        foreach (var (id, title) in new[] { ("assets", "动画资产"), ("preview", "预览"), ("inspector", "工具"), ("", "恢复") })
        {
            var button = new Button { Text = title, TooltipText = id == "" ? "恢复分区布局" : "显示" + title };
            button.Pressed += () =>
            {
                if (id == "") { if (FocusedPanel.Length > 0) Docking.Maximize(FocusedPanel); else RestorePanels(); }
                else ShowPanel(id == "inspector" ? ActiveTool : id);
            };
            _switcher.AddChild(button);
        }
        var content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", Tokens.Space.Sm);
        var padding = Surfaces.Padding(Tokens.Space.Sm); padding.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(padding);
        var body = new HBoxContainer();
        padding.AddChild(body);
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        body.AddChild(content);
        body.AddChild(_filterDock);
        body.AddThemeConstantOverride("separation", Tokens.Space.Sm);
        _filterDock.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Card,
            Tokens.Space.Md, Tokens.Space.Md, Tokens.LineStrong));
        content.AddChild(_switcher);
        Docking.SizeFlagsVertical = SizeFlags.ExpandFill; content.AddChild(Docking);
        Docking.LayoutChanged += () => { SyncNavigation(); LayoutChanged?.Invoke(); };
        Docking.ViewChanged += SyncNavigation;
        Docking.Notice += Toast;
        root.AddChild(Status);

        AddChild(_drawer); _drawer.SetAnchorsPreset(LayoutPreset.RightWide);
        _drawer.OffsetTop = Tokens.Size.HeaderHeight + Tokens.Space.Sm; _drawer.OffsetBottom = -Tokens.Size.StatusBarHeight - Tokens.Space.Sm;
        _drawer.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Card, Tokens.Space.Lg, Tokens.Space.Lg, Tokens.LineStrong));
        _drawerBody.AddThemeConstantOverride("separation", Tokens.Space.Md); _drawer.AddChild(_drawerBody);
        var heading = new HBoxContainer(); _drawerBody.AddChild(heading);
        _drawerTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill; _drawerTitle.VerticalAlignment = VerticalAlignment.Center; heading.AddChild(_drawerTitle);
        var close = new Button { Text = "关闭", TooltipText = "关闭抽屉 · Esc" };
        close.Pressed += CloseDrawer; heading.AddChild(close);
        AddChild(_toast); _toast.SetAnchorsPreset(LayoutPreset.BottomWide);
        _toast.OffsetLeft = 16; _toast.OffsetRight = -16; _toast.OffsetTop = -76; _toast.OffsetBottom = -36;
        _toast.AddThemeStyleboxOverride("normal", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Control, 12, 8, Tokens.LineStrong));
        _toast.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
    }

    public override void _Ready()
    {
        _toastTimer = new Godot.Timer { OneShot = true, WaitTime = 4 }; AddChild(_toastTimer);
        _toastTimer.Timeout += () => _toast.Hide();
        Resized += ResizeDrawer; ResizeDrawer();
    }
    public void Register(string id, string title, Control panel)
    {
        _panels.Add(id, panel);
        if (id == "filters") _filterDock.AddChild(panel);
        else if (id is "export" or "jobs")
        {
            var scroll = new ScrollContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            panel.SizeFlagsHorizontal = SizeFlags.ExpandFill; scroll.AddChild(panel);
            scroll.SetMeta("title", title); _drawerBody.AddChild(scroll); _drawers[id] = scroll;
        }
        else Docking.Register(id, title, panel);
    }
    public void Initialize(WorkspaceLayout? saved) => Docking.Initialize(saved?.Clone() ?? WorkspaceLayout.Studio());
    public Control Panel(string id) => _panels[id];
    public bool IsPanelVisible(string id) => _panels.TryGetValue(id, out var panel) && panel.IsVisibleInTree();
    public void ShowPanel(string id)
    {
        if (!_panels.ContainsKey(id)) return;
        if (id == "filters")
        {
            _drawer.Hide();
            if (Docking.IsPanelVisible("assets")) Layout.CompactActive = "assets";
            ActiveDrawer = id;
            _filterDock.Show();
        }
        else if (_drawers.ContainsKey(id))
        {
            _filterDock.Hide();
            ActiveDrawer = id;
            foreach (var (key, host) in _drawers) host.Visible = key == id;
            _drawerTitle.Text = _drawers[id].GetMeta("title").AsString(); _drawer.Show();
        }
        else
        {
            if (ActiveDrawer != "filters" || id is not ("assets" or "preview" or "navigation")) CloseDrawer();
            if (id is "inspector" or "bones" or "retarget") ActiveTool = id;
            Docking.ShowPanel(id);
        }
    }
    public void CloseDrawer() { _drawer.Hide(); _filterDock.Hide(); ActiveDrawer = ""; }
    public void FocusPanel(string id) { ShowPanel(id); if (FocusedPanel != id) Docking.Maximize(id); }
    public void RestorePanels() { CloseDrawer(); Docking.Restore(WorkspaceLayout.Studio()); }
    public void HidePanel(string id) { if (id == "filters" || _drawers.ContainsKey(id)) CloseDrawer(); else Docking.HidePanel(id); }
    public void CycleFocus() => Docking.CycleFocus();
    public void Toast(string text) { _toast.Text = text; _toast.Show(); _toastTimer?.Start(); }
    private void ResizeDrawer()
    {
        _drawer.OffsetLeft = -Math.Min(460, Math.Max(0, Size.X - 16)); _drawer.OffsetRight = -8;
        _filterDock.CustomMinimumSize = new(Math.Clamp(Size.X * .29f, 202, 302), 0);
    }
    private void SyncNavigation()
    {
        foreach (var group in Layout.Root.Groups())
            if (group.Active is "inspector" or "bones" or "retarget") ActiveTool = group.Active;
        _switcher.Visible = Compact || FocusedPanel.Length > 0;
    }
}
