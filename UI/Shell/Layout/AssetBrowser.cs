using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Hosts the asset views and owns the one thing they must share: the selection. Switching
/// between grid and list preserves the chosen ids, the range anchor, the hovered item and the
/// scroll offset, because losing those on a view toggle is the kind of papercut that makes a
/// tool feel amateur.
/// </summary>
public partial class AssetBrowser : VBoxContainer
{
    private const string DragPrefix = "animanager-asset:";

    public enum ViewMode { Grid, List }

    public event Action<AssetGridEvent>? Activated;
    public event Action? SelectionChanged;
    public event Action<int, int>? VisibleRangeChanged;
    public event Action<Animanager.Core.Browse.AssetSort>? SortRequested;
    public event Action<string>? ContextMenuRequested;
    public event Action<ViewMode>? ViewModeChanged;
    public event Action? DeleteRequested;
    internal event Action<AssetCardModel>? OpenSourceRequested;

    public AssetSelection Selection { get; } = new();
    public ViewMode Mode { get; private set; } = ViewMode.Grid;

    private readonly ScrollContainer _scroll;
    private readonly AssetGrid _grid;
    private readonly AssetList _list;
    private List<AssetCardModel> _rows = [];
    private int _scrollMemoryGrid, _scrollMemoryList;
    private int _viewGeneration;
    private bool _restoringScroll;
    private string? _keyboardId;

    public AssetBrowser()
    {
        AddThemeConstantOverride("separation", 0);
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        AddChild(_scroll);

        _grid = new AssetGrid();
        _list = new AssetList { Visible = false };
        _grid.Bind(_scroll);
        _list.Bind(_scroll);
        _grid.BindSelection(Selection);
        _list.BindSelection(Selection);
        Selection.Changed += () => _keyboardId = null;
        _scroll.AddChild(_grid);
        _scroll.AddChild(_list);

        foreach (var view in new Control[] { _grid, _list })
        {
            if (view is AssetGrid grid)
            {
                grid.Activated += OnViewActivated;
                grid.SelectionChanged += () => SelectionChanged?.Invoke();
                grid.VisibleRangeChanged += (first, last) => VisibleRangeChanged?.Invoke(first, last);
                grid.ContextMenuRequested += id => ContextMenuRequested?.Invoke(id);
                grid.OpenSourceRequested += card => OpenSourceRequested?.Invoke(card);
            }
            else if (view is AssetList list)
            {
                list.Activated += OnViewActivated;
                list.SelectionChanged += () => SelectionChanged?.Invoke();
                list.VisibleRangeChanged += (first, last) => VisibleRangeChanged?.Invoke(first, last);
                list.SortRequested += key => SortRequested?.Invoke(Animanager.Core.Browse.AssetSort.FromKey(key, list.SortDescending));
                list.ContextMenuRequested += id => ContextMenuRequested?.Invoke(id);
                list.OpenSourceRequested += card => OpenSourceRequested?.Invoke(card);
            }
        }

        _scroll.Resized += () =>
        {
            if (Mode == ViewMode.Grid) _grid.Reflow();
            else _list.Reflow();
        };
        _grid.GuiInput += HandleKeys;
        _list.GuiInput += HandleKeys;
    }

    public int Count => _rows.Count;
    public IReadOnlyList<AssetCardModel> Rows => _rows;
    public ScrollContainer Scroll => _scroll;
    public Rect2 RowRect(int index) => Mode == ViewMode.Grid ? _grid.CardRect(index) : new(AssetList.PadX, AssetList.HeaderHeight + index * AssetList.RowHeight, Math.Max(1, _list.Size.X - 2 * AssetList.PadX), AssetList.RowHeight);
    public IEnumerable<int> VisibleIndices() => Enumerable.Range(0, Count).Where(i =>
        RowRect(i).End.Y > _scroll.ScrollVertical + (Mode == ViewMode.List ? AssetList.HeaderHeight : 0) && RowRect(i).Position.Y < _scroll.ScrollVertical + _scroll.Size.Y);
    public void SetSort(Animanager.Core.Browse.AssetSort sort) => _list.SetSort(sort.Key, sort.Descending);

    private void OnViewActivated(AssetGridEvent activation)
    {
        _keyboardId = Row(activation.Index)?.Id;
        Activated?.Invoke(activation);
    }

    private void HandleKeys(InputEvent ev)
    {
        if (ev is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Delete) { DeleteRequested?.Invoke(); ActiveView.AcceptEvent(); return; }
        if ((key.CtrlPressed || key.MetaPressed) && key.Keycode == Key.A)
        { Selection.AddRange(_rows.Where(row => row.ClosedLibraryId == null).Select(row => row.Id)); ActiveView.AcceptEvent(); return; }
        int firstClosed = _rows.FindIndex(row => row.ClosedLibraryId != null);
        int available = firstClosed < 0 ? Count : firstClosed;
        if (available == 0) return;

        int current = IndexOf(_keyboardId ?? "");
        if (current < 0) current = IndexOf(Selection.Anchor ?? "");
        if (key.Keycode == Key.Space)
        {
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && !key.MetaPressed)
            {
                ActiveView.AcceptEvent();
                if (!key.Echo) Selection.Toggle(_rows[Math.Max(0, current)].Id);
            }
            return;
        }

        if (key.IsActionPressed("ui_accept", allowEcho: true, exactMatch: true))
        {
            ActiveView.AcceptEvent();
            if (!key.Echo && current >= 0 && Selection.Contains(_rows[current].Id))
                Activated?.Invoke(new AssetGridEvent(current, OpenRequested: true));
            return;
        }

        int columns = Mode == ViewMode.Grid ? _grid.Columns : 1;
        int next = key switch
        {
            { Keycode: Key.Home } => 0,
            { Keycode: Key.End } => available - 1,
            _ when key.IsActionPressed("ui_left", allowEcho: true) => Math.Max(0, current - 1),
            _ when key.IsActionPressed("ui_right", allowEcho: true) => Math.Max(0, current + 1),
            _ when key.IsActionPressed("ui_up", allowEcho: true) => Math.Max(0, current - columns),
            _ when key.IsActionPressed("ui_down", allowEcho: true) => current < 0 ? 0 : current + columns,
            _ => -1
        };
        if (next < 0) return;
        next = Math.Min(next, available - 1);
        ActiveView.AcceptEvent();
        if (key.ShiftPressed) Selection.SelectRange(_rows[next].Id);
        else Selection.Select(_rows[next].Id);
        _keyboardId = _rows[next].Id;
        ScrollToIndex(next);
    }

    internal static Variant BeginAssetDrag(Control view, AssetCardModel? card, AssetSelection selection)
    {
        if (card == null || card.Trashed || card.ClosedLibraryId != null) return default;
        selection.CancelPendingClick();
        string[] keys = selection.Contains(card.Id) ? [card.Id, .. selection.Where(id => id != card.Id)] : [card.Id];
        var label = new Label { Text = keys.Length == 1 ? card.Name : $"{keys.Length} 个资产", MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeStyleboxOverride("normal", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Control, 12, 8, Tokens.Accent));
        view.SetDragPreview(label);
        return Variant.From<string[]>([DragPrefix, .. keys]);
    }

    internal static void DrawSourceButton(Control view, Rect2 rect, AssetCardModel card, bool hovered)
    {
        string label = SourceActionLabel(card);
        view.DrawStyleBox(Surfaces.Fill(hovered ? Tokens.Hover : Tokens.Raised, Tokens.Radius.Control, border: Tokens.LineStrong), rect);
        float width = Typography.Width(Typography.Semi, label, Type.Caption);
        view.DrawString(Typography.Semi, rect.GetCenter() + new Vector2(-width / 2, 4), label,
            HorizontalAlignment.Left, -1, Type.Caption, Tokens.Accent);
    }

    internal static string SourceActionLabel(AssetCardModel card) => card.LocateClosedLibrary ? "定位来源库" : "打开来源库";

    internal static string[] DraggedAssets(Variant data)
    {
        if (data.VariantType != Variant.Type.PackedStringArray) return [];
        var payload = data.AsStringArray();
        return payload.Length > 1 && payload[0] == DragPrefix ? payload[1..] : [];
    }

    public AssetGrid Grid => _grid;
    public AssetList List => _list;

    /// <summary>Rows visible in the current view; the shell uses this to prefetch thumbnails.</summary>
    public Control ActiveView => Mode == ViewMode.Grid ? _grid : _list;

    public void SetRows(IEnumerable<AssetCardModel> rows)
    {
        _rows = rows.ToList();
        _grid.SetCards(_rows);
        _list.SetRows(_rows);
        // Both views receive the same model instances, so a thumbnail loaded once shows in both.
        _grid.Reflow();
        _list.Reflow();
    }

    public AssetCardModel? Row(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    public void UpdateRow(int index, Action<AssetCardModel> mutate)
    {
        if (index < 0 || index >= _rows.Count) return;
        mutate(_rows[index]);
        _grid.QueueRedraw();
        _list.QueueRedraw();
    }

    public int IndexOf(string id) => _rows.FindIndex(row => row.Id == id);

    public void SetMode(ViewMode mode)
    {
        if (Mode == mode) return;
        bool transferFocus = ActiveView.IsInsideTree() && ActiveView.HasFocus();

        // Remember where each view was, so toggling back returns to the same place.
        if (!_restoringScroll)
        {
            if (Mode == ViewMode.Grid) _scrollMemoryGrid = _scroll.ScrollVertical;
            else _scrollMemoryList = _scroll.ScrollVertical;
        }

        Mode = mode;
        _grid.Visible = mode == ViewMode.Grid;
        _list.Visible = mode == ViewMode.List;
        if (transferFocus) ActiveView.GrabFocus();

        if (mode == ViewMode.Grid) _grid.Reflow();
        else _list.Reflow();

        int generation = ++_viewGeneration;
        int offset = mode == ViewMode.Grid ? _scrollMemoryGrid : _scrollMemoryList;
        _restoringScroll = true;
        // Scroll ranges update after container layout; setting the offset earlier can clamp it.
        Callable.From(() =>
        {
            if (generation != _viewGeneration) return;
            _scroll.ScrollVertical = offset;
            _restoringScroll = false;
        }).CallDeferred();
        ViewModeChanged?.Invoke(mode);
    }

    public void ScrollToIndex(int index)
    {
        if (Mode == ViewMode.Grid) _grid.ScrollToIndex(index);
        else _list.ScrollToIndex(index);
    }

    public void SelectIndex(int index)
    {
        var row = Row(index);
        if (row is { ClosedLibraryId: null }) Selection.Select(row.Id);
    }

    /// <summary>Grid column count at the current width; used by the responsive rule.</summary>
    public static float WidthForColumns(int columns) => AssetGrid.WidthForColumns(columns);

    public static float TwoColumns => Tokens.CardMetrics.TwoColumns;

    public static float ThreeColumns => AssetGrid.WidthForColumns(3);

    /// <summary>
    /// The narrowest width at which the current view mode is still readable. The list only needs
    /// a name column, so it survives far narrower than the grid does.
    /// </summary>
    public float MinimumWidthForMode => Mode == ViewMode.Grid ? TwoColumns : 420;
}
