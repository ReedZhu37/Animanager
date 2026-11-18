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
    public event Action? CreateCollection;
    public event Action<string>? TagSelected;
    private readonly VBoxContainer _collections = new();
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
    private readonly HFlowContainer _tags;
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
        _stack.AddChild(new NavSection("库"));
        foreach (var (key, icon, title) in Categories)
        {
            AddCategory(key, icon, title);
            AddRailButton(key, icon, title);
        }

        _stack.AddChild(Hairline.Spacer(12));
        _stack.AddChild(new NavSection("集合", () => CreateCollection?.Invoke()));
        _stack.AddChild(_collections);
        _rail.AddChild(_collectionRail);
        _collectionRail.GetPopup().IdPressed += id => CollectionSelected?.Invoke(_collectionRail.GetPopup().GetItemMetadata((int)id).AsString());

        _stack.AddChild(Hairline.Spacer(12));
        _stack.AddChild(new NavSection("标签"));
        _tags = new HFlowContainer();
        _tags.AddThemeConstantOverride("h_separation", 6);
        _tags.AddThemeConstantOverride("v_separation", 6);
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
        row.Pressed += () =>
        {
            SetActive(key);
            CategorySelected?.Invoke(key);
        };
        _rows[key] = row;
        _stack.AddChild(row);
    }

    private void AddCollection(string key, string title)
    {
        var row = new NavRow(Icons.Folder, title);
        row.Pressed += () => CollectionSelected?.Invoke(key);
        _rows[key] = row;
        _collections.AddChild(row);
    }

    public void SetCollections(IEnumerable<(string Id, string Name)> entries)
    {
        foreach (var child in _collections.GetChildren()) { _collections.RemoveChild(child); child.QueueFree(); }
        foreach (var key in _rows.Keys.Where(k => !Categories.Any(c => c.Key == k)).ToArray()) _rows.Remove(key);
        var menu = _collectionRail.GetPopup(); menu.Clear();
        foreach (var entry in entries) { AddCollection(entry.Id, entry.Name); menu.AddItem(entry.Name); menu.SetItemMetadata(menu.ItemCount - 1, entry.Id); }
        _collectionRail.Disabled = menu.ItemCount == 0; SetActive(ActiveCategory);
    }

    public void SetTags(IEnumerable<(string Name, int Count)> tags)
    {
        foreach (var child in _tags.GetChildren())
        {
            _tags.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var (name, count) in tags)
        {
            var chip = new Button
            {
                Text = $"{name} {count}", CustomMinimumSize = new Vector2(0, 22), ThemeTypeVariation = "Chip"
            };
            chip.AddThemeFontSizeOverride("font_size", Type.Caption);
            chip.Pressed += () => TagSelected?.Invoke(name);
            _tags.AddChild(chip);
        }
    }

    public void SetCount(string key, int count)
    {
        if (_rows.TryGetValue(key, out var row)) row.SetCount(count);
        if (_railButtons.TryGetValue(key, out var button)) button.TooltipText = Categories.First(c => c.Key == key).Title + " · " + count.ToString("N0");
    }

    public void SetActive(string key)
    {
        foreach (var pair in _rows) pair.Value.SetActive(pair.Key == key);
        foreach (var pair in _railButtons) pair.Value.SetActive(pair.Key == key);
        ActiveCategory = key;
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
