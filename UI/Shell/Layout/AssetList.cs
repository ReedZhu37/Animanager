using Godot;

namespace Animanager.UI.Shell;

/// <summary>Column definition for the asset list. Order is the contract; widths are logical units.</summary>
public sealed class AssetColumn
{
    public required string Key;
    public required string Title;
    public int MinWidth;
    public int Weight = 1;          // share of leftover width
    public bool RightAligned;
    public bool Mono;

    public string SortKey = "";     // empty = not sortable

    /// <summary>
    /// 0 = never dropped, higher = dropped sooner. Duration and frame rate matter more than the
    /// import timestamp when the columns are competing for the same 500 pixels.
    /// </summary>
    public int DropPriority;
}

/// <summary>
/// Virtualised asset list: one row per asset, sortable header, multi-select that shares its
/// state with the grid through <see cref="AssetSelection"/>.
///
/// Drawn on a single CanvasItem for the same reason the grid is: a library of thousands of
/// assets must not build thousands of Controls.
/// </summary>
public partial class AssetList : Control
{
    public const int RowHeight = 30;
    public const int HeaderHeight = 32;
    public const int PadX = 16;
    public const int PadY = 0;
    private const int ThumbWidth = 40;
    private const int ThumbHeight = 24;

    private readonly List<AssetCardModel> _rows = [];
    private readonly HashSet<string> _hiddenColumns = new(StringComparer.Ordinal);
    private readonly List<AssetColumn> _columns = [];
    private AssetSelection _selection = new();
    private ScrollContainer? _scroll;
    private int _hover = -1;
    private string _sortKey = "imported";
    private bool _sortDescending = true;
    private float[] _widths = [];

    public event Action<AssetGridEvent>? Activated;
    public event Action? SelectionChanged;
    public event Action<int, int>? VisibleRangeChanged;
    public event Action<string>? SortRequested;
    public event Action<string>? ContextMenuRequested;
    internal event Action<AssetCardModel>? OpenSourceRequested;

    public int Count => _rows.Count;
    public string SortKey => _sortKey;
    public bool SortDescending => _sortDescending;
    public IReadOnlyList<AssetColumn> Columns => _columns;
    public Rect2 HeaderRect => new(0, _scroll?.ScrollVertical ?? 0, Size.X, HeaderHeight);

    public AssetList()
    {
        _columns.AddRange(new[]
        {
            // Ordered by usefulness: name never drops, import time anchors the right edge, and
            // everything between them sheds by priority as the column narrows.
            new AssetColumn { Key = "name", Title = "名称", MinWidth = 148, Weight = 3, SortKey = "name" },
            new AssetColumn { Key = "kind", Title = "类型", MinWidth = 44, Weight = 0, DropPriority = 4 },
            new AssetColumn { Key = "duration", Title = "时长", MinWidth = 52, Weight = 0, RightAligned = true, Mono = true, SortKey = "duration", DropPriority = 1 },
            new AssetColumn { Key = "fps", Title = "帧率", MinWidth = 44, Weight = 0, RightAligned = true, Mono = true, SortKey = "fps", DropPriority = 3 },
            new AssetColumn { Key = "skeleton", Title = "骨架", MinWidth = 74, Weight = 1, DropPriority = 2 },
            new AssetColumn { Key = "size", Title = "大小", MinWidth = 58, Weight = 0, RightAligned = true, Mono = true, SortKey = "size", DropPriority = 2 },
            new AssetColumn { Key = "imported", Title = "导入", MinWidth = 76, Weight = 0, Mono = true, SortKey = "imported", DropPriority = 0 }
        });
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, RowHeight);
    }

    public void Bind(ScrollContainer scroll)
    {
        _scroll = scroll;
        scroll.GetVScrollBar().ValueChanged += _ => QueueRedraw();
    }

    public void BindSelection(AssetSelection selection)
    {
        if (ReferenceEquals(_selection, selection)) return;
        _selection = selection;
        _selection.Changed += QueueRedraw;
        QueueRedraw();
    }

    public AssetCardModel? Row(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    public void SetRows(IEnumerable<AssetCardModel> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _selection.SetView(_rows.Where(row => row.ClosedLibraryId == null).Select(row => row.Id));
        Reflow();
    }

    public void UpdateRow(int index, Action<AssetCardModel> mutate)
    {
        if (index < 0 || index >= _rows.Count) return;
        mutate(_rows[index]);
        QueueRedraw();
    }

    public int IndexOf(string id) => _rows.FindIndex(row => row.Id == id);

    public void SetSort(string key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Reflow();
    }

    public void Reflow()
    {
        float width = _scroll is { } scroll && scroll.Size.X > 1 ? scroll.Size.X : Math.Max(Size.X, 480);
        ComputeWidths(Math.Max(0, width - PadX * 2));
        float body = HeaderHeight + _rows.Count * RowHeight + 4;
        CustomMinimumSize = new Vector2(0, Math.Max(RowHeight, body));
        UpdateMinimumSize();
        QueueRedraw();
    }

    /// <summary>
    /// Every column starts at its minimum, then the remainder is shared by weight. Minimums are
    /// treated as hard floors: when the space is too tight the total simply overflows and the
    /// caller sees a narrower set of columns rather than zero-width ones.
    /// </summary>
    private void ComputeWidths(float available)
    {
        if (_widths.Length != _columns.Count) _widths = new float[_columns.Count];

        // Drop columns by priority until the remainder fits, so the visible part of the row is
        // never clipped. Priority 0 columns (name, import time) are never dropped.
        _hiddenColumns.Clear();
        var droppable = _columns
            .Select((column, index) => (column, index))
            .Where(pair => pair.column.DropPriority > 0)
            .OrderByDescending(pair => pair.column.DropPriority)
            .ToList();

        float Total(IEnumerable<AssetColumn> columns) => columns.Sum(column => (float)column.MinWidth);
        var visible = _columns.ToList();
        foreach (var (column, _) in droppable)
        {
            if (Total(visible) <= available) break;
            visible.Remove(column);
            _hiddenColumns.Add(column.Key);
        }

        float minTotal = Total(visible);
        float slack = Math.Max(0, available - minTotal);
        float weightTotal = visible.Sum(column => (float)column.Weight);
        for (int i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            if (_hiddenColumns.Contains(column.Key))
            {
                _widths[i] = 0;
                continue;
            }
            _widths[i] = column.MinWidth + (weightTotal > 0 ? slack * column.Weight / weightTotal : 0);
        }
    }

    private bool ColumnVisible(int column) => _widths.Length == _columns.Count && _widths[column] > .5f;

    private float TotalWidth => _widths.Sum();

    private float ColumnX(int column)
    {
        float x = PadX;
        for (int i = 0; i < column; i++) x += _widths[i];
        return x;
    }

    public override void _Draw()
    {
        float top = _scroll?.ScrollVertical ?? 0;
        float bottom = top + Math.Max(_scroll?.Size.Y ?? Size.Y, RowHeight);
        int first = Math.Max(0, (int)(top / RowHeight) - 1);
        int last = Math.Min(_rows.Count - 1, (int)(bottom / RowHeight) + 1);

        for (int i = first; i <= last; i++) DrawRow(i);

        // Draw above the rows, cancelling the parent's scroll for both paint and hit testing.
        DrawSetTransform(new Vector2(0, top));
        DrawHeader();
        DrawSetTransform(Vector2.Zero);

        VisibleRangeChanged?.Invoke(first, last);
    }

    private void DrawHeader()
    {
        DrawRect(new Rect2(0, 0, Size.X, HeaderHeight), Tokens.Bg);

        for (int c = 0; c < _columns.Count; c++)
        {
            if (!ColumnVisible(c)) continue;
            var column = _columns[c];
            float x = ColumnX(c);
            bool sorted = column.SortKey.Length > 0 && column.SortKey == _sortKey;
            var colour = sorted ? Tokens.Text : Tokens.Text3;
            float textY = (HeaderHeight - 1) * .5f + 4;

            if (column.RightAligned)
            {
                float textWidth = Typography.Width(Typography.Regular, column.Title, Type.Caption);
                DrawString(Typography.Regular, new Vector2(x + _widths[c] - textWidth - 8, textY), column.Title,
                    HorizontalAlignment.Left, -1, Type.Caption, colour);
            }
            else
            {
                DrawString(Typography.Regular, new Vector2(x + 6, textY), column.Title,
                    HorizontalAlignment.Left, -1, Type.Caption, colour);
            }

            if (!sorted) continue;
            // Sort caret sits just after the title, so the label itself never shifts.
            float markerX = column.RightAligned
                ? x + _widths[c] - Typography.Width(Typography.Regular, column.Title, Type.Caption) - 16
                : x + Typography.Width(Typography.Regular, column.Title, Type.Caption) + 12;
            Icons.Draw(this, _sortDescending ? Icons.ChevronDown : Icons.ChevronUp,
                new Rect2(markerX, textY - 3, 9, 9), Tokens.Accent, 1.5f);
        }

        DrawLine(new Vector2(PadX, HeaderHeight - 1), new Vector2(PadX + TotalWidth, HeaderHeight - 1), Tokens.Line, 1);
    }

    private void DrawRow(int index)
    {
        var row = _rows[index];
        bool closed = row.ClosedLibraryId != null;
        bool selected = _selection.Contains(row.Id);
        bool hovered = index == _hover;

        float y = HeaderHeight + index * RowHeight;
        var rect = new Rect2(PadX, y, TotalWidth, RowHeight);

        if (selected && hovered) DrawStyleBox(Surfaces.Fill(Tokens.CardHover, Tokens.Radius.Chip), rect);
        else if (selected) DrawStyleBox(Surfaces.Fill(Tokens.Active.Alpha(.7f), Tokens.Radius.Chip), rect);
        else if (hovered) DrawStyleBox(Surfaces.Fill(Tokens.Hover.Alpha(.55f), Tokens.Radius.Chip), rect);

        if (selected)
        {
            // A 2px accent bar on the left edge marks the row without colouring the whole row.
            DrawRect(new Rect2(PadX, y + 5, 2, RowHeight - 10), Tokens.Accent);
        }

        float mid = y + RowHeight * .5f;

        // Thumbnail
        var thumb = new Rect2(PadX + 6, mid - ThumbHeight * .5f, ThumbWidth, ThumbHeight);
        DrawStyleBox(Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Chip), thumb);
        if (row.Thumbnail != null)
        {
            var natural = row.Thumbnail.GetSize();
            float scale = Mathf.Max(thumb.Size.X / natural.X, thumb.Size.Y / natural.Y);
            var drawn = natural * scale;
            DrawTextureRect(row.Thumbnail, new Rect2(thumb.Position + (thumb.Size - drawn) * .5f, drawn), false);
        }
        else
        {
            Icons.Draw(this, closed ? Icons.Folder : Icons.Cube, new Rect2(thumb.GetCenter() - new Vector2(7, 7), new Vector2(14, 14)),
                Tokens.Text3.Alpha(.5f), 1.3f);
        }

        // Cell text. The name cell starts after the thumbnail and carries the favourite star.
        float nameX = PadX + 6 + ThumbWidth + 10;
        float nameWidth = _widths[0] - (ThumbWidth + 22);
        if (closed) nameWidth = Math.Max(20, SourceButtonRect(index).Position.X - nameX - 8);
        string libraryLabel = closed ? "库未打开 · " + row.LibraryName : row.LibraryName;
        if (row.MotionLabel.Length > 0) libraryLabel = row.MotionLabel;
        float libraryWidth = libraryLabel.Length == 0 ? 0 : Math.Min(nameWidth * .4f,
            Typography.Width(Typography.Regular, libraryLabel, Type.Micro) + 8);
        var nameColour = closed ? Tokens.Text3 : selected ? Tokens.Text : Tokens.Text.Alpha(.94f);
        DrawString(Typography.Semi, new Vector2(nameX, mid + 4),
            Typography.Fit(Typography.Semi, row.Name, Type.Row, Math.Max(20, nameWidth - libraryWidth)),
            HorizontalAlignment.Left, -1, Type.Row, nameColour);
        if (libraryWidth > 0)
            DrawString(Typography.Regular, new Vector2(nameX + nameWidth - libraryWidth + 4, mid + 4),
                Typography.Fit(Typography.Regular, libraryLabel, Type.Micro, libraryWidth - 8),
                HorizontalAlignment.Left, -1, Type.Micro, Tokens.Text3);

        if (closed)
        {
            AssetBrowser.DrawSourceButton(this, SourceButtonRect(index), row, hovered);
            return;
        }

        if (row.Favorite)
            Icons.Draw(this, Icons.Star, new Rect2(PadX + _widths[0] - 14, mid - 5, 11, 11), Tokens.Warn, 1.1f);

        for (int c = 1; c < _columns.Count; c++)
        {
            if (!ColumnVisible(c)) continue;
            var column = _columns[c];
            string text = column.Key switch
            {
                "kind" => row.IsAnimation ? "动画" : "模型",
                "duration" => Field(row, "duration"),
                "fps" => Field(row, "fps"),
                "skeleton" => Field(row, "skeleton"),
                "size" => Field(row, "size"),
                "imported" => Field(row, "imported"),
                _ => ""
            };
            if (text.Length == 0) continue;

            var font = column.Mono ? Typography.Mono : Typography.Regular;
            int size = column.Mono ? Type.Micro : Type.Caption;
            var colour = column.Key is "kind" ? Tokens.Text2 : Tokens.Text3;
            float x = ColumnX(c);
            float cellWidth = _widths[c] - 16;

            if (column.RightAligned)
            {
                float textWidth = Typography.Width(font, text, size);
                DrawString(font, new Vector2(x + _widths[c] - textWidth - 8, mid + 4),
                    Typography.Fit(font, text, size, cellWidth), HorizontalAlignment.Left, -1, size, colour);
            }
            else
            {
                DrawString(font, new Vector2(x + 6, mid + 4),
                    Typography.Fit(font, text, size, cellWidth), HorizontalAlignment.Left, -1, size, colour);
            }
        }
    }

    internal Rect2 SourceButtonRect(int index) => new(
        Math.Min(PadX + TotalWidth, Size.X - PadX) - 96, HeaderHeight + index * RowHeight + 3, 90, 24);

    /// <summary>
    /// Structured cell values. Kept as named fields on the model rather than parsed out of a
    /// display string, so a column can be sorted on the underlying number.
    /// </summary>
    private static string Field(AssetCardModel row, string key) => key switch
    {
        "duration" => row.DurationText,
        "fps" => row.FpsText,
        "skeleton" => row.SkeletonText,
        "size" => row.SizeText,
        "imported" => row.ImportedText,
        _ => ""
    };

    private int RowAt(Vector2 point)
    {
        if (HeaderRect.HasPoint(point) || point.Y < HeaderHeight) return -1;
        int index = (int)((point.Y - HeaderHeight) / RowHeight);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    private int ColumnAt(Vector2 point)
    {
        if (point.X < PadX) return -1;
        float x = PadX;
        for (int i = 0; i < _columns.Count; i++)
        {
            if (!ColumnVisible(i)) continue;
            if (point.X >= x && point.X < x + _widths[i]) return i;
            x += _widths[i];
        }
        return -1;
    }

    public override void _GuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseMotion motion)
        {
            int index = RowAt(motion.Position);
            if (index != _hover)
            {
                _hover = index;
                QueueRedraw();
            }
            TooltipText = index >= 0 ? _rows[index].Tooltip : "";

            // Cursor feedback over sortable headers is the cheapest way to advertise sorting.
            int column = HeaderRect.HasPoint(motion.Position) ? ColumnAt(motion.Position) : -1;
            MouseDefaultCursorShape = (column >= 0 && _columns[column].SortKey.Length > 0) ||
                (Row(index)?.ClosedLibraryId != null && SourceButtonRect(index).HasPoint(motion.Position))
                ? CursorShape.PointingHand
                : CursorShape.Arrow;
        }

        if (ev is not InputEventMouseButton button) return;
        if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown) return;
        if (!button.Pressed)
        {
            if (button.ButtonIndex == MouseButton.Left) _selection.ReleaseRow(Row(RowAt(button.Position))?.Id);
            return;
        }
        _selection.CancelPendingClick();

        // Header click sorts; clicking the active column flips direction.
        if (button.ButtonIndex == MouseButton.Left && HeaderRect.HasPoint(button.Position))
        {
            int column = ColumnAt(button.Position);
            if (column < 0 || _sortKey == "motion") return;
            var definition = _columns[column];
            if (definition.SortKey.Length == 0) return;
            bool descending = definition.SortKey == _sortKey ? !_sortDescending : false;
            SetSort(definition.SortKey, descending);
            SortRequested?.Invoke(definition.SortKey);
            return;
        }

        int hit = RowAt(button.Position);
        if (hit < 0)
        {
            if (button.ButtonIndex == MouseButton.Left && !button.CtrlPressed && !button.ShiftPressed)
                _selection.Clear();
            return;
        }

        var row = _rows[hit];
        string id = row.Id;
        if (row.ClosedLibraryId != null)
        {
            if (button.ButtonIndex == MouseButton.Right) ContextMenuRequested?.Invoke(id);
            else if (button.ButtonIndex == MouseButton.Left && SourceButtonRect(hit).HasPoint(button.Position))
                OpenSourceRequested?.Invoke(row);
            AcceptEvent();
            return;
        }
        if (button.ButtonIndex == MouseButton.Right)
        {
            if (!_selection.Contains(id)) _selection.Select(id);
            ContextMenuRequested?.Invoke(id);
            return;
        }
        if (button.ButtonIndex != MouseButton.Left) return;

        GrabFocus();
        _selection.PressRow(id, button);

        SelectionChanged?.Invoke();
        Activated?.Invoke(new AssetGridEvent(hit, button.DoubleClick && !button.CtrlPressed && !button.ShiftPressed));
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        return AssetBrowser.BeginAssetDrag(this, Row(RowAt(atPosition)), _selection);
    }

    public void ScrollToIndex(int index)
    {
        if (_scroll == null || index < 0) return;
        _scroll.ScrollVertical = Math.Max(0, index * RowHeight);
    }
}
