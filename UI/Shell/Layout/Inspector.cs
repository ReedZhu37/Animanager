using Godot;
using Animanager.Core.Browse;
using Animanager.Core.Models;

namespace Animanager.UI.Shell;

/// <summary>Right-hand inspector. Owns no data: callers push a card model plus attributes.</summary>
public partial class Inspector : PanelContainer
{
    private readonly VBoxContainer _empty;
    private readonly Control _emptyHost;
    private readonly VBoxContainer _content;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly BadgePill _kindPill;
    private readonly HFlowContainer _tagChips;
    private readonly ClipList _clipList;
    private readonly Button _addTag, _saveTags;

    private FieldRow? _duration, _fps, _skeleton, _bones, _format, _size, _imported, _path, _library;

    public Label TitleLabel => _title;
    public Label Summary { get; } = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    public Label Readiness { get; } = new() { Name = "AssetReadiness", AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "未检查" };
    internal VBoxContainer WeaponOverview { get; } = new();
    public LineEdit TagsInput { get; } = new() { Name = "AssetTags", PlaceholderText = "标签以空格分隔" };
    public Button FavoriteButton { get; private set; } = null!;
    public event Action? TagsSaved;
    public event Action? MoreRequested;
    public event Action<string>? TagSelected;
    public event Action<string>? ClipSelected;
    public event Action? FavoriteToggled;
    public event Action? TagAddRequested;

    public Inspector()
    {
        AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, 0));
        CustomMinimumSize = new Vector2(Tokens.Size.Inspector, 0);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        AddChild(scroll);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(root);

        _empty = BuildEmptyState(out var emptyHost);
        _emptyHost = emptyHost;
        root.AddChild(emptyHost);

        _content = new VBoxContainer { Visible = false };
        _content.AddThemeConstantOverride("separation", 0);
        root.AddChild(_content);

        // Header
        var header = new MarginContainer();
        header.AddThemeConstantOverride("margin_left", 16);
        header.AddThemeConstantOverride("margin_right", 10);
        header.AddThemeConstantOverride("margin_top", 12);
        header.AddThemeConstantOverride("margin_bottom", 10);
        _content.AddChild(header);

        var headerColumn = new VBoxContainer();
        headerColumn.AddThemeConstantOverride("separation", 6);
        header.AddChild(headerColumn);

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 4);
        headerColumn.AddChild(titleRow);

        _title = Type.Build("", Typography.Semi, Type.SectionTitle, Tokens.Text);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.VerticalAlignment = VerticalAlignment.Center;
        titleRow.AddChild(_title);

        var favourite = new IconButton(Icons.StarOutline, 15, "收藏");
        favourite.Pressed += () => FavoriteToggled?.Invoke();
        titleRow.AddChild(favourite); FavoriteButton = favourite;
        var more = new IconButton(Icons.MoreVertical, 15, "更多操作");
        titleRow.AddChild(more); more.Pressed += () => MoreRequested?.Invoke();

        var metaRow = new HFlowContainer();
        metaRow.AddThemeConstantOverride("h_separation", 6);
        headerColumn.AddChild(metaRow);
        _kindPill = new BadgePill("动画", Tokens.Accent);
        metaRow.AddChild(_kindPill);
        _subtitle = Type.Build("", Typography.Mono, Type.Micro, Tokens.Text3);
        _subtitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _subtitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        metaRow.AddChild(_subtitle);

        _content.AddChild(Hairline.Horizontal());

        headerColumn.AddChild(Summary);
        Readiness.AddThemeFontSizeOverride("font_size", Type.Caption);
        headerColumn.AddChild(Readiness);
        var overview = new InspectorSection("概览");
        _content.AddChild(overview);
        _duration = NewField(overview, "最长片段", true);
        _fps = NewField(overview, "源帧率", true);
        _skeleton = NewField(overview, "骨架", false);
        _bones = NewField(overview, "骨骼 / 网格", true);
        overview.Body.AddChild(WeaponOverview);

        var file = new InspectorSection("文件");
        _content.AddChild(file);
        _format = NewField(file, "格式", false);
        _size = NewField(file, "大小", true);
        _imported = NewField(file, "导入", true);
        _library = NewField(file, "来源库", false);
        _path = NewField(file, "库路径", true);

        var tags = new InspectorSection("标签");
        _content.AddChild(tags);
        _tagChips = new HFlowContainer();
        _tagChips.AddThemeConstantOverride("h_separation", 5);
        _tagChips.AddThemeConstantOverride("v_separation", 5);
        tags.Body.AddChild(_tagChips);
        _addTag = new Button
        {
            Name = "AssetAddTag", Text = "添加标签", ThemeTypeVariation = "GhostButton", CustomMinimumSize = new Vector2(0, 24),
            Alignment = HorizontalAlignment.Left
        };
        _addTag.AddThemeFontSizeOverride("font_size", Type.Caption);
        _addTag.Pressed += () => TagAddRequested?.Invoke();
        tags.Body.AddChild(_addTag);
        tags.Body.AddChild(TagsInput);
        _saveTags = new Button { Text = "保存标签" }; tags.Body.AddChild(_saveTags);
        _saveTags.Pressed += () => TagsSaved?.Invoke(); TagsInput.TextSubmitted += _ => TagsSaved?.Invoke();

        var animations = new InspectorSection("动画片段");
        _content.AddChild(animations);
        _clipList = new ClipList();
        _clipList.ClipSelected += clip => ClipSelected?.Invoke(clip);
        animations.Body.AddChild(_clipList);

        _content.AddChild(Hairline.Spacer(18));
        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
    }

    private VBoxContainer BuildEmptyState(out Control host)
    {
        var empty = new VBoxContainer();
        empty.AddThemeConstantOverride("separation", 10);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 56);
        pad.AddChild(empty);
        host = pad;

        var icon = new IconView(Icons.Panel, 32, Tokens.Text3);
        icon.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        empty.AddChild(icon);

        var title = Type.Build("未选择资产", Typography.Semi, Type.Body, Tokens.Text2);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        empty.AddChild(title);

        var hint = Type.Build("在网格中选择资产，查看属性、\n动画片段与重定向配置", Typography.Regular, Type.Caption,
            Tokens.Text3);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        empty.AddChild(hint);
        return empty;
    }

    private static FieldRow NewField(InspectorSection section, string label, bool mono)
    {
        var row = new FieldRow(label, "—", mono);
        section.Body.AddChild(row);
        return row;
    }

    public void ShowEmpty()
    {
        _emptyHost.Visible = true;
        _content.Visible = false;
    }

    public void SetEditable(bool editable)
    {
        TagsInput.Editable = editable;
        _addTag.Disabled = _saveTags.Disabled = FavoriteButton.Disabled = !editable;
    }

    public void ShowAsset(AssetCardModel card, AssetAttributes? attributes, int boneCount, int meshCount,
        string sourceFormat, IEnumerable<(string Name, double? Seconds)> clips, string? currentClip = null)
    {
        _emptyHost.Visible = false;
        _content.Visible = true;

        FavoriteButton.Text = "";
        ((IconButton)FavoriteButton).SetIcon(card.Favorite ? Icons.Star : Icons.StarOutline);
        FavoriteButton.TooltipText = card.Favorite ? "取消收藏" : "收藏";
        if (!TagsInput.HasFocus()) TagsInput.Text = card.Tags;
        _title.Text = card.Name;
        _title.TooltipText = card.Name;
        _kindPill.SetText(card.IsAnimation ? "动画" : "模型");
        _subtitle.Text = sourceFormat.TrimStart('.').ToUpperInvariant() + "  ·  " + card.Id;
        _subtitle.TooltipText = _subtitle.Text;

        _duration?.SetValue(attributes?.Duration is { } seconds ? $"{seconds:0.###} s" : "未知");
        _fps?.SetValue(attributes?.SourceFps is { } fps ? $"{fps:0.###} fps" : "未知");
        _skeleton?.SetValue(attributes is { Skeletons.Length: > 0 }
            ? string.Join("、", attributes.Skeletons.Select(s => s.Name))
            : "未知");
        _bones?.SetValue(boneCount > 0 ? $"{boneCount} / {meshCount}" : "—");
        _format?.SetValue(sourceFormat.TrimStart('.').ToUpperInvariant());
        _size?.SetValue(card.Size >= 1048576 ? $"{card.Size / 1048576.0:F1} MB" : $"{card.Size / 1024.0:F1} KB");
        _imported?.SetValue(DateTime.TryParse(card.ImportedAt, out var imported)
            ? imported.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未知");
        _library?.SetValue(card.LibraryName);
        _path?.SetValue(card.VirtualPath);

        foreach (var child in _tagChips.GetChildren())
        {
            _tagChips.RemoveChild(child);
            child.QueueFree();
        }
        var tags = card.Tags.Split([' ', ',', '，', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.Length == 0) _tagChips.AddChild(Type.CaptionLabel("没有标签", Tokens.Text3));
        foreach (var tag in tags)
        {
            var chip = new Button { Text = tag, CustomMinimumSize = new Vector2(0, 22), ThemeTypeVariation = "Chip" };
            chip.AddThemeFontSizeOverride("font_size", Type.Caption);
            chip.Pressed += () => TagSelected?.Invoke(tag);
            _tagChips.AddChild(chip);
        }

        _clipList.SetClips(clips, currentClip);
    }

}
