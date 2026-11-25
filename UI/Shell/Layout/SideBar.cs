using Animanager.Core.Storage;
using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Left navigation. Expands to 210px, collapses to a 50px glyph rail. Collapsing does not
/// leave an empty column: the rail keeps every category reachable by icon.
/// </summary>
public partial class SideBar : PanelContainer
{
    public event Action<string>? CategorySelected;
    public event Action<string>? CollectionSelected;
    public event Action? OpenLibraryFolder;
    public event Action<string>? OpenLibrarySelected;
    public event Action<string>? CloseLibraryRequested;
    public event Action? CreateCollection;
    public event Action? CreateTag;
    public event Action? CreateTagGroup;
    public event Action<string>? EditTagGroup;
    public event Action<string>? TagSelected;
    public event Action<string>? TagGroupSelected;
    internal event Action<string, string[]>? AssetsDroppedOnTag;
    private readonly VBoxContainer _collections = new();
    private readonly VBoxContainer _openLibraries = new() { Name = "OpenLibrariesList", Visible = false };
    private readonly MenuButton _collectionRail = new() { Text = "集合", TooltipText = "选择集合" };

    public bool Collapsed { get; private set; }
    public string ActiveCategory { get; private set; } = "All";

    private static readonly (string Key, string Icon, string Title)[] Categories =
    [
        ("All", Icons.Layers, "全部资产"),
        ("Animation", Icons.Film, "动画片段"),
        ("Model", Icons.Cube, "模型与骨架"),
        ("Favorites", Icons.Star, "我的收藏"),
        ("Trash", Icons.Trash, "回收站")
    ];

    private readonly VBoxContainer _stack;
    private readonly VBoxContainer _rail;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _tags;
    private readonly List<(string Name, Button Chip)> _tagButtons = [];
    private readonly HashSet<string> _foldedTagGroups = [];
    private readonly Dictionary<string, NavRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IconButton> _railButtons = new(StringComparer.Ordinal);

    public SideBar()
    {
        AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, 0));
        CustomMinimumSize = new Vector2(Tokens.Size.Sidebar, 0);

        // Collapsed rail: same categories, glyphs only.
        var railHost = new MarginContainer { Visible = false };
        railHost.AddThemeConstantOverride("margin_top", 10);
        railHost.AddThemeConstantOverride("margin_left", 8);
        railHost.AddThemeConstantOverride("margin_right", 8);
        AddChild(railHost);
        _rail = new VBoxContainer();
        _rail.AddThemeConstantOverride("separation", 4);
        railHost.AddChild(_rail);
        _railHost = railHost;

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        AddChild(_scroll);

        _stack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _stack.AddThemeConstantOverride("separation", 1);
        _scroll.AddChild(_stack);

        _stack.AddChild(Hairline.Spacer(10));
        _stack.AddChild(new NavSection("资产"));
        foreach (var (key, icon, title) in Categories)
        {
            AddCategory(key, icon, title);
            AddRailButton(key, icon, title);
        }

        _stack.AddChild(_openLibraries);

        _stack.AddChild(Hairline.Spacer(12));
        var collectionHeading = new NavSection("集合", () => CreateCollection?.Invoke(), "新建集合")
        {
            Name = "CollectionRootDrop",
            TooltipText = "将子集合拖到这里可移到顶层"
        };
        EnableCollectionRootDrop(collectionHeading);
        _stack.AddChild(collectionHeading);
        _collections.AddThemeConstantOverride("separation", 0);
        _stack.AddChild(_collections);
        _rail.AddChild(_collectionRail);
        _collectionRail.GetPopup().IdPressed += id =>
        {
            var key = _collectionRail.GetPopup().GetItemMetadata((int)id).AsString();
            RevealCollection(key);
            CollectionSelected?.Invoke(key);
        };
        var expand = new IconButton(Icons.Panel, 16, "展开侧栏");
        expand.Pressed += () => SetCollapsed(false);
        _rail.AddChild(expand);

        _stack.AddChild(Hairline.Spacer(12));
        var tagHeading = new NavSection("标签");
        var createTag = TagGroupButton("新建标签", "创建可用于任何资产的标签");
        createTag.Name = "CreateAppTag";
        createTag.Pressed += () => CreateTag?.Invoke();
        tagHeading.AddAction(createTag);
        var createGroup = TagGroupButton("新建组", "新建标签组");
        createGroup.Pressed += () => CreateTagGroup?.Invoke();
        tagHeading.AddAction(createGroup);
        _stack.AddChild(tagHeading);
        _tags = new VBoxContainer();
        var tagPad = new MarginContainer();
        tagPad.AddThemeConstantOverride("margin_left", 14);
        tagPad.AddThemeConstantOverride("margin_right", 12);
        tagPad.AddChild(_tags);
        _stack.AddChild(tagPad);

        _stack.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _stack.AddChild(BuildFooter());

        _rows["All"].SetActive(true);
        _railButtons["All"].SetActive(true);
    }

    private Control? _railHost;

    private void AddRailButton(string key, string icon, string title)
    {
        var button = new IconButton(icon, 17, title);
        button.CustomMinimumSize = new Vector2(34, 32);
        if (key == "Favorites") EnableFavoritesDrop(button);
        button.Pressed += () =>
        {
            SetActive(key);
            CategorySelected?.Invoke(key);
        };
        _rail.AddChild(button);
        _railButtons[key] = button; button.Name = "Category" + key;
    }

    private Control BuildFooter()
    {
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 2);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 10);
        pad.AddThemeConstantOverride("margin_right", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        pad.AddChild(foot);

        var folder = new NavRow(Icons.Folder, "打开库目录");
        folder.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        folder.Pressed += () => OpenLibraryFolder?.Invoke();
        foot.AddChild(folder);

        var collapse = new IconButton(Icons.Panel, 16, "收起侧栏");
        collapse.Pressed += () => SetCollapsed(!Collapsed);
        foot.AddChild(collapse);
        return pad;
    }

    private void AddCategory(string key, string icon, string title)
    {
        var row = new NavRow(icon, title);
        if (key == "Favorites")
        {
            row.TooltipText = "拖入资产即可收藏";
            EnableFavoritesDrop(row);
        }
        row.Pressed += () =>
        {
            SetActive(key);
            CategorySelected?.Invoke(key);
        };
        _rows[key] = row;
        _stack.AddChild(row);
    }

    internal void SetOpenLibraries((string Id, string Name, string Tooltip)[] entries)
    {
        foreach (var child in _openLibraries.GetChildren()) { _openLibraries.RemoveChild(child); child.QueueFree(); }
        _openLibraries.Visible = entries.Length > 0;
        if (entries.Length == 0) return;

        _openLibraries.AddChild(new NavSection("已打开的库"));
        foreach (var (id, name, tooltip) in entries)
        {
            var line = new HBoxContainer();
            var row = new NavRow(Icons.Folder, name)
            {
                TooltipText = tooltip, FocusMode = FocusModeEnum.All, SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            row.Pressed += () => OpenLibrarySelected?.Invoke(id);
            line.AddChild(row);
            var close = new IconButton(Icons.Close, 13, $"移出浏览 · {name}")
            {
                Name = "CloseLibrary" + id, FocusMode = FocusModeEnum.All
            };
            close.Pressed += () => CloseLibraryRequested?.Invoke(id);
            line.AddChild(close);
            _openLibraries.AddChild(line);
        }
    }

    internal void SetTagSections((string GroupId, string Group, (string Name, int Count)[] Tags)[] sections)
    {
        HighlightDrop(null);
        _foldedTagGroups.IntersectWith(sections.Select(section => section.GroupId));
        _tagButtons.Clear();
        foreach (var child in _tags.GetChildren())
        {
            _tags.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var (groupId, group, tags) in sections)
        {
            var heading = new HBoxContainer();
            heading.AddThemeConstantOverride("separation", 2);
            var flow = new HFlowContainer { Visible = !_foldedTagGroups.Contains(groupId) };
            var disclosure = new IconButton(Icons.ChevronDown, 11)
            {
                Name = "TagGroupDisclosure" + groupId, Disabled = tags.Length == 0
            };
            void SyncDisclosure()
            {
                disclosure.SetIcon(flow.Visible ? Icons.ChevronDown : Icons.ChevronRight);
                disclosure.TooltipText = (flow.Visible ? "折叠 " : "展开 ") + group;
            }
            SyncDisclosure();
            disclosure.Pressed += () =>
            {
                if (!_foldedTagGroups.Add(groupId)) _foldedTagGroups.Remove(groupId);
                flow.Visible = !_foldedTagGroups.Contains(groupId);
                SyncDisclosure();
            };
            heading.AddChild(disclosure);
            var title = Type.Build(group, Typography.Semi, Type.Caption, Tokens.Text2);
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            title.TooltipText = group;
            title.MouseFilter = MouseFilterEnum.Pass;
            heading.AddChild(title);
            var union = TagGroupButton("并集", $"显示包含「{group}」中任一标签的资产");
            union.Name = "TagGroupUnion" + groupId;
            union.Disabled = tags.Length == 0;
            union.Pressed += () => TagGroupSelected?.Invoke(groupId);
            heading.AddChild(union);
            if (groupId != AppTagCatalog.AllGroupId)
            {
                var edit = TagGroupButton("编辑", $"编辑标签组「{group}」");
                edit.Pressed += () => EditTagGroup?.Invoke(groupId);
                heading.AddChild(edit);
            }
            _tags.AddChild(heading);
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            _tags.AddChild(flow);
            foreach (var (name, count) in tags)
            {
                string label = $"{name} {count}";
                var chip = new Button
                {
                    Text = Typography.Fit(Typography.Regular, label, Type.Caption, 140),
                    TooltipText = label + "\n点击切换筛选，拖入资产可添加标签", ToggleMode = true,
                    CustomMinimumSize = new Vector2(0, 22), ThemeTypeVariation = "Chip"
                };
                chip.AddThemeFontSizeOverride("font_size", Type.Caption);
                chip.Pressed += () => TagSelected?.Invoke(name);
                // Saving can rebuild the tag chips, so wait until Godot finishes dispatching the drop.
                EnableEditableAssetDrop(chip, keys => Callable.From(() => AssetsDroppedOnTag?.Invoke(name, keys)).CallDeferred(),
                    assets => $"给 {assets} 个资产添加「{name}」");
                flow.AddChild(chip);
                _tagButtons.Add((name, chip));
            }
        }
    }

    private static Button TagGroupButton(string text, string tooltip)
    {
        var button = new Button
        {
            Text = text, TooltipText = tooltip, Flat = true,
            MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(0, 24)
        };
        button.AddThemeFontSizeOverride("font_size", Type.Caption);
        return button;
    }

    internal void SetSelectedTags(IReadOnlySet<string> tags)
    {
        foreach (var (name, chip) in _tagButtons) chip.SetPressedNoSignal(tags.Contains(name));
    }

    public void SetCount(string key, int count)
    {
        if (_rows.TryGetValue(key, out var row)) row.SetCount(count);
        if (_railButtons.TryGetValue(key, out var button))
        {
            var tip = Categories.First(c => c.Key == key).Title + " · " + count.ToString("N0");
            button.TooltipText = key == "Favorites" ? tip + "\n拖入资产即可收藏" : tip;
        }
    }

    public void SetActive(string key)
    {
        foreach (var pair in _rows) pair.Value.SetActive(pair.Key == key);
        foreach (var pair in _railButtons) pair.Value.SetActive(pair.Key == key);
        ActiveCategory = key;
        var menu = _collectionRail.GetPopup();
        for (int i = 0; i < menu.ItemCount; i++) menu.SetItemChecked(i, menu.GetItemMetadata(i).AsString() == key);
    }

    public void SetCollapsed(bool collapsed)
    {
        if (Collapsed == collapsed) return;
        Collapsed = collapsed;
        CustomMinimumSize = new Vector2(collapsed ? Tokens.Size.SidebarRail : Tokens.Size.Sidebar, 0);
        _scroll.Visible = !collapsed;
        if (_railHost != null) _railHost.Visible = collapsed;
    }
}
