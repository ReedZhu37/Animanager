using Godot;

namespace Animanager.UI.Shell;

/// <summary>A navigation row: glyph, label, optional count. Active state is a 2px accent bar.</summary>
public partial class NavRow : Button
{
    private readonly Label _label;
    private readonly Label _count;
    private readonly IconView _icon;
    private readonly ColorRect _bar;

    public NavRow(string icon, string text, int iconSize = 15)
    {
        Flat = true;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(0, Tokens.Size.Row);
        AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Hover.Alpha(.7f), Tokens.Radius.Control));
        AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control));
        AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_left", 14);
        pad.AddThemeConstantOverride("margin_right", 10);
        pad.AddChild(new Control { CustomMinimumSize = new Vector2(0, Tokens.Size.Row) });
        AddChild(pad);
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 9);
        pad.AddChild(row);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _icon = new IconView(icon, iconSize, Tokens.Text3);
        row.AddChild(_icon);

        _label = Type.Build(text, Typography.Regular, Type.Row, Tokens.Text2);
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(_label);

        _count = Type.MonoLabel("", Tokens.Text3);
        _count.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_count);

        _bar = new ColorRect { Color = Tokens.Accent, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        AddChild(_bar);
        _bar.SetAnchorsPreset(LayoutPreset.CenterLeft);
        _bar.OffsetRight = 2;
        _bar.OffsetTop = -8;
        _bar.OffsetBottom = 8;
    }

    public string LabelText => _label.Text;

    public void SetIcon(string icon) => _icon.Set(icon);

    public void SetCount(int count) => _count.Text = count > 0 ? count.ToString("N0") : "";

    public void SetActive(bool active)
    {
        _bar.Visible = active;
        _label.AddThemeColorOverride("font_color", active ? Tokens.Text : Tokens.Text2);
        _label.AddThemeFontOverride("font", active ? Typography.Semi : Typography.Regular);
        _icon.SetTint(active ? Tokens.Accent : Tokens.Text3);
        AddThemeStyleboxOverride("normal",
            Surfaces.Fill(active ? Tokens.Active.Alpha(.55f) : Colors.Transparent, Tokens.Radius.Control));
    }
}

/// <summary>Section caption above a group of navigation rows, with an optional add action.</summary>
public partial class NavSection : MarginContainer
{
    public NavSection(string title, Action? onAdd = null)
    {
        AddThemeConstantOverride("margin_left", 14);
        AddThemeConstantOverride("margin_right", 10);
        AddThemeConstantOverride("margin_top", 6);
        AddThemeConstantOverride("margin_bottom", 4);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        AddChild(row);
        var label = Type.Build(title, Typography.Semi, Type.Micro, Tokens.Text3);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);
        if (onAdd == null) return;
        var add = new IconButton(Icons.Plus, 12, "新建");
        add.CustomMinimumSize = new Vector2(18, 18);
        add.Pressed += onAdd;
        row.AddChild(add);
    }
}
