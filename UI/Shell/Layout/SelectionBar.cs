using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Contextual actions shown once more than one asset is selected.
/// Actions are container-based buttons, so their glyph and label never overlap.
/// </summary>
public partial class SelectionBar : PanelContainer
{
    public event Action<string>? ActionRequested;

    private readonly Label _count;
    private readonly List<Button> _normalActions = [];
    private readonly List<Button> _trashActions = [];

    public SelectionBar()
    {
        Visible = false;
        AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control, 0, 0,
            Tokens.Accent.Alpha(.32f)));

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 8);
        pad.AddThemeConstantOverride("margin_top", 4);
        pad.AddThemeConstantOverride("margin_bottom", 4);
        AddChild(pad);

        var row = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 4);
        pad.AddChild(row);

        _count = Type.Build("已选 0 项", Typography.Semi, Type.Row, Tokens.Accent);
        _count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _count.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_count);

        foreach (var (icon, text, id) in new[]
                 {
                     (Icons.Tag, "标签", "tags"), (Icons.Star, "收藏", "favorite"),
                     (Icons.Layers, "集合", "collection"), (Icons.Export, "导出", "export")
                 })
        {
            var button = Action(icon, text, id, Tokens.Text2, Tokens.Text);
            row.AddChild(button); _normalActions.Add(button);
        }

        var trash = Action(Icons.Trash, "删除", "trash", Tokens.Danger.Alpha(.9f), Tokens.Danger);
        row.AddChild(trash); _normalActions.Add(trash);
        foreach (var (icon, label, id) in new[] { (Icons.Repeat, "恢复", "restore"), (Icons.Trash, "永久删除", "purge") })
        {
            var button = Action(icon, label, id, Tokens.Text2, id == "purge" ? Tokens.Danger : Tokens.Text);
            row.AddChild(button); _trashActions.Add(button); button.Hide();
        }
    }

    private Button Action(string icon, string text, string id, Color iconTint, Color textTint)
    {
        var button = new Button { CustomMinimumSize = new Vector2(0, 28), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        button.AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        button.AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Active.Alpha(.8f), Tokens.Radius.Control));
        button.AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control));
        button.AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, 0));

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_left", 8);
        pad.AddThemeConstantOverride("margin_right", 8);
        pad.AddThemeConstantOverride("margin_top", 3);
        pad.AddThemeConstantOverride("margin_bottom", 3);
        button.AddChild(pad);
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 5);
        pad.AddChild(row);
        row.AddChild(new IconView(icon, 13, iconTint));
        row.AddChild(Type.Build(text, Typography.Regular, Type.Caption, textTint));

        button.CustomMinimumSize = new Vector2(13 + 5 + Typography.Width(Typography.Regular, text, Type.Caption) + 16, 28);
        button.Pressed += () => ActionRequested?.Invoke(id);
        return button;
    }

    public void SetCount(int count)
    {
        _count.Text = $"已选 {count} 项";
        Visible = count > 1;
    }
    public void SetTrashMode(bool trash)
    {
        foreach (var action in _normalActions) action.Visible = !trash;
        foreach (var action in _trashActions) action.Visible = trash;
    }

    public void SetEditingEnabled(bool enabled)
    {
        foreach (var action in _normalActions) action.Disabled = !enabled;
        foreach (var action in _trashActions) action.Disabled = !enabled;
    }
}

/// <summary>Filter chip with a removal affordance.</summary>
public partial class FilterChip : Button
{
    public readonly string Key;

    public FilterChip(string key, string label)
    {
        Key = key;
        Text = Typography.Fit(Typography.Regular, label, Type.Caption, 220) + "  ✕";
        CustomMinimumSize = new Vector2(0, 24);
        ThemeTypeVariation = "Chip";
        AddThemeFontSizeOverride("font_size", Type.Caption);
        TooltipText = "移除筛选：" + label;
    }
}

/// <summary>Row of active filter chips plus a clear-all action.</summary>
public partial class FilterBar : HFlowContainer
{
    public event Action<string>? Removed;
    public event Action? Cleared;

    private readonly Button _clear;
    private bool _expanded = true;

    public FilterBar()
    {
        AddThemeConstantOverride("h_separation", 6);
        AddThemeConstantOverride("v_separation", 4);
        _clear = new Button { Text = "清除全部", ThemeTypeVariation = "GhostButton", CustomMinimumSize = new Vector2(0, 24) };
        _clear.AddThemeFontSizeOverride("font_size", Type.Caption);
        _clear.Pressed += () => Cleared?.Invoke();
        AddChild(_clear);
        _clear.Hide();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Visible = expanded && _clear.Visible;
    }

    public void SetFilters(IEnumerable<(string Key, string Label)> filters)
    {
        foreach (var child in GetChildren())
        {
            if (child == _clear) continue;
            RemoveChild(child);
            child.QueueFree();
        }
        bool any = false;
        foreach (var (key, label) in filters)
        {
            any = true;
            var chip = new FilterChip(key, label);
            var captured = key;
            chip.Pressed += () => Removed?.Invoke(captured);
            AddChild(chip);
        }
        _clear.Visible = any;
        MoveChild(_clear, -1);
        Visible = any && _expanded;
    }
}
