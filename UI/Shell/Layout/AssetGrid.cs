using Godot;

namespace Animanager.UI.Shell;

/// <summary>Everything a grid cell needs to paint itself. Produced by the data layer, not the grid.</summary>
public sealed class AssetCardModel
{
    public string Id = "";
    public string Name = "";
    public string MetaLine = "";      // "FBX · 2.5 MB"
    public string DetailLine = "";    // "2.0s · 60fps · UE5_Manny"
    public bool IsAnimation;
    public bool Favorite;
    public Texture2D? Thumbnail;
    public bool ThumbnailPending;
    public string Tooltip = "";

    // Structured fields. The inspector and the list view render these directly; sorting reads
    // the numbers, never a display string.
    public string Tags = "";
    public string VirtualPath = "";
    public string ImportedAt = "";
    public long Size;

    public double? DurationSeconds;
    public double? SourceFps;
    public int SkeletonCount;
    public string SkeletonName = "";

    // Pre-formatted cell text. Empty when unknown, so cells can be left blank instead of "0".
    public string DurationText = "";
    public string FpsText = "";
    public string SkeletonText = "";
    public string SizeText = "";
    public string ImportedText = "";
}

/// <summary>
/// Virtualised asset grid. One CanvasItem paints only the rows inside the viewport, so a
/// library of thousands of assets scrolls without building thousands of Controls.
/// </summary>
public partial class AssetGrid : Control
{
    private static readonly int CardWidth = Tokens.CardMetrics.Width;
    private static readonly int CardHeight = Tokens.CardMetrics.Height;
    private static readonly int GapX = Tokens.CardMetrics.GapX;
    private static readonly int GapY = Tokens.CardMetrics.GapY;
    private static readonly int PadX = Tokens.CardMetrics.PadX;
    private static readonly int PadY = Tokens.CardMetrics.PadY;

    private readonly List<AssetCardModel> _cards = [];
    private AssetSelection _selection = new();
    private int _hover = -1;
    private int _columns = 1;
    private ScrollContainer? _scroll;

    public event Action<AssetGridEvent>? Activated;
    public event Action? SelectionChanged;
    public event Action<int, int>? VisibleRangeChanged;
    public event Action<string>? ContextMenuRequested;

    public IReadOnlyCollection<string> SelectedIds => _selection.Ids;

    /// <summary>
    /// Shares one selection model with the list view, so switching view modes keeps exactly what
    /// the user had chosen and Shift-clicking still ranges from the same anchor.
    /// </summary>
    public void BindSelection(AssetSelection selection)
    {
        if (ReferenceEquals(_selection, selection)) return;
        _selection.Changed -= QueueRedraw;
        _selection = selection;
        _selection.Changed += QueueRedraw;
        QueueRedraw();
    }
    public int Count => _cards.Count;
    public int Columns => _columns;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, CardHeight);
    }

    public void Bind(ScrollContainer scroll)
    {
        _scroll = scroll;
        scroll.GetVScrollBar().ValueChanged += _ => QueueRedraw();
    }

    public AssetCardModel? Card(int index) => index >= 0 && index < _cards.Count ? _cards[index] : null;

    public void SetCards(IEnumerable<AssetCardModel> cards)
    {
        _cards.Clear();
        _cards.AddRange(cards);
        _selection.SetView(_cards.Select(card => card.Id));
        Reflow();
    }

    public void UpdateCard(int index, Action<AssetCardModel> mutate)
    {
        if (index < 0 || index >= _cards.Count) return;
        mutate(_cards[index]);
        QueueRedraw();
    }

    public int IndexOf(string id) => _cards.FindIndex(card => card.Id == id);

    private static int ColumnsFor(float width) => Math.Max(1, (int)((width - PadX * 2 + GapX) / (CardWidth + GapX)));

    public void Reflow()
    {
        float width = _scroll is { } scroll && scroll.Size.X > 1 ? scroll.Size.X
            : Size.X > 1 ? Size.X
            : Tokens.CardMetrics.TwoColumns;
        _columns = ColumnsFor(width);
        int rows = (int)Mathf.Ceil((float)_cards.Count / _columns);
        float height = PadY * 2 + rows * CardHeight + Math.Max(0, rows - 1) * GapY;
        CustomMinimumSize = new Vector2(0, Math.Max(CardHeight, height));
        UpdateMinimumSize();
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Reflow();
    }

    /// <summary>Visible column count at a given width; used by callers that must reserve room.</summary>
    public static int ColumnsAt(float width) => ColumnsFor(width);

    public static float WidthForColumns(int columns) => PadX * 2 + columns * CardWidth + Math.Max(0, columns - 1) * GapX;

    public Rect2 CardRect(int index)
    {
        int column = index % _columns, row = index / _columns;
        return new Rect2(PadX + column * (CardWidth + GapX), PadY + row * (CardHeight + GapY), CardWidth, CardHeight);
    }

    public override void _Draw()
    {
        if (_cards.Count == 0) return;

        float top = _scroll?.ScrollVertical ?? 0;
        float bottom = top + Math.Max(_scroll?.Size.Y ?? Size.Y, CardHeight);
        int firstRow = Math.Max(0, (int)((top - PadY) / (CardHeight + GapY)));
        int lastRow = (int)((bottom - PadY) / (CardHeight + GapY)) + 1;
        int first = firstRow * _columns;
        int last = Math.Min(_cards.Count - 1, (lastRow + 1) * _columns - 1);

        for (int i = Math.Max(0, first); i <= last; i++) DrawCard(i, CardRect(i));

        VisibleRangeChanged?.Invoke(Math.Max(0, first), Math.Max(0, last));
    }

    private void DrawCard(int index, Rect2 rect)
    {
        var card = _cards[index];
        bool selected = _selection.Contains(card.Id);
        bool hovered = index == _hover;

        var background = selected || hovered ? Tokens.CardHover : Tokens.Card;
        DrawStyleBox(Surfaces.Fill(background, Tokens.Radius.Card, 0, 0, selected ? Tokens.Accent : Tokens.Line), rect);

        var thumbRect = new Rect2(rect.Position + new Vector2(Tokens.CardMetrics.ThumbInset, Tokens.CardMetrics.ThumbInset),
            new Vector2(CardWidth - Tokens.CardMetrics.ThumbInset * 2, Tokens.CardMetrics.ThumbHeight));
        DrawStyleBox(Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Control, 0, 0), thumbRect);

        if (card.Thumbnail != null)
        {
            var natural = card.Thumbnail.GetSize();
            float scale = Mathf.Max(thumbRect.Size.X / natural.X, thumbRect.Size.Y / natural.Y);
            var drawn = natural * scale;
            DrawTextureRect(card.Thumbnail, new Rect2(thumbRect.Position + (thumbRect.Size - drawn) * .5f, drawn), false);
        }
        else
        {
            var centre = thumbRect.GetCenter();
            Icons.Draw(this, Icons.Cube, new Rect2(centre - new Vector2(15, 15), new Vector2(30, 30)),
                Tokens.Text3.Alpha(.45f), 1.4f);
            if (card.ThumbnailPending)
            {
                const string label = "读取中";
                float width = Typography.Width(Typography.Regular, label, Type.Micro);
                DrawString(Typography.Regular, centre + new Vector2(-width * .5f, 28), label,
                    HorizontalAlignment.Left, -1, Type.Micro, Tokens.Text3);
            }
        }

        if (card.Favorite)
            Icons.Draw(this, Icons.Star, new Rect2(thumbRect.Position + new Vector2(thumbRect.Size.X - 18, 5),
                new Vector2(13, 13)), Tokens.Warn, 1.2f);

        if (card.IsAnimation)
        {
            var badge = new Rect2(thumbRect.Position + new Vector2(7, thumbRect.Size.Y - 23), new Vector2(19, 17));
            DrawStyleBox(Surfaces.Fill(new Color(0, 0, 0, .45f), Tokens.Radius.Chip), badge);
            Icons.Draw(this, Icons.Play, new Rect2(badge.Position + new Vector2(6, 4), new Vector2(9, 9)),
                Tokens.Text.Alpha(.92f), 1f);
        }

        // Baselines are derived from the thumbnail edge so the block can never outgrow the card.
        float textWidth = CardWidth - Tokens.CardMetrics.ThumbInset * 2;
        float left = rect.Position.X + Tokens.CardMetrics.ThumbInset;
        float baseline = rect.Position.Y + Tokens.CardMetrics.ThumbInset + Tokens.CardMetrics.ThumbHeight;

        DrawString(Typography.Semi, new Vector2(left, baseline + 20),
            Typography.Fit(Typography.Semi, card.Name, Type.Row, textWidth),
            HorizontalAlignment.Left, -1, Type.Row, Tokens.Text);

        if (card.MetaLine.Length > 0)
            DrawString(Typography.Mono, new Vector2(left, baseline + 38),
                Typography.Fit(Typography.Mono, card.MetaLine, Type.Micro, textWidth),
                HorizontalAlignment.Left, -1, Type.Micro, Tokens.Text3);

        if (card.DetailLine.Length > 0)
            DrawString(Typography.Regular, new Vector2(left, baseline + 54),
                Typography.Fit(Typography.Regular, card.DetailLine, Type.Micro, textWidth),
                HorizontalAlignment.Left, -1, Type.Micro, Tokens.Text3.Alpha(.9f));

        if (selected)
        {
            var check = rect.Position + new Vector2(Tokens.CardMetrics.ThumbInset, Tokens.CardMetrics.ThumbInset);
            DrawCircle(check + new Vector2(8, 8), 8f, Tokens.Accent);
            Icons.Draw(this, Icons.Check, new Rect2(check + new Vector2(2.5f, 2.5f), new Vector2(11, 11)),
                Tokens.OnAccent, 1.8f);
        }
    }

    private int IndexAt(Vector2 point)
    {
        if (_columns <= 0) return -1;
        float localX = point.X - PadX, localY = point.Y - PadY;
        if (localX < 0 || localY < 0) return -1;
        int column = (int)(localX / (CardWidth + GapX));
        int row = (int)(localY / (CardHeight + GapY));
        if (column >= _columns) return -1;
        // Reject clicks that land in the gap between two cards.
        if (localX - column * (CardWidth + GapX) > CardWidth) return -1;
        if (localY - row * (CardHeight + GapY) > CardHeight) return -1;
        int index = row * _columns + column;
        return index >= 0 && index < _cards.Count ? index : -1;
    }

    public override void _GuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseMotion motion)
        {
            int index = IndexAt(motion.Position);
            if (index != _hover)
            {
                _hover = index;
                QueueRedraw();
            }
            TooltipText = index >= 0 ? _cards[index].Tooltip : "";
        }

        if (ev is InputEventMouseButton { Pressed: true } button)
        {
            if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown) return;

            int hit = IndexAt(button.Position);
            if (hit < 0)
            {
                if (button.ButtonIndex == MouseButton.Left && !button.CtrlPressed && !button.ShiftPressed)
                    _selection.Clear();
                return;
            }

            string id = _cards[hit].Id;
            if (button.ButtonIndex == MouseButton.Right)
            {
                if (!_selection.Contains(id)) _selection.Select(id);
                ContextMenuRequested?.Invoke(id);
                return;
            }
            if (button.ButtonIndex != MouseButton.Left) return;

            GrabFocus();
            if (button.ShiftPressed) _selection.SelectRange(id);
            else if (button.CtrlPressed) _selection.Toggle(id);
            else _selection.Select(id);

            SelectionChanged?.Invoke();
            Activated?.Invoke(new AssetGridEvent(hit, _cards[hit], button.CtrlPressed || button.ShiftPressed));
        }
    }

    public void SelectSingle(string id)
    {
        _selection.Select(id);
        SelectionChanged?.Invoke();
    }

    public void Toggle(string id)
    {
        _selection.Toggle(id);
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        _selection.SetAll(_cards.Select(card => card.Id));
        SelectionChanged?.Invoke();
    }

    public void ScrollToIndex(int index)
    {
        if (_scroll == null || index < 0) return;
        int row = index / _columns;
        _scroll.ScrollVertical = Math.Max(0, PadY + row * (CardHeight + GapY) - 8);
    }

}

/// <summary>Raised when a card is clicked or activated by the keyboard.</summary>
public readonly record struct AssetGridEvent(int Index, AssetCardModel Card, bool Additive);
