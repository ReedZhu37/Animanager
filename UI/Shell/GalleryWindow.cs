using Godot;
using Animanager.Core.Browse;

namespace Animanager.UI.Shell;

/// <summary>
/// Component gallery. Renders every widget in its states on one page so the design system
/// can be reviewed without hunting through the shell. Not shipped in the app.
/// </summary>
public partial class GalleryWindow : Control
{
    /// <summary>Document column width. A gallery is a reading surface, not a fluid layout.</summary>
    private const int DocumentWidth = 1080;

    public override void _Ready()
    {
        Theme = ThemeFactory.Create();
        GetWindow().Title = "Animanager · 组件画廊";
        if (DisplayServer.GetName() != "headless") GetWindow().Size = new Vector2I(1280, 1000);

        var background = new ColorRect { Color = Tokens.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        AddChild(scroll);
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // ScrollContainer sizes its child to that child's minimum. Giving the padding a fixed
        // minimum width that matches the document column is the only stable arrangement here;
        // mirroring the viewport size by hand races the layout pass.
        var pad = new MarginContainer { CustomMinimumSize = new Vector2(DocumentWidth, 0) };
        pad.AddThemeConstantOverride("margin_left", 36);
        pad.AddThemeConstantOverride("margin_right", 36);
        pad.AddThemeConstantOverride("margin_top", 24);
        pad.AddThemeConstantOverride("margin_bottom", 40);
        scroll.AddChild(pad);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 0);
        pad.AddChild(page);

        PageTitle(page, "Animanager · 组件画廊", "每个组件都在可用状态下列出，用来核对设计规则。");

        Section(page, "配色与字阶");
        page.AddChild(BuildPalette());
        page.AddChild(Hairline.Spacer(12));
        page.AddChild(BuildTypeScale());

        Section(page, "按钮");
        page.AddChild(BuildButtons());

        Section(page, "输入与筛选");
        page.AddChild(BuildInputs());

        Section(page, "导航行与侧栏收起态");
        page.AddChild(BuildNavRows());

        Section(page, "资产视图");
        page.AddChild(BuildCards());
        page.AddChild(Hairline.Spacer(14));
        page.AddChild(BuildListPreview());

        Section(page, "检查器");
        page.AddChild(BuildInspector());

        Section(page, "选择条与状态");
        page.AddChild(BuildSelectionAndStatus());

        Section(page, "预览播放控件");
        page.AddChild(BuildTransport());

        Section(page, "响应式规则");
        page.AddChild(BuildResponsiveTable());

        if (OS.GetCmdlineUserArgs().Any(arg => arg.StartsWith("--gallery-shot"))) CaptureDriver();
    }

    private void PageTitle(Container parent, string title, string subtitle)
    {
        var block = new VBoxContainer();
        block.AddThemeConstantOverride("separation", 4);
        block.AddChild(Type.DialogTitleLabel(title));
        block.AddChild(Type.CaptionLabel(subtitle, Tokens.Text2));
        block.AddChild(Hairline.Spacer(18));
        parent.AddChild(block);
    }

    private void Section(Container parent, string title)
    {
        parent.AddChild(Hairline.Spacer(20));
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        var label = Type.StrongLabel(title, Tokens.Text, Type.SectionTitle);
        header.AddChild(label);
        var rule = new ColorRect { Color = Tokens.Line, CustomMinimumSize = new Vector2(0, 1), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        rule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(rule);
        parent.AddChild(header);
        parent.AddChild(Hairline.Spacer(12));
    }

    private Control BuildPalette()
    {
        var wrap = new HFlowContainer();
        wrap.AddThemeConstantOverride("h_separation", 10);
        wrap.AddThemeConstantOverride("v_separation", 10);
        foreach (var (name, colour) in new[]
                 {
                     ("bg/base", Tokens.Bg), ("bg/panel", Tokens.Panel), ("bg/raised", Tokens.Raised),
                     ("bg/card", Tokens.Card), ("bg/sunken", Tokens.Sunken), ("line", Tokens.Line),
                     ("line+", Tokens.LineStrong), ("text", Tokens.Text), ("text-2", Tokens.Text2),
                     ("text-3", Tokens.Text3), ("accent", Tokens.Accent), ("accent-wash", Tokens.AccentWash),
                     ("info", Tokens.Info), ("success", Tokens.Success), ("warn", Tokens.Warn),
                     ("danger", Tokens.Danger)
                 })
        {
            var swatch = new PanelContainer { CustomMinimumSize = new Vector2(112, 58) };
            swatch.AddThemeStyleboxOverride("panel", Surfaces.Fill(colour, Tokens.Radius.Control, 8, 6,
                Tokens.LineStrong));
            var label = Type.Build(name, Typography.Mono, Type.Micro, Contrast(colour));
            label.VerticalAlignment = VerticalAlignment.Bottom;
            swatch.AddChild(label);
            wrap.AddChild(swatch);
        }
        return wrap;
    }

    private static Color Contrast(Color background) =>
        background.R * .299f + background.G * .587f + background.B * .114f > .55f ? Tokens.Bg : Tokens.Text;

    private Control BuildTypeScale()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        foreach (var (name, sample) in new[]
                 {
                     ("19/600 弹窗标题", Type.DialogTitleLabel("弹窗标题 Dialog title")),
                     ("15/600 分区标题", Type.SectionTitleLabel("分区标题 Section title")),
                     ("14/600 品牌", Type.StrongLabel("Animanager", Tokens.Text, Type.Brand)),
                     ("13/400 正文", Type.BodyLabel("正文文字正文文字 Body text")),
                     ("12/600 名称", Type.StrongLabel("UE5_Quinn_Idle")),
                     ("11/400 次要", Type.CaptionLabel("次要说明 secondary caption")),
                     ("10/mono 数字", Type.MonoLabel("1,204 · 2.46 s · 60 fps · a1b2c3d4")),
                 })
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var tag = Type.MonoLabel(name, Tokens.Text3);
            tag.CustomMinimumSize = new Vector2(150, 0);
            row.AddChild(tag);
            row.AddChild(sample);
            column.AddChild(row);
        }
        return column;
    }

    private Control BuildButtons()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);

        var primary = new HBoxContainer();
        primary.AddThemeConstantOverride("separation", 8);
        primary.AddChild(new CapButton(Icons.Export, "导出", primary: true));
        primary.AddChild(new CapButton(Icons.Import, "导入"));
        primary.AddChild(new CapButton(Icons.Filter, "筛选"));
        primary.AddChild(new CapButton(Icons.Layers, "任务"));
        var disabled = new CapButton(Icons.Export, "禁用态");
        disabled.Disabled = true;
        primary.AddChild(disabled);
        column.AddChild(Row("标准 / 主要 / 禁用", primary));

        var icons = new HBoxContainer();
        icons.AddThemeConstantOverride("separation", 4);
        foreach (var icon in new[] { Icons.Grid, Icons.List, Icons.Search, Icons.Star, Icons.Trash, Icons.Filter,
                     Icons.More, Icons.Play, Icons.Pause, Icons.Repeat, Icons.Expand, Icons.ChevronDown,
                     Icons.Check, Icons.Gear, Icons.Cube, Icons.Folder, Icons.Tag, Icons.Film, Icons.Close })
            icons.AddChild(new IconButton(icon, 17, icon));
        column.AddChild(Row("图标按钮（20 个矢量图标）", icons));

        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 6);
        chips.AddThemeConstantOverride("v_separation", 6);
        chips.AddChild(new FilterChip("skeleton", "骨架 UE5_Manny"));
        chips.AddChild(new FilterChip("duration", "时长 0.5–4 s"));
        chips.AddChild(new FilterChip("fps", "帧率 60"));
        var plain = new Button { Text = "普通 chip", ThemeTypeVariation = "Chip", CustomMinimumSize = new Vector2(0, 24) };
        chips.AddChild(plain);
        column.AddChild(Row("筛选 chip", chips));

        var badges = new HBoxContainer();
        badges.AddThemeConstantOverride("separation", 8);
        badges.AddChild(new BadgePill("2", Tokens.Accent));
        badges.AddChild(new BadgePill("动画", Tokens.Accent));
        badges.AddChild(new BadgePill("FBX", Tokens.Text2));
        badges.AddChild(new BadgePill("12", Tokens.Warn));
        column.AddChild(Row("徽标", badges));
        return column;
    }

    /// <summary>
    /// Label column plus content. A GridContainer is used because it gives the label column a
    /// real width and lets the content column absorb the remainder, without both children
    /// competing for ExpandFill and collapsing the label to zero.
    /// </summary>
    private Control Row(string label, Control content)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);

        var tag = Type.MonoLabel(label, Tokens.Text3);
        tag.CustomMinimumSize = new Vector2(
            Math.Max(150, Typography.Width(Typography.Mono, label, Type.Micro) + 12), 0);
        tag.VerticalAlignment = VerticalAlignment.Center;
        grid.AddChild(tag);

        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(content);
        return grid;
    }

    private Control BuildInputs()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);

        var search = new SearchField("搜索资产名称或标签", "Ctrl+F") { CustomMinimumSize = new Vector2(360, 30) };
        column.AddChild(Row("搜索框（空）", search));

        var filled = new SearchField("搜索资产名称或标签", "Ctrl+F") { CustomMinimumSize = new Vector2(360, 30) };
        filled.SetValue("Nodachi");
        column.AddChild(Row("搜索框（有值）", filled));

        var entry = new LineEdit { Text = "例如：行走 战斗 Humanoid", CustomMinimumSize = new Vector2(360, 30) };
        column.AddChild(Row("文本输入", entry));

        var sort = new OptionButton { CustomMinimumSize = new Vector2(160, 30) };
        foreach (var item in new[] { "最近导入", "名称 A→Z", "时长", "大小" }) sort.AddItem(item);
        column.AddChild(Row("下拉框", sort));

        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 6);
        chips.AddChild(new FilterChip("a", "骨架 UE5_Manny"));
        chips.AddChild(new FilterChip("b", "帧率 60"));
        chips.AddChild(new Button { Text = "清除全部", ThemeTypeVariation = "GhostButton", CustomMinimumSize = new Vector2(0, 24) });
        column.AddChild(Row("筛选条件条", chips));
        return column;
    }

    private Control BuildNavRows()
    {
        var column = new HBoxContainer();
        column.AddThemeConstantOverride("separation", 24);

        var expanded = new VBoxContainer();
        expanded.CustomMinimumSize = new Vector2(210, 0);
        var all = new NavRow(Icons.Layers, "全部资产");
        all.SetActive(true);
        all.SetCount(744);
        expanded.AddChild(all);
        var anim = new NavRow(Icons.Film, "动画片段");
        anim.SetCount(741);
        expanded.AddChild(anim);
        var model = new NavRow(Icons.Cube, "模型与骨架");
        model.SetCount(3);
        expanded.AddChild(model);
        var fav = new NavRow(Icons.Star, "我的收藏");
        expanded.AddChild(fav);
        var trash = new NavRow(Icons.Trash, "回收站");
        trash.SetCount(5);
        expanded.AddChild(trash);
        column.AddChild(WithLabel("展开态 210px", expanded));

        var tags = new VBoxContainer();
        tags.CustomMinimumSize = new Vector2(210, 0);
        tags.AddChild(new NavSection("集合", () => { }));
        tags.AddChild(new NavRow(Icons.Folder, "连招"));
        tags.AddChild(new NavRow(Icons.Folder, "位移"));
        tags.AddChild(new NavSection("标签", () => { }));
        var tagFlow = new HFlowContainer();
        tagFlow.AddThemeConstantOverride("h_separation", 6);
        foreach (var (name, count) in new[] { ("战斗", 214), ("跑步", 88), ("Humanoid", 402) })
        {
            var chip = new Button { Text = $"{name} {count}", ThemeTypeVariation = "Chip", CustomMinimumSize = new Vector2(0, 22) };
            chip.AddThemeFontSizeOverride("font_size", Type.Caption);
            tagFlow.AddChild(chip);
        }
        tags.AddChild(tagFlow);
        column.AddChild(WithLabel("分组与标签", tags));

        var rail = new VBoxContainer();
        rail.AddThemeConstantOverride("separation", 4);
        rail.CustomMinimumSize = new Vector2(50, 0);
        foreach (var icon in new[] { Icons.Layers, Icons.Film, Icons.Cube, Icons.Star, Icons.Trash })
        {
            var button = new IconButton(icon, 17, icon);
            button.CustomMinimumSize = new Vector2(34, 32);
            rail.AddChild(button);
        }
        column.AddChild(WithLabel("收起态 50px", rail));
        return column;
    }

    private Control WithLabel(string label, Control content)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        column.AddChild(Type.MonoLabel(label, Tokens.Text3));
        var surface = new PanelContainer();
        surface.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 8, 8, Tokens.Line));
        surface.AddChild(content);
        column.AddChild(surface);
        return column;
    }

    private Control BuildCards()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var states = new (string Label, bool Selected, bool Favorite, bool Animation, bool Pending, string Name)[]
        {
            ("默认", false, false, false, false, "UE5_Manny"),
            ("选中", true, false, false, false, "UE5_Quinn"),
            ("收藏 + 动画", false, true, true, false, "Idle_Combo01"),
            ("读取中", false, false, true, true, "Nodachi_Animations…"),
        };
        foreach (var (label, selected, favorite, animation, pending, name) in states)
        {
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 6);
            column.AddChild(Type.MonoLabel(label, Tokens.Text3));
            column.AddChild(CardPreview(name, selected, favorite, animation, pending));
            row.AddChild(column);
        }
        return row;
    }

    /// <summary>A single card drawn through the real grid, so the gallery cannot drift from it.</summary>
    private Control CardPreview(string name, bool selected, bool favorite, bool animation, bool pending)
    {
        var host = new Control
        {
            CustomMinimumSize = new Vector2(Tokens.CardMetrics.Width + Tokens.CardMetrics.PadX * 2,
                Tokens.CardMetrics.Height + Tokens.CardMetrics.PadY * 2)
        };
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = host.CustomMinimumSize
        };
        scroll.Modulate = Tokens.Text;
        host.AddChild(scroll);
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var grid = new Shell.AssetGrid();
        scroll.AddChild(grid);
        grid.Bind(scroll);
        var card = new AssetCardModel
        {
            Id = name,
            Name = name,
            MetaLine = "FBX · 2.5 MB",
            DetailLine = animation ? "2.0s · 60fps · UE5_Manny" : "根 · 89 骨骼",
            IsAnimation = animation,
            Favorite = favorite,
            ThumbnailPending = pending
        };
        grid.SetCards([card]);
        if (selected) grid.SelectSingle(name);
        return host;
    }

    /// <summary>
    /// The list view, drawn through the real component so the gallery cannot drift from it.
    /// Rows carry deliberately varied data (some unknown) to show the empty-cell behaviour.
    /// </summary>
    private Control BuildListPreview()
    {
        var host = new PanelContainer { CustomMinimumSize = new Vector2(1000, 8 * AssetList.RowHeight + AssetList.HeaderHeight + 12) };
        host.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 0, 0, Tokens.Line));

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        host.AddChild(scroll);

        var list = new AssetList();
        scroll.AddChild(list);
        list.Bind(scroll);

        var rows = new List<AssetCardModel>
        {
            Row("UE5_Quinn", "动画", 2.4, 60, "UE5_Manny", "2.5 MB", "11-14 15:53", true),
            Row("UE5_Manny_Idle", "模型", null, null, "UE5_Manny", "4.2 MB", "11-14 15:53"),
            Row("UE5_Manny", "模型", null, null, "UE5_Manny", "2.8 MB", "11-14 15:53"),
            Row("UE4_Mannequin", "模型", null, null, "UE4_Mannequin", "992.1 KB", "11-14 15:53"),
            Row("Nodachi_Animations_Sequence_Combo03", "动画", 6.12, 60, "UE5_Manny", "249.9 KB", "11-14 15:07"),
            Row("Nodachi_Animations_Sequence_Turn_L", "动画", 0.88, 60, "UE5_Manny", "412.4 KB", "11-14 15:07"),
            Row("Nodachi_Animations_Sequence_Roll", "动画", 1.5, null, "", "320.5 KB", "11-14 15:07")
        };
        list.SetRows(rows);
        list.BindSelection(new AssetSelection());
        list.Reflow();
        return WithLabel("列表视图（可排序表头，列随宽度增减）", host);
    }

    private static AssetCardModel Row(string name, string kind, double? duration, double? fps, string skeleton,
        string size, string imported, bool favorite = false) => new()
    {
        Id = name,
        Name = name,
        IsAnimation = kind == "动画",
        Favorite = favorite,
        SizeText = size,
        ImportedText = imported,
        SkeletonText = skeleton,
        DurationText = duration is { } d ? $"{d:0.##}s" : "",
        FpsText = fps is { } f ? $"{f:0}" : ""
    };

    private Control BuildInspector()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var inspector = new Inspector();
        var clipList = new ClipList();
        var cards = new AssetCardModel
        {
            Id = "assets/47b6f756f6f77d0fb407e31dffe23f3cc0ece6977aa73",
            Name = "Idle_Combo01",
            Tags = "战斗 转刀 Humanoid",
            VirtualPath = "assets/47b6f756f6f77d0fb407e31dffe23f3cc0ece6977aa73",
            ImportedAt = "2026-11-14T15:53:00",
            Size = 2621440,
            IsAnimation = true
        };
        inspector.ShowAsset(cards,
            new AssetAttributes([new AssetSkeleton("ue5", "UE5_Manny")], 2.4, 60), 161, 1, ".fbx",
            [("Idle_Combo01", 2.4), ("Idle_Breath", 3.1), ("Turn_L", 1.2)]);
        inspector.CustomMinimumSize = new Vector2(Tokens.Size.Inspector, 520);
        row.AddChild(WithLabel("有选中", inspector));

        var empty = new Inspector { CustomMinimumSize = new Vector2(Tokens.Size.Inspector, 520) };
        empty.ShowEmpty();
        row.AddChild(WithLabel("空态", empty));
        return row;
    }

    private Control BuildSelectionAndStatus()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);

        var bar = new SelectionBar();
        bar.SetCount(4);
        column.AddChild(WithLabel("多选条", bar));

        var status = new StatusBar();
        status.Report("已载入 Idle_Combo01", Tokens.Success, "744 个资产");
        column.AddChild(WithLabel("状态栏 · 成功", status));

        foreach (var (message, severity, label) in new[]
                 {
                     ("正在读取骨架选项…", Tokens.Info, "状态栏 · 进度"),
                     ("排序需要数据层参数", Tokens.Warn, "状态栏 · 警告"),
                     ("无法预览：文件缺失", Tokens.Danger, "状态栏 · 错误")
                 })
        {
            var bar2 = new StatusBar();
            bar2.Report(message, severity, "744 个资产");
            column.AddChild(WithLabel(label, bar2));
        }
        return column;
    }

    private Control BuildTransport()
    {
        var stage = new PreviewStage { CustomMinimumSize = new Vector2(520, 340) };
        stage.SetTitle("Idle_Combo01", "89 骨骼  ·  1 网格  ·  3 片段");
        stage.SetClips(["Idle_Combo01", "Idle_Breath", "Turn_L"], "Idle_Combo01");
        stage.SetTime(1.24, 2.4);
        stage.SetPlaying(true);
        return WithLabel("预览台（含播放控件与片段列）", stage);
    }

    private Control BuildResponsiveTable()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        column.AddChild(Type.CaptionLabel(
            "布局按实测最小宽度选择，而不是固定断点。偏好顺序如下，选中第一个能放下的方案："));
        column.AddChild(Hairline.Spacer(6));
        foreach (var (label, detail) in new[]
                 {
                     ("1", "侧栏展开 + 检查器 + 预览 + 3 列"),
                     ("2", "侧栏收起 + 检查器 + 预览 + 3 列"),
                     ("3", "侧栏展开 + 检查器 + 预览 + 2 列"),
                     ("4", "侧栏收起 + 检查器 + 预览 + 2 列"),
                     ("5", "侧栏展开 + 无检查器 + 预览 + 3 列"),
                     ("6", "侧栏收起 + 无检查器 + 预览 + 3 列"),
                     ("7", "侧栏收起 + 无检查器 + 预览 + 2 列"),
                     ("8", "侧栏收起 + 无检查器 + 无预览 + 2 列")
                 })
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var index = Type.MonoLabel(label, Tokens.Accent);
            index.CustomMinimumSize = new Vector2(20, 0);
            row.AddChild(index);
            row.AddChild(Type.CaptionLabel(detail, Tokens.Text2));
            column.AddChild(row);
        }
        column.AddChild(Hairline.Spacer(8));
        column.AddChild(Type.CaptionLabel(
            "正在使用的列宽：侧栏 210 / 收起 50 · 资产 2 列 356 · 预览 ≥330 · 检查器 292", Tokens.Text3));
        return column;
    }

    private async void CaptureDriver()
    {
        var arg = OS.GetCmdlineUserArgs().First(a => a.StartsWith("--gallery-shot"));
        var folder = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : ".runtime/gallery";
        Directory.CreateDirectory(folder);
        var window = GetWindow();
        window.Size = new Vector2I(1440, 1000);
        for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var scroll = GetChild<ScrollContainer>(1);
        float pageHeight = ((Control)scroll.GetChild(0)).GetCombinedMinimumSize().Y;
        int slice = 0;
        for (float offset = 0; offset < pageHeight; offset += 940)
        {
            scroll.ScrollVertical = (int)offset;
            for (int i = 0; i < 6; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (!image.IsEmpty()) image.SavePng(Path.Combine(folder, $"gallery-{slice:00}.png"));
            slice++;
            if (slice > 8) break;
        }
        GetTree().Quit();
    }
}
