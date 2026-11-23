using Animanager.Core.Storage;
using Godot;

namespace Animanager.UI.Shell;

internal enum CollectionMenuAction { CreateChild = 10, Rename, Close, Delete, Export, Copy }

public partial class SideBar
{
    internal event Action<string, CollectionMenuAction>? CollectionActionRequested;
    internal Func<string, string?, CollectionPlacement, bool>? CanMoveCollection { get; set; }
    internal event Action<string, string?, CollectionPlacement>? CollectionMoveRequested;
    internal Func<string, string[], bool>? CanDropAssetsOnCollection { get; set; }
    internal event Action<string, string[]>? AssetsDroppedOnCollection;
    internal Func<string[], bool>? CanDropAssetsOnFavorites { get; set; }
    internal event Action<string[]>? AssetsDroppedOnFavorites;

    private readonly List<(string Id, string? ParentId, Control Line, IconButton? Disclosure)> _collectionLines = [];
    private readonly HashSet<string> _foldedCollections = [];
    private const string CollectionDragPrefix = "animanager-collection";
    private readonly StyleBoxFlat _dropOutline = Surfaces.Outline(Tokens.Accent);
    private readonly StyleBoxFlat _dropBefore = Surfaces.Bar(Colors.Transparent, true, false, Tokens.Accent, 2);
    private readonly StyleBoxFlat _dropAfter = Surfaces.Bar(Colors.Transparent, false, true, Tokens.Accent, 2);
    private Panel? _highlightedDrop;

    internal void SetCollections(IEnumerable<(string Id, string Name, string? ParentId)> entries)
    {
        HighlightDrop(null);
        foreach (var entry in _collectionLines)
        {
            _rows.Remove(entry.Id);
            _collections.RemoveChild(entry.Line);
            entry.Line.QueueFree();
        }
        _collectionLines.Clear();

        var children = entries.ToLookup(entry => entry.ParentId);
        var pending = new Stack<((string Id, string Name, string? ParentId) Entry, int Depth)>();
        foreach (var root in children[null].Reverse()) pending.Push((root, 0));
        var menu = _collectionRail.GetPopup();
        menu.Clear();
        while (pending.TryPop(out var branch))
        {
            var (entry, depth) = branch;
            int indent = Math.Min(depth, 5);
            AddCollectionRow(entry.Id, entry.Name, entry.ParentId, indent, children.Contains(entry.Id));
            menu.AddCheckItem(entry.Name);
            menu.SetItemMetadata(menu.ItemCount - 1, entry.Id);
            menu.SetItemIndent(menu.ItemCount - 1, indent);
            foreach (var child in children[entry.Id].Reverse()) pending.Push((child, depth + 1));
        }

        _foldedCollections.IntersectWith(_collectionLines.Select(entry => entry.Id));
        UpdateCollectionVisibility();
        _collectionRail.Disabled = menu.ItemCount == 0;
        SetActive(ActiveCategory);
    }

    internal void ExpandCollection(string id)
    {
        if (_foldedCollections.Remove(id)) UpdateCollectionVisibility();
    }

    private void RevealCollection(string id)
    {
        var parents = _collectionLines.ToDictionary(line => line.Id, line => line.ParentId);
        while (parents.TryGetValue(id, out var parent) && parent != null)
        {
            _foldedCollections.Remove(parent);
            id = parent;
        }
        UpdateCollectionVisibility();
    }

    private void UpdateCollectionVisibility()
    {
        var hiddenChildrenOf = new HashSet<string>();
        foreach (var (id, parent, line, disclosure) in _collectionLines)
        {
            line.Visible = parent == null || !hiddenChildrenOf.Contains(parent);
            bool folded = _foldedCollections.Contains(id);
            if (!line.Visible || folded) hiddenChildrenOf.Add(id);
            disclosure?.SetIcon(folded ? Icons.ChevronRight : Icons.ChevronDown);
            if (disclosure != null) disclosure.TooltipText = folded ? "展开子集合" : "折叠子集合";
        }
    }

    private void AddCollectionRow(string id, string name, string? parentId, int indent, bool hasChildren)
    {
        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 0);
        line.AddChild(Hairline.Gap(6 + indent * 12));
        IconButton? disclosure = null;
        if (hasChildren)
        {
            disclosure = new IconButton(Icons.ChevronDown, 11) { Name = "CollectionDisclosure" + id };
            disclosure.Pressed += () =>
            {
                if (!_foldedCollections.Add(id)) _foldedCollections.Remove(id);
                UpdateCollectionVisibility();
            };
            line.AddChild(disclosure);
        }
        else line.AddChild(Hairline.Gap(21));

        var row = new NavRow(Icons.Folder, name)
        {
            Name = "CollectionRow" + id, FocusMode = FocusModeEnum.All, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = $"{name}\n拖动此行到其他集合的上沿或下沿可调整顺序\n拖到行中间可移入集合\n拖入资产即可加入集合"
        };
        row.Pressed += () => CollectionSelected?.Invoke(id);
        var actions = new MenuButton { Name = "CollectionActions" + id, Text = "⋯", TooltipText = "管理集合「" + name + "」",
            Flat = true, FocusMode = FocusModeEnum.All, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseDefaultCursorShape = CursorShape.PointingHand };
        var menu = actions.GetPopup();
        menu.AddItem("新建子集合…", (int)CollectionMenuAction.CreateChild);
        menu.AddItem("编辑集合…", (int)CollectionMenuAction.Rename);
        menu.AddItem("复制集合", (int)CollectionMenuAction.Copy);
        menu.AddItem("导出集合…", (int)CollectionMenuAction.Export);
        menu.AddItem("关闭集合", (int)CollectionMenuAction.Close);
        menu.AddSeparator();
        menu.AddItem("删除集合…", (int)CollectionMenuAction.Delete);
        actions.AboutToPopup += () => menu.SetItemDisabled(menu.GetItemIndex((int)CollectionMenuAction.Close), ActiveCategory != id);
        menu.IdPressed += action => CollectionActionRequested?.Invoke(id, (CollectionMenuAction)action);
        row.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }) return;
            row.AcceptEvent();
            actions.ShowPopup();
        };
        CollectionPlacement Placement(Vector2 position) => position.Y < row.Size.Y * .25f ? CollectionPlacement.Before
            : position.Y >= row.Size.Y * .75f ? CollectionPlacement.After : CollectionPlacement.Inside;

        EnableNavigationDrop(row, Callable.From<Vector2, Variant>(_ =>
        {
            var label = new Label { Text = name, MouseFilter = MouseFilterEnum.Ignore };
            label.AddThemeStyleboxOverride("normal", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Control, 12, 8, Tokens.Accent));
            row.SetDragPreview(label);
            return Variant.From<string[]>([CollectionDragPrefix, id]);
        }), (position, data) =>
        {
            if (DraggedCollection(data) is { } sourceId)
            {
                var placement = Placement(position);
                if (CanMoveCollection?.Invoke(sourceId, id, placement) != true) return null;
                return placement switch
                {
                    CollectionPlacement.Before => _dropBefore,
                    CollectionPlacement.After => _dropAfter,
                    _ => _dropOutline
                };
            }
            var keys = AssetBrowser.DraggedAssets(data);
            return keys.Length > 0 && CanDropAssetsOnCollection?.Invoke(id, keys) == true ? _dropOutline : null;
        }, (position, data) =>
        {
            if (DraggedCollection(data) is { } sourceId) DropCollection(sourceId, id, Placement(position));
            else
            {
                var keys = AssetBrowser.DraggedAssets(data);
                if (keys.Length > 0) AssetsDroppedOnCollection?.Invoke(id, keys);
            }
        });
        line.AddChild(row);
        line.AddChild(actions);
        _rows.Add(id, row);
        _collectionLines.Add((id, parentId, line, disclosure));
        _collections.AddChild(line);
    }

    private static string? DraggedCollection(Variant data)
    {
        if (data.VariantType != Variant.Type.PackedStringArray) return null;
        var payload = data.AsStringArray();
        return payload.Length == 2 && payload[0] == CollectionDragPrefix ? payload[1] : null;
    }

    private void DropCollection(string id, string? targetId, CollectionPlacement placement)
    {
        // Moving a collection rebuilds the rows after Godot finishes dispatching the drop.
        Callable.From(() => CollectionMoveRequested?.Invoke(id, targetId, placement)).CallDeferred();
    }

    private void EnableCollectionRootDrop(Control target) => EnableNavigationDrop(target, default, (_, data) =>
        DraggedCollection(data) is { } id && CanMoveCollection?.Invoke(id, null, CollectionPlacement.Inside) == true
            ? _dropOutline : null, (_, data) =>
        {
            if (DraggedCollection(data) is { } id) DropCollection(id, null, CollectionPlacement.Inside);
        });

    private void EnableFavoritesDrop(Control target) => EnableNavigationDrop(target, default, (_, data) =>
    {
        var keys = AssetBrowser.DraggedAssets(data);
        return keys.Length > 0 && CanDropAssetsOnFavorites?.Invoke(keys) == true ? _dropOutline : null;
    }, (_, data) =>
    {
        var keys = AssetBrowser.DraggedAssets(data);
        if (keys.Length > 0) AssetsDroppedOnFavorites?.Invoke(keys);
    });

    private void EnableNavigationDrop(Control target, Callable getData, Func<Vector2, Variant, StyleBox?> dropStyle,
        Action<Vector2, Variant> drop)
    {
        var highlight = new Panel { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        target.AddChild(highlight);
        highlight.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        target.SetDragForwarding(getData,
            Callable.From<Vector2, Variant, bool>((position, data) =>
            {
                var style = dropStyle(position, data);
                if (style == null)
                {
                    if (_highlightedDrop == highlight) HighlightDrop(null);
                    return false;
                }
                if (highlight.GetThemeStylebox("panel") != style) highlight.AddThemeStyleboxOverride("panel", style);
                HighlightDrop(highlight);
                return true;
            }),
            Callable.From(drop));
        target.MouseExited += () => { if (_highlightedDrop == highlight) HighlightDrop(null); };
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd) HighlightDrop(null);
    }

    private void HighlightDrop(Panel? highlight)
    {
        if (_highlightedDrop == highlight) return;
        _highlightedDrop?.Hide();
        _highlightedDrop = highlight;
        highlight?.Show();
    }
}
