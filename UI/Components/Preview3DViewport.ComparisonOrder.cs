using Animanager.UI.Shell;
using Godot;

namespace Animanager.UI.Components;

public partial class Preview3DViewport
{
    private PanelContainer _comparisonOrder = null!;
    private VBoxContainer _comparisonOrderRows = null!;
    private Action? _clearComparisonDropMarker;

    private void BuildComparisonOrder(Control tools, Control overlay)
    {
        var open = new Button { Name = "OpenComparisonOrder", Text = "管理对比…" };
        tools.AddChild(open);
        open.Pressed += () =>
        {
            if (_comparisonOrder.Visible) { _comparisonOrder.Hide(); return; }
            RebuildComparisonOrder();
            _comparisonOrder.Show();
            FitComparisonOrder();
        };
        _comparisonOrder = new PanelContainer { Name = "ComparisonOrder", Visible = false };
        _comparisonOrder.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Panel, padX: 10, padY: 8, border: Tokens.LineStrong));
        overlay.AddChild(_comparisonOrder);
        overlay.Resized += FitComparisonOrder;
        var body = new VBoxContainer();
        _comparisonOrder.AddChild(body);
        var header = new HBoxContainer();
        body.AddChild(header);
        var title = Shell.Type.SectionTitleLabel("对比素材");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        var close = new Button { Text = "完成", ThemeTypeVariation = "GhostButton" };
        close.Pressed += _comparisonOrder.Hide;
        header.AddChild(close);
        body.AddChild(Shell.Type.CaptionLabel("拖动排序 · 对应视口从左到右", Tokens.Text2));
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true
        };
        body.AddChild(scroll);
        _comparisonOrderRows = new VBoxContainer { Name = "ComparisonOrderList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_comparisonOrderRows);
    }

    private void FitComparisonOrder()
    {
        if (!_comparisonOrder.Visible) return;
        _comparisonOrder.Position = new Vector2(8, Size.Y >= 200 ? 76 : 38);
        _comparisonOrder.Size = new Vector2(Math.Min(372, Size.X - 16), Math.Min(288, Size.Y - _comparisonOrder.Position.Y - 8));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd) ClearComparisonDropMarker();
    }

    private void ClearComparisonDropMarker()
    {
        _clearComparisonDropMarker?.Invoke();
        _clearComparisonDropMarker = null;
    }

    private void UpdateComparisonActions()
    {
        if (AssetComparison == null) _comparisonOrder.Hide();
        else if (_comparisonOrder.Visible) RebuildComparisonOrder();
        UpdateComparisonNames();
    }

    private void RebuildComparisonOrder()
    {
        ClearComparisonDropMarker();
        foreach (var row in _comparisonOrderRows.GetChildren())
        {
            _comparisonOrderRows.RemoveChild(row);
            row.QueueFree();
        }
        if (AssetComparison is not { } comparison) return;
        for (int i = 0; i < comparison.Count; i++)
            AddComparisonOrderRow(comparison, comparison.Entries[i], i);
    }

    private void AddComparisonOrderRow(AnimationComparison comparison, AnimationComparison.Entry entry, int index)
    {
        var row = new PanelContainer
        {
            Name = $"ComparisonOrderRow{index}", MouseDefaultCursorShape = CursorShape.Drag,
            MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new(0, 40)
        };
        var normal = Surfaces.Fill(Tokens.Raised, padX: 10, padY: 4);
        var before = Surfaces.Fill(Tokens.Active, padX: 10, padY: 4);
        var after = Surfaces.Fill(Tokens.Active, padX: 10, padY: 4);
        before.BorderColor = after.BorderColor = Tokens.Accent;
        before.BorderWidthTop = after.BorderWidthBottom = 3;
        row.AddThemeStyleboxOverride("panel", normal);
        _comparisonOrderRows.AddChild(row);
        var contents = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(contents);
        var grip = Shell.Type.BodyLabel("⠿", Tokens.Text2);
        grip.MouseFilter = MouseFilterEnum.Ignore;
        contents.AddChild(grip);
        var name = Shell.Type.BodyLabel(entry.Label.Text);
        name.MouseFilter = MouseFilterEnum.Ignore;
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.TooltipText = entry.Label.Text;
        contents.AddChild(name);
        if (entry.Key is { } key)
        {
            var remove = new Button
            {
                Name = "RemoveComparisonRow", Text = "移除", ThemeTypeVariation = "GhostButton",
                TooltipText = $"从对比中移除「{entry.Label.Text}」", MouseDefaultCursorShape = CursorShape.PointingHand
            };
            remove.Pressed += () => ComparisonRemoveRequested?.Invoke(key);
            contents.AddChild(remove);
        }
        else
        {
            var current = Shell.Type.CaptionLabel("当前预览", Tokens.Text2);
            current.MouseFilter = MouseFilterEnum.Ignore;
            contents.AddChild(current);
        }

        int DraggedIndex(Variant data)
        {
            if (data.VariantType != Variant.Type.Int || AssetComparison != comparison) return -1;
            ulong id = data.AsUInt64();
            for (int i = 0; i < comparison.Count; i++)
                if (comparison.Entries[i].Root.GetInstanceId() == id) return i;
            return -1;
        }
        int Insertion(Vector2 position) => index + (position.Y >= row.Size.Y * .5f ? 1 : 0);
        void ResetMarker() => row.AddThemeStyleboxOverride("panel", normal);
        row.MouseExited += ResetMarker;
        row.SetDragForwarding(Callable.From<Vector2, Variant>(_ =>
        {
            var preview = new PanelContainer();
            preview.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Active, padX: 10, padY: 6, border: Tokens.Accent));
            var label = Shell.Type.BodyLabel(entry.Label.Text);
            label.CustomMinimumSize = new(120, 24);
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.CustomMaximumSize = new(280, 0);
            preview.AddChild(label);
            row.SetDragPreview(preview);
            return Variant.From(entry.Root.GetInstanceId());
        }), Callable.From<Vector2, Variant, bool>((position, data) =>
        {
            int source = DraggedIndex(data);
            int insertion = Insertion(position);
            bool canMove = source >= 0 && insertion != source && insertion != source + 1;
            ClearComparisonDropMarker();
            if (canMove)
            {
                row.AddThemeStyleboxOverride("panel", insertion == index ? before : after);
                _clearComparisonDropMarker = ResetMarker;
            }
            return canMove;
        }), Callable.From<Vector2, Variant>((position, data) =>
        {
            if (!comparison.MoveEntry(DraggedIndex(data), Insertion(position))) return;
            UpdateComparisonActions();
            Redraw();
        }));
    }
}
