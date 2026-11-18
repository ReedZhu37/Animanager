using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Top bar: brand, library switcher, elastic search, the primary actions, job count.
/// Every composite control publishes its own minimum width — a Button with anchored content
/// contributes nothing to the row's minimum size and would collapse on narrow windows.
/// </summary>
public partial class AppHeader : PanelContainer
{
    public event Action? ImportPressed;
    public event Action? ExportPressed;
    public event Action? JobsPressed;
    public event Action? LibraryPressed;
    public event Action? MorePressed;
    public event Action<string>? SearchChanged;

    public LineEdit SearchInput => _search.Input;
    private readonly List<Control> _secondary = [];
    private readonly SearchField _search;
    private readonly Label _libraryName;
    private readonly Label _libraryCount;
    private readonly PanelContainer _jobBadge;
    private readonly Label _jobCount;

    public AppHeader(string libraryName = "未打开库", int assetCount = 0)
    {
        var surface = Surfaces.Bar(Tokens.Raised, false, true, Tokens.Line);
        surface.ContentMarginTop = surface.ContentMarginBottom = Tokens.Space.Sm;
        AddThemeStyleboxOverride("panel", surface);
        CustomMinimumSize = new Vector2(0, Tokens.Size.HeaderHeight);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", Tokens.Space.Sm);
        AddChild(row);
        row.AddChild(Hairline.Gap(14));

        // Brand
        var brand = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        brand.AddThemeConstantOverride("separation", 8);
        brand.AddChild(new BrandMark());
        var brandText = Type.Build("Animanager", Typography.Semi, Type.Brand, Tokens.Text);
        brandText.VerticalAlignment = VerticalAlignment.Center;
        brandText.CustomMinimumSize = new Vector2(Typography.Width(Typography.Semi, "Animanager", Type.Brand), 0);
        brand.AddChild(brandText);
        row.AddChild(brand); _secondary.Add(brand);
        row.AddChild(Hairline.Gap(8));

        // Library switcher: a container so the name sizes correctly; a transparent button on top.
        var library = new PanelContainer { TooltipText = "切换资产库" };
        library.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 10, 4, Tokens.Line));
        var libraryRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        libraryRow.AddThemeConstantOverride("separation", 7);
        libraryRow.AddChild(new StatusDot(10));
        _libraryName = Type.Build(libraryName, Typography.Semi, Type.Row, Tokens.Text);
        _libraryName.CustomMinimumSize = new(95, 0);
        _libraryName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _libraryName.VerticalAlignment = VerticalAlignment.Center;
        libraryRow.AddChild(_libraryName);
        _libraryCount = Type.MonoLabel(assetCount.ToString("N0"), Tokens.Text3);
        _libraryCount.VerticalAlignment = VerticalAlignment.Center;
        libraryRow.AddChild(_libraryCount);
        libraryRow.AddChild(new IconView(Icons.ChevronDown, 13, Tokens.Text3));
        library.AddChild(libraryRow);
        var libraryButton = new Button
        {
            Name = "LibraryButton",
            Flat = true, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "切换资产库"
        };
        libraryButton.AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        libraryButton.AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Text.Alpha(.04f), Tokens.Radius.Control));
        libraryButton.AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Text.Alpha(.08f), Tokens.Radius.Control));
        libraryButton.AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, 0));
        libraryButton.Pressed += () => LibraryPressed?.Invoke();
        library.AddChild(libraryButton);
        row.AddChild(library); _secondary.Add(library);

        // Search is the only elastic element in the header.
        _search = new SearchField("搜索资产名称或标签", "Ctrl+F")
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, Tokens.Size.Control)
        };
        _search.TextChanged += text => SearchChanged?.Invoke(text);
        row.AddChild(_search);

        var import = new CapButton(Icons.Import, "导入");
        import.Pressed += () => ImportPressed?.Invoke();
        row.AddChild(import); _secondary.Add(import);

        var export = new CapButton(Icons.Export, "导出", primary: true);
        export.Pressed += () => ExportPressed?.Invoke();
        row.AddChild(export); _secondary.Add(export);

        // Jobs with a badge that stays inside the button's bounds.
        var jobs = new Control { CustomMinimumSize = new Vector2(14 + 7 + Typography.Width(Typography.Semi, "任务", Type.Row) + 26, Tokens.Size.Control) };
        var jobsButton = new CapButton(Icons.Layers, "任务");
        jobsButton.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        jobsButton.Pressed += () => JobsPressed?.Invoke();
        jobs.AddChild(jobsButton);
        _jobBadge = new PanelContainer
        {
            CustomMinimumSize = new Vector2(16, 14), MouseFilter = MouseFilterEnum.Ignore, Visible = false
        };
        _jobCount = Type.Build("0", Typography.Semi, 9, Tokens.Text2);
        _jobCount.HorizontalAlignment = HorizontalAlignment.Center;
        _jobCount.VerticalAlignment = VerticalAlignment.Center;
        _jobBadge.AddChild(_jobCount);
        jobs.AddChild(_jobBadge);
        row.AddChild(jobs); _secondary.Add(jobs);

        var more = new IconButton(Icons.More, 17, "更多");
        more.Pressed += () => MorePressed?.Invoke();
        row.AddChild(more);
        row.AddChild(Hairline.Gap(8));

        // Place the badge after the row has been laid out.
        jobsButton.Resized += () => PlaceBadge(jobs);
        jobs.Resized += () => PlaceBadge(jobs);
    }

    private void PlaceBadge(Control host)
    {
        var size = _jobBadge.GetCombinedMinimumSize();
        _jobBadge.Position = new Vector2(Math.Max(0, host.Size.X - size.X - 6), 3);
        _jobBadge.Size = size;
    }

    public void SetLibrary(string name, int assetCount)
    {
        _libraryName.Text = name;
        _libraryCount.Text = assetCount.ToString("N0");
    }

    public void SetJobs(int running, int total)
    {
        _jobCount.Text = total.ToString();
        _jobBadge.Visible = total > 0;
        var tint = running > 0 ? Tokens.Accent : Tokens.LineStrong;
        _jobBadge.AddThemeStyleboxOverride("panel", Surfaces.Fill(tint, Tokens.Radius.Pill, 5, 1));
        _jobCount.AddThemeColorOverride("font_color", running > 0 ? Tokens.OnAccent : Tokens.Text2);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Callable.From(() =>
        {
            for (int i = 0; i < _secondary.Count; i++) _secondary[i].Visible = Size.X >= (i == 0 ? 1050 : i == 1 ? 850 : 700);
        }).CallDeferred();
    }
    public void FocusSearch() => _search.GrabFocus();
}

/// <summary>Product mark: three keys of an animation curve.</summary>
public partial class BrandMark : Control
{
    public BrandMark()
    {
        CustomMinimumSize = new Vector2(22, 22);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Draw()
    {
        var size = Size;
        DrawStyleBox(Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control), new Rect2(Vector2.Zero, size));
        DrawRect(new Rect2(size.X * .5f - 1, size.Y * .24f, 2, size.Y * .52f), Tokens.Accent);
        DrawCircle(new Vector2(size.X * .5f, size.Y * .32f), 2f, Tokens.Accent);
        DrawCircle(new Vector2(size.X * .5f, size.Y * .5f), 2.5f, Tokens.Accent);
        DrawCircle(new Vector2(size.X * .5f, size.Y * .68f), 1.7f, Tokens.Accent.Alpha(.75f));
    }
}

/// <summary>Bottom status bar: severity dot, message, context counters, scale, console hint.</summary>
public partial class StatusBar : PanelContainer
{
    public Label MessageLabel => _message;
    public Label ScaleLabel => _scale;
    public Label ContextLabel => _context;
    private readonly StatusDot _dot;
    private readonly Label _message;
    private readonly Label _context;
    private readonly Label _scale;

    public StatusBar()
    {
        AddThemeStyleboxOverride("panel", Surfaces.Bar(Tokens.Raised, true, false, Tokens.Line));
        CustomMinimumSize = new Vector2(0, Tokens.Size.StatusBarHeight);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        row.AddChild(Hairline.Gap(14));
        _dot = new StatusDot(10);
        row.AddChild(_dot);

        _message = Type.Build("就绪", Typography.Regular, Type.Caption, Tokens.Text2);
        _message.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _message.VerticalAlignment = VerticalAlignment.Center;
        _message.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(_message);

        _context = Type.MonoLabel("", Tokens.Text3);
        _context.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_context);
        row.AddChild(Hairline.Gap(14));

        _scale = Type.MonoLabel("100%", Tokens.Text3);
        _scale.TooltipText = "界面缩放";
        _scale.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_scale);
        row.AddChild(Hairline.Gap(14));

        var console = Type.MonoLabel("Console  F12", Tokens.Text3);
        console.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(console);
        row.AddChild(Hairline.Gap(14));
    }

    public void Report(string message, Color? severity = null, string? context = null)
    {
        _message.Text = message;
        _message.TooltipText = message;
        _dot.SetTint(severity ?? Tokens.Success);
        if (context != null) _context.Text = context;
    }

    public void SetScaleLabel(string text) => _scale.Text = text;
}
