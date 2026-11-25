using Godot;

namespace Animanager.UI.Shell;

internal sealed partial class PreviewTabs : VBoxContainer
{
    private const int MaxPanes = 4;
    private const int Gap = 6;
    private const int MinColumnWidth = 360;
    private enum TabAction { Close, CloseOthers, CloseRight, CloseAll }
    private readonly HBoxContainer _tabs = new() { Name = "PreviewTabBar" };
    private readonly MenuButton _arrange = new() { Name = "ArrangePreviews", Text = "并排", TooltipText = "选择要并排显示的预览" };
    private readonly PopupMenu _tabMenu = new() { Name = "PreviewTabContextMenu" };
    private readonly ScrollContainer _tabScroll = new()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, FollowFocus = true
    };
    private readonly ScrollContainer _views = new()
    {
        Name = "PreviewPanes", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill
    };
    private readonly Control _canvas = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly List<PreviewStage> _order = [];
    private readonly HashSet<PreviewStage> _shown = [];
    private readonly Dictionary<PreviewStage, (HBoxContainer Row, Button Tab)> _buttons = [];
    private PreviewStage? _menuStage;

    internal PreviewStage ActiveStage { get; private set; } = null!;
    internal IReadOnlyList<PreviewStage> Stages => _order;
    internal event Action<PreviewStage>? Activated;
    internal event Action<PreviewStage>? CloseRequested;

    internal PreviewTabs()
    {
        Name = "PreviewTabs";
        CustomMinimumSize = new(Tokens.Size.StageMin, 160);
        AddThemeConstantOverride("separation", Gap);
        var bar = new HBoxContainer();
        AddChild(bar);
        bar.AddChild(_tabScroll);
        _tabScroll.AddChild(_tabs);
        bar.AddChild(_arrange);
        _arrange.AboutToPopup += BuildArrangeMenu;
        _arrange.GetPopup().IdPressed += ChoosePane;
        AddChild(_tabMenu);
        _tabMenu.AddItem("关闭标签", (int)TabAction.Close, Key.W | (Key)KeyModifierMask.MaskCtrl);
        _tabMenu.AddSeparator();
        _tabMenu.AddItem("关闭其他标签", (int)TabAction.CloseOthers);
        _tabMenu.AddItem("关闭右侧标签", (int)TabAction.CloseRight);
        _tabMenu.AddItem("关闭所有标签", (int)TabAction.CloseAll);
        _tabMenu.IdPressed += id => ApplyTabAction((TabAction)id);
        AddChild(_views);
        _views.AddChild(_canvas);
        _views.Resized += Arrange;
        _views.GetVScrollBar().ValueChanged += _ => Callable.From(SyncVisibility).CallDeferred();
        _canvas.ItemRectChanged += SyncVisibility;
        _views.GetVScrollBar().VisibilityChanged += Arrange;
        VisibilityChanged += SyncVisibility;
    }

    internal void AddStage(PreviewStage stage)
    {
        stage.Hide();
        _canvas.AddChild(stage);
        _order.Add(stage);
        var tab = new Button
        {
            Text = "预览", CustomMinimumSize = new(124, 28), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            ToggleMode = true, ThemeTypeVariation = "GhostButton"
        };
        var close = new Button
        {
            Text = "×", TooltipText = "关闭预览（Ctrl+W）", CustomMinimumSize = new(24, 28),
            FocusMode = FocusModeEnum.None, ThemeTypeVariation = "GhostButton"
        };
        tab.Pressed += () => Activate(stage);
        tab.GuiInput += input => HandleTabInput(tab, input);
        close.GuiInput += input => HandleTabInput(close, input);
        void HandleTabInput(Control button, InputEvent input)
        {
            if (input is not InputEventMouseButton { Pressed: true } mouse) return;
            if (mouse.ButtonIndex == MouseButton.Middle)
            {
                button.AcceptEvent();
                CloseRequested?.Invoke(stage);
            }
            else if (mouse.ButtonIndex == MouseButton.Right)
            {
                button.AcceptEvent();
                ShowTabMenu(stage, button.GetGlobalTransformWithCanvas() * mouse.Position);
            }
        }
        close.Pressed += () => CloseRequested?.Invoke(stage);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 0);
        row.AddChild(tab);
        row.AddChild(close);
        _tabs.AddChild(row);
        _buttons.Add(stage, (row, tab));
        stage.Viewport.FocusEntered += () => Activate(stage, reveal: false);
        SetTabLabel(stage, "", "");
    }

    internal void SetTabLabel(PreviewStage stage, string title, string tooltip)
    {
        var (row, tab) = _buttons[stage];
        row.Visible = title.Length > 0;
        if (!row.Visible && _menuStage == stage) _tabMenu.Hide();
        tab.Text = title;
        tab.TooltipText = tooltip;
        stage.SetPaneTitle(title);
        _arrange.Disabled = _order.Count < 2;
    }

    internal void Activate(PreviewStage stage, bool reveal = true)
    {
        if (!_order.Contains(stage)) return;
        bool changed = ActiveStage != stage;
        if (!_shown.Contains(stage))
        {
            _shown.Remove(ActiveStage);
            _shown.Add(stage);
        }
        SetActive(stage);
        if (changed) Arrange();
        else _buttons[stage].Tab.SetPressedNoSignal(true);
        if (reveal)
        {
            Callable.From(() =>
            {
                if (!IsVisibleInTree() || ActiveStage != stage) return;
                if (_buttons[stage].Row.Visible) _tabScroll.EnsureControlVisible(_buttons[stage].Row);
                _views.EnsureControlVisible(stage);
            }).CallDeferred();
        }
    }

    internal void RemoveStage(PreviewStage stage)
    {
        if (_menuStage == stage)
        {
            _tabMenu.Hide();
            _menuStage = null;
        }
        int index = _order.IndexOf(stage);
        var neighbors = _order.Skip(index + 1).Concat(_order.Take(index).Reverse()).ToArray();
        _order.RemoveAt(index);
        _shown.Remove(stage);
        if (ActiveStage == stage)
        {
            var next = neighbors.FirstOrDefault(_shown.Contains) ?? neighbors[0];
            _shown.Add(next);
            SetActive(next);
        }
        var (row, _) = _buttons[stage];
        _buttons.Remove(stage);
        _tabs.RemoveChild(row);
        row.QueueFree();
        stage.Viewport.SetPresentationVisible(false);
        _canvas.RemoveChild(stage);
        stage.QueueFree();
        Arrange();
        Activate(ActiveStage);
    }

    internal bool TryCloseActiveTab()
    {
        if (!_buttons.TryGetValue(ActiveStage, out var button) || !button.Row.Visible) return false;
        CloseRequested?.Invoke(ActiveStage);
        return true;
    }

    private void ShowTabMenu(PreviewStage stage, Vector2 position)
    {
        _menuStage = stage;
        _tabMenu.SetItemDisabled(_tabMenu.GetItemIndex((int)TabAction.CloseOthers), _order.Count == 1);
        _tabMenu.SetItemDisabled(_tabMenu.GetItemIndex((int)TabAction.CloseRight), _order[^1] == stage);
        var popupPosition = (Vector2I)(GetWindow().GetFinalTransform() * position);
        _tabMenu.ResetSize();
        _tabMenu.PopupOnParent(new Rect2I(popupPosition, Vector2I.One));
    }

    private void ApplyTabAction(TabAction action)
    {
        if (_menuStage is not { } stage || !_buttons.TryGetValue(stage, out var button) || !button.Row.Visible) return;
        PreviewStage[] closing = action switch
        {
            TabAction.Close => [stage],
            TabAction.CloseOthers => _order.Where(other => other != stage).ToArray(),
            TabAction.CloseRight => _order.Skip(_order.IndexOf(stage) + 1).ToArray(),
            TabAction.CloseAll => _order.ToArray(),
            _ => []
        };
        if (action is TabAction.CloseOthers or TabAction.CloseRight && closing.Contains(ActiveStage))
            Activate(stage);
        foreach (var target in closing.OrderBy(target => target == ActiveStage)) CloseRequested?.Invoke(target);
    }

    private void SetActive(PreviewStage stage)
    {
        if (ActiveStage == stage) return;
        ActiveStage = stage;
        Activated?.Invoke(stage);
    }

    private void BuildArrangeMenu()
    {
        var menu = _arrange.GetPopup();
        menu.Clear();
        menu.AddItem("单视图", 0);
        menu.SetItemDisabled(0, _shown.Count <= 1);
        menu.AddSeparator();
        for (int i = 0; i < _order.Count; i++)
        {
            var stage = _order[i];
            var tab = _buttons[stage].Tab;
            menu.AddCheckItem(Typography.Fit(Typography.Regular, tab.Text, Type.Body, 340), i + 1);
            int item = menu.ItemCount - 1;
            menu.SetItemTooltip(item, tab.TooltipText);
            menu.SetItemChecked(item, _shown.Contains(stage));
            menu.SetItemDisabled(item, _shown.Contains(stage) ? _shown.Count == 1 : _shown.Count == MaxPanes);
        }
        menu.HideOnCheckableItemSelection = false;
    }

    private void ChoosePane(long id)
    {
        if (id == 0)
        {
            _shown.Clear();
            _shown.Add(ActiveStage);
        }
        else
        {
            var stage = _order[(int)id - 1];
            if (_shown.Contains(stage))
            {
                if (_shown.Count == 1) return;
                _shown.Remove(stage);
                if (ActiveStage == stage) SetActive(_order.First(_shown.Contains));
            }
            else if (_shown.Count < MaxPanes) _shown.Add(stage);
        }
        Arrange();
        var menu = _arrange.GetPopup();
        if (menu.Visible)
        {
            menu.SetItemDisabled(0, _shown.Count <= 1);
            for (int i = 0; i < _order.Count; i++)
            {
                bool shown = _shown.Contains(_order[i]);
                int item = menu.GetItemIndex(i + 1);
                menu.SetItemChecked(item, shown);
                menu.SetItemDisabled(item, shown ? _shown.Count == 1 : _shown.Count == MaxPanes);
            }
        }
    }

    private void Arrange()
    {
        if (_shown.Count == 0) return;
        var visible = _order.Where(_shown.Contains).ToArray();
        float width = _views.Size.X;
        float scrollbar = _views.GetVScrollBar().GetCombinedMinimumSize().X;
        int columns = visible.Length > 1 && width >= 2 * MinColumnWidth + Gap + scrollbar ? 2 : 1;
        int rows = (visible.Length + columns - 1) / columns;
        float height = Math.Max(160, (_views.Size.Y - Gap * (rows - 1)) / rows);
        float contentHeight = height * rows + Gap * (rows - 1);
        _canvas.CustomMinimumSize = new(0, contentHeight);
        if (contentHeight > _views.Size.Y + .5f) width -= scrollbar;
        float paneWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        foreach (var stage in _order) stage.Visible = _shown.Contains(stage);
        for (int i = 0; i < visible.Length; i++)
        {
            var stage = visible[i];
            stage.Position = new((i % columns) * (paneWidth + Gap), (i / columns) * (height + Gap));
            stage.Size = new(paneWidth, height);
            stage.SetPanePresentation(visible.Length > 1, stage == ActiveStage);
        }
        foreach (var (stage, (_, tab)) in _buttons)
        {
            tab.SetPressedNoSignal(stage == ActiveStage);
            tab.AddThemeStyleboxOverride("normal", Surfaces.Fill(_shown.Contains(stage) ? Tokens.Panel : Colors.Transparent,
                Tokens.Radius.Control));
            var active = Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control, border: Tokens.Accent);
            tab.AddThemeStyleboxOverride("pressed", active);
            tab.AddThemeStyleboxOverride("hover_pressed", active);
        }
        _arrange.Disabled = _order.Count < 2;
        _arrange.Text = visible.Length > 1 ? $"并排 · {visible.Length}" : "并排";
        SyncVisibility();
    }

    private void SyncVisibility()
    {
        var bounds = _views.GetGlobalRect();
        foreach (var stage in _order)
        {
            var viewport = stage.Viewport;
            viewport.SetPresentationVisible(viewport.IsVisibleInTree() && bounds.Intersects(viewport.GetGlobalRect()));
        }
    }

    public override void _Input(InputEvent input)
    {
        if (_shown.Count < 2 || !IsVisibleInTree() ||
            input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Middle or MouseButton.Right })
            return;
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered == null) return;
        foreach (var stage in _shown)
        {
            if (stage == ActiveStage || !stage.IsVisibleInTree() || (hovered != stage && !stage.IsAncestorOf(hovered))) continue;
            Activate(stage, reveal: false);
            break;
        }
    }
}
