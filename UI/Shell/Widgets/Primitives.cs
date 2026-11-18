using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Paints a vector icon. Never anchors its children, so its size is fully described by
/// CustomMinimumSize and containers can measure it.
/// </summary>
public partial class IconView : Control
{
    public string Icon { get; private set; } = Icons.More;
    public Color Tint { get; private set; } = Tokens.Text2;
    public float Weight { get; private set; } = 1.6f;

    public IconView() { }

    public IconView(string icon, int size = 16, Color? tint = null)
    {
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
        Set(icon, tint);
    }

    public void Set(string icon, Color? tint = null)
    {
        Icon = icon;
        if (tint.HasValue) Tint = tint.Value;
        if (IsInsideTree()) QueueRedraw();
    }

    public void SetTint(Color tint)
    {
        Tint = tint;
        if (IsInsideTree()) QueueRedraw();
    }

    public override void _Draw()
    {
        float side = Mathf.Min(Size.X, Size.Y);
        if (side <= 0) return;
        Icons.Draw(this, Icon, new Rect2(0, 0, side, side), Tint, Weight);
    }
}

/// <summary>Status dot whose colour carries severity.</summary>
public partial class StatusDot : Control
{
    public Color Tint { get; set; } = Tokens.Success;

    public StatusDot(int size = 10)
    {
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void SetTint(Color tint)
    {
        Tint = tint;
        if (IsInsideTree()) QueueRedraw();
    }

    public override void _Draw()
    {
        var centre = Size * .5f;
        DrawCircle(centre, 3f * (Size.X / 10f), Tint);
    }
}

/// <summary>Small tinted capsule, e.g. a count on the filter button.</summary>
public partial class BadgePill : PanelContainer
{
    private readonly Label _label;

    public BadgePill(string text, Color? tint = null)
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var colour = tint ?? Tokens.Text2;
        AddThemeStyleboxOverride("panel", Surfaces.Fill(colour.Alpha(.12f), Tokens.Radius.Chip, 6, 1));
        _label = Type.Build(text, Typography.Semi, Type.Micro, colour);
        _label.VerticalAlignment = VerticalAlignment.Center;
        AddChild(_label);
    }

    public void SetText(string text) => _label.Text = text;

    public void SetTint(Color tint)
    {
        AddThemeStyleboxOverride("panel", Surfaces.Fill(tint.Alpha(.12f), Tokens.Radius.Chip, 6, 1));
        _label.AddThemeColorOverride("font_color", tint);
    }
}

/// <summary>
/// Icon plus optional label. The width is published explicitly because Button is not a
/// container: an anchored child contributes nothing to the parent's minimum size and the
/// row collapses once the window narrows.
/// </summary>
public partial class CapButton : Button
{
    public const int HorizontalPadding = 11;
    public const int Gap = 7;

    private readonly IconView? _icon;
    private readonly int _iconSize;

    public CapButton(string icon, string text, bool primary = false, bool ghost = false, int iconSize = 15)
    {
        _iconSize = iconSize;
        CustomMinimumSize = new Vector2(0, Tokens.Size.Control);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.All;
        // Without the variation the primary colours would be painted on the default surface.
        if (primary) ThemeTypeVariation = "PrimaryButton";
        else if (ghost) ThemeTypeVariation = "GhostButton";

        var tint = primary ? Tokens.OnAccent : Tokens.Text2;
        _icon = new IconView(icon, iconSize, tint);

        // A Button is not a container: an unanchored child sits at (0,0) at its minimum size.
        // The padding therefore has to fill the button explicitly.
        var pad = new MarginContainer();
        pad.MouseFilter = MouseFilterEnum.Ignore;
        pad.AddThemeConstantOverride("margin_left", HorizontalPadding);
        pad.AddThemeConstantOverride("margin_right", HorizontalPadding);
        pad.AddThemeConstantOverride("margin_top", 3);
        pad.AddThemeConstantOverride("margin_bottom", 3);
        AddChild(pad);
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", Gap);
        pad.AddChild(row);
        row.AddChild(_icon);

        float textWidth = 0;
        if (text.Length > 0)
        {
            var label = Type.Build(text, Typography.Semi, Type.Row, primary ? Tokens.OnAccent : Tokens.Text);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Visible = true;
            row.AddChild(label);
            textWidth = Typography.Width(Typography.Semi, text, Type.Row) + Gap;
        }

        CustomMinimumSize = new Vector2(iconSize + textWidth + HorizontalPadding * 2, Tokens.Size.Control);
        MouseEntered += () => _icon.SetTint(primary ? Tokens.OnAccent : Tokens.Text);
        MouseExited += () => _icon.SetTint(primary ? Tokens.OnAccent : Tokens.Text2);
    }

    public CapButton(string text, bool primary = false) : this(Icons.Plus, text, primary) { }
}

/// <summary>Icon-only square button. 26–30px hit area depending on glyph size.</summary>
public partial class IconButton : Button
{
    private readonly IconView _icon;
    private readonly int _glyphSize;

    public IconButton(string icon, int glyphSize = 16, string tooltip = "")
    {
        _glyphSize = glyphSize;
        var side = glyphSize + 10;
        CustomMinimumSize = new Vector2(side, side);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        TooltipText = tooltip;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.All;
        AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control));
        AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control));
        AddThemeStyleboxOverride("focus", Surfaces.Outline(Tokens.Accent.Alpha(.5f)));
        AddThemeStyleboxOverride("disabled", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        _icon = new IconView(icon, glyphSize, Tokens.Text2);
        AddChild(_icon);
        MouseEntered += () => _icon.SetTint(Tokens.Text);
        MouseExited += () => _icon.SetTint(Tokens.Text2);
        // Size the glyph from the button rather than anchoring it: anchored children stay
        // put when a container grows the button, which puts the icon off-centre.
        _icon.SetAnchorsPreset(LayoutPreset.Center);
        _icon.OffsetLeft = -glyphSize * .5f;
        _icon.OffsetTop = -glyphSize * .5f;
        _icon.OffsetRight = glyphSize * .5f;
        _icon.OffsetBottom = glyphSize * .5f;
    }

    public void SetIcon(string icon) => _icon.Set(icon);
    public void SetTint(Color tint) => _icon.SetTint(tint);
    public void SetActive(bool active) =>
        _icon.SetTint(active ? Tokens.Accent : Tokens.Text2);
}

/// <summary>Horizontal hairline. A 1px Control, not a separator with theme padding.</summary>
public static class Hairline
{
    public static ColorRect Horizontal(Color? colour = null) => new()
    {
        Color = colour ?? Tokens.Line,
        CustomMinimumSize = new Vector2(0, 1),
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    public static ColorRect Vertical(Color? colour = null) => new()
    {
        Color = colour ?? Tokens.Line,
        CustomMinimumSize = new Vector2(1, 0),
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    public static Control Gap(int width) => new()
    {
        CustomMinimumSize = new Vector2(width, 0),
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    public static Control Spacer(int height) => new()
    {
        CustomMinimumSize = new Vector2(0, height),
        MouseFilter = Control.MouseFilterEnum.Ignore
    };
}

/// <summary>Search field: glyph, editable text, shortcut hint. Reports through an event.</summary>
public partial class SearchField : PanelContainer
{
    public event Action<string>? TextChanged;
    public event Action? Submitted;

    private readonly LineEdit _edit;

    public SearchField(string placeholder, string? shortcutHint = null)
    {
        CustomMinimumSize = new Vector2(0, Tokens.Size.Control);
        AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Control, 0, 0, Tokens.Line));

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        AddChild(row);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        row.AddChild(Hairline.Gap(10));
        row.AddChild(new IconView(Icons.Search, 15, Tokens.Text3));

        _edit = new LineEdit
        {
            PlaceholderText = placeholder,
            Flat = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, Tokens.Size.Control - 2),
            MouseDefaultCursorShape = CursorShape.Ibeam
        };
        _edit.AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, 0, 8, 0));
        _edit.AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, 0, 8, 0));
        _edit.AddThemeFontSizeOverride("font_size", Type.Caption);
        _edit.TextChanged += text => TextChanged?.Invoke(text);
        _edit.TextSubmitted += _ => Submitted?.Invoke();
        row.AddChild(_edit);

        if (shortcutHint != null)
        {
            var hint = Type.MonoLabel(shortcutHint, Tokens.Text3);
            hint.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(hint);
        }
        row.AddChild(Hairline.Gap(10));
    }

    public LineEdit Input => _edit;
    public string Value => _edit.Text;
    public void SetValue(string value) => _edit.Text = value;
    public new void GrabFocus() => _edit.GrabFocus();
}
