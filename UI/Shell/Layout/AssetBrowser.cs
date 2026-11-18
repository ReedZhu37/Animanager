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
    public enum ViewMode { Grid, List }

    public event Action<AssetGridEvent>? Activated;
    public event Action? SelectionChanged;
    public event Action<int, int>? VisibleRangeChanged;
    public event Action<Animanager.Core.Browse.AssetSort>? SortRequested;
    public event Action<string>? ContextMenuRequested;
    public event Action<ViewMode>? ViewModeChanged;
    public event Action? DeleteRequested;

    public AssetSelection Selection { get; } = new();
    public ViewMode Mode { get; private set; } = ViewMode.Grid;

    private readonly ScrollContainer _scroll;
    private readonly AssetGrid _grid;
    private readonly AssetList _list;
    private List<AssetCardModel> _rows = [];
    private int _scrollMemoryGrid, _scrollMemoryList;
    private int _viewGeneration;
    private bool _restoringScroll;

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
        _scroll.AddChild(_grid);
        _scroll.AddChild(_list);

        foreach (var view in new Control[] { _grid, _list })
        {
            if (view is AssetGrid grid)
            {
                grid.Activated += e => { _keyboardIndex = e.Index; Activated?.Invoke(e); };
                grid.SelectionChanged += () => SelectionChanged?.Invoke();
                grid.VisibleRangeChanged += (first, last) => VisibleRangeChanged?.Invoke(first, last);
                grid.ContextMenuRequested += id => ContextMenuRequested?.Invoke(id);
            }
            else if (view is AssetList list)
            {
                list.Activated += e => { _keyboardIndex = e.Index; Activated?.Invoke(e); };
                list.SelectionChanged += () => SelectionChanged?.Invoke();
                list.VisibleRangeChanged += (first, last) => VisibleRangeChanged?.Invoke(first, last);
                list.SortRequested += key => SortRequested?.Invoke(Animanager.Core.Browse.AssetSort.FromKey(key, list.SortDescending));
                list.ContextMenuRequested += id => ContextMenuRequested?.Invoke(id);
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
    private int _keyboardIndex;
    private void HandleKeys(InputEvent ev)
    {
        if (ev is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Delete) { DeleteRequested?.Invoke(); ActiveView.AcceptEvent(); return; }
        if ((key.CtrlPressed || key.MetaPressed) && key.Keycode == Key.A)
        { Selection.AddRange(_rows.Select(r => r.Id)); ActiveView.AcceptEvent(); return; }
        if (Count == 0) return;
        int current = Math.Max(0, IndexOf(Selection.Anchor ?? ""));
        int columns = Mode == ViewMode.Grid ? _grid.Columns : 1;
        int next = key.Keycode switch
        {
            Key.Left => current - 1, Key.Right => current + 1, Key.Up => current - columns,
            Key.Down => current + columns, Key.Home => 0, Key.End => Count - 1,
            Key.Enter or Key.KpEnter or Key.Space => current, _ => -999
        };
        if (next == -999) return;
        if (key.ShiftPressed && key.Keycode is Key.Up or Key.Down or Key.Left or Key.Right)
            next = _keyboardIndex + (key.Keycode == Key.Up ? -columns : key.Keycode == Key.Down ? columns : key.Keycode == Key.Left ? -1 : 1);
        next = Math.Clamp(next, 0, Count - 1); _keyboardIndex = next;
        if (key.ShiftPressed) Selection.SelectRange(_rows[next].Id);
        else if (key.Keycode == Key.Space && key.CtrlPressed) Selection.Toggle(_rows[next].Id);
        else Selection.Select(_rows[next].Id);
        ScrollToIndex(next); Activated?.Invoke(new(next, _rows[next], key.ShiftPressed || key.CtrlPressed)); ActiveView.AcceptEvent();
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

        // Remember where each view was, so toggling back returns to the same place.
        if (!_restoringScroll)
        {
            if (Mode == ViewMode.Grid) _scrollMemoryGrid = _scroll.ScrollVertical;
            else _scrollMemoryList = _scroll.ScrollVertical;
        }

        Mode = mode;
        _grid.Visible = mode == ViewMode.Grid;
        _list.Visible = mode == ViewMode.List;

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
        if (row != null) Selection.Select(row.Id);
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
