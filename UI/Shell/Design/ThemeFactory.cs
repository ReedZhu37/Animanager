using Godot;

namespace Animanager.UI.Shell;

/// <summary>StyleBox construction. One place that knows about borders, radii and padding.</summary>
public static class Surfaces
{
    public static MarginContainer Padding(int inset)
    {
        var padding = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            padding.AddThemeConstantOverride("margin_" + side, inset);
        return padding;
    }
    public static StyleBoxFlat Fill(Color background, int radius = Tokens.Radius.Control,
        int padX = 0, int padY = 0, Color? border = null, int borderWidth = 1)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY
        };
        box.SetCornerRadiusAll(radius);
        if (border.HasValue)
        {
            box.BorderColor = border.Value;
            box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = borderWidth;
        }
        return box;
    }

    /// <summary>Transparent body with a visible outline; used for focus rings and selection.</summary>
    public static StyleBoxFlat Outline(Color line, int radius = Tokens.Radius.Control)
    {
        var box = Fill(Colors.Transparent, radius, 0, 0, line, 1);
        return box;
    }

    /// <summary>Background only, with square corners. Optionally draws one edge as a 1px rule.</summary>
    public static StyleBoxFlat Bar(Color background, bool squareTop, bool squareBottom, Color? line = null,
        int lineWidth = 1)
    {
        var box = Fill(background, 0);
        if (!line.HasValue) return box;
        box.BorderColor = line.Value;
        if (squareTop) box.BorderWidthTop = lineWidth;
        if (squareBottom) box.BorderWidthBottom = lineWidth;
        return box;
    }

    public static StyleBoxFlat Hairline(bool horizontal, Color? color = null)
    {
        var box = Fill(color ?? Tokens.Line, 0);
        box.ContentMarginLeft = box.ContentMarginRight = 0;
        box.ContentMarginTop = box.ContentMarginBottom = 0;
        box.ExpandMarginLeft = 0;
        box.ExpandMarginRight = 0;
        return box;
    }
}

/// <summary>Theme resource wiring every stock Godot control to the design tokens.</summary>
public static class ThemeFactory
{
    public static Theme Create()
    {
        var theme = new Theme { DefaultFont = Typography.Regular, DefaultFontSize = Type.Body };

        theme.SetColor("font_color", "Label", Tokens.Text);
        theme.SetFontSize("font_size", "Label", Type.Body);

        foreach (var type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, Surfaces.Fill(Tokens.Raised, Tokens.Radius.Control, 12, 6, Tokens.LineStrong));
            theme.SetStylebox("hover", type, Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control, 12, 6, Tokens.LineStrong));
            theme.SetStylebox("pressed", type, Surfaces.Fill(Tokens.Active, Tokens.Radius.Control, 12, 6, Tokens.LineStrong));
            theme.SetStylebox("disabled", type, Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 12, 6, Tokens.Line));
            theme.SetStylebox("focus", type, Surfaces.Outline(Tokens.Accent.Alpha(.55f)));
            theme.SetColor("font_color", type, Tokens.Text);
            theme.SetColor("font_hover_color", type, Tokens.Text);
            theme.SetColor("font_pressed_color", type, Tokens.Text);
            theme.SetColor("font_disabled_color", type, Tokens.Text3);
            theme.SetFont("font", type, Typography.Regular);
            theme.SetFontSize("font_size", type, Type.Body);
            theme.SetConstant("h_separation", type, Tokens.Space.Sm - 2);
        }

        RegisterVariation(theme, "PrimaryButton", "Button");
        theme.SetStylebox("normal", "PrimaryButton", Surfaces.Fill(Tokens.Accent, Tokens.Radius.Control, 12, 6));
        theme.SetStylebox("hover", "PrimaryButton", Surfaces.Fill(new Color("63e0c6"), Tokens.Radius.Control, 12, 6));
        theme.SetStylebox("pressed", "PrimaryButton", Surfaces.Fill(Tokens.AccentDim, Tokens.Radius.Control, 12, 6));
        theme.SetColor("font_color", "PrimaryButton", Tokens.OnAccent);
        theme.SetColor("font_hover_color", "PrimaryButton", Tokens.OnAccent);
        theme.SetColor("font_pressed_color", "PrimaryButton", Tokens.OnAccent);
        theme.SetFont("font", "PrimaryButton", Typography.Semi);

        RegisterVariation(theme, "DockTab", "Button");
        theme.SetStylebox("normal", "DockTab", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control, 12, 6));
        theme.SetStylebox("hover", "DockTab", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control, 12, 6));
        theme.SetStylebox("pressed", "DockTab", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control, 12, 6));
        theme.SetColor("font_color", "DockTab", Tokens.Text2);
        theme.SetColor("font_pressed_color", "DockTab", Tokens.Accent);
        foreach (var type in new[] { "CheckBox", "CheckButton" })
        {
            foreach (var state in new[] { "normal", "disabled", "hover", "pressed", "hover_pressed" })
                theme.SetStylebox(state, type, Surfaces.Fill(state is "hover" or "hover_pressed" ? Tokens.Hover : Colors.Transparent, Tokens.Radius.Control, 8, 6));
            theme.SetStylebox("focus", type, Surfaces.Outline(Tokens.Accent.Alpha(.5f)));
            theme.SetConstant("h_separation", type, 8);
        }

        RegisterVariation(theme, "GhostButton", "Button");
        theme.SetStylebox("normal", "GhostButton", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        theme.SetStylebox("hover", "GhostButton", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control));
        theme.SetStylebox("pressed", "GhostButton", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control));
        theme.SetStylebox("disabled", "GhostButton", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control));
        theme.SetColor("font_color", "GhostButton", Tokens.Text2);
        theme.SetColor("font_hover_color", "GhostButton", Tokens.Text);
        theme.SetColor("font_disabled_color", "GhostButton", Tokens.Text3);

        RegisterVariation(theme, "Chip", "Button");
        theme.SetStylebox("normal", "Chip", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Pill, 10, 2, Tokens.Line));
        theme.SetStylebox("hover", "Chip", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Pill, 10, 2, Tokens.LineStrong));
        theme.SetStylebox("pressed", "Chip", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Pill, 10, 2,
            Tokens.Accent.Alpha(.5f)));
        theme.SetColor("font_color", "Chip", Tokens.Text2);
        theme.SetColor("font_pressed_color", "Chip", Tokens.Accent);
        theme.SetFontSize("font_size", "Chip", Type.Caption);

        theme.SetStylebox("normal", "LineEdit", Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Control, 10, 5, Tokens.LineStrong));
        theme.SetStylebox("focus", "LineEdit", Surfaces.Outline(Tokens.Accent.Alpha(.7f)));
        theme.SetStylebox("read_only", "LineEdit", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 10, 5, Tokens.Line));
        theme.SetColor("font_color", "LineEdit", Tokens.Text);
        theme.SetColor("font_placeholder_color", "LineEdit", Tokens.Text3);
        theme.SetColor("caret_color", "LineEdit", Tokens.Accent);
        theme.SetColor("selection_color", "LineEdit", Tokens.AccentWash);
        theme.SetFont("font", "LineEdit", Typography.Regular);
        theme.SetFontSize("font_size", "LineEdit", Type.Body);

        theme.SetStylebox("panel", "Panel", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Card));
        theme.SetStylebox("panel", "PanelContainer", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Card));

        theme.SetStylebox("panel", "PopupMenu", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Card, 6, 6, Tokens.LineStrong));
        theme.SetStylebox("hover", "PopupMenu", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control, 6, 5));
        theme.SetColor("font_color", "PopupMenu", Tokens.Text);
        theme.SetColor("font_disabled_color", "PopupMenu", Tokens.Text3);
        theme.SetStylebox("separator", "PopupMenu", Surfaces.Fill(Tokens.Line, 0, 0, 1));
        theme.SetFontSize("font_size", "PopupMenu", Type.Body);
        theme.SetConstant("v_separation", "PopupMenu", Tokens.Space.Sm - 2);

        theme.SetStylebox("panel", "AcceptDialog", Surfaces.Fill(Tokens.Raised, Tokens.Radius.Popup, 14, 14, Tokens.LineStrong));

        theme.SetStylebox("panel", "ItemList", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control));
        theme.SetStylebox("selected", "ItemList", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control, 4, 4));
        theme.SetStylebox("selected_focus", "ItemList", Surfaces.Fill(Tokens.Active, Tokens.Radius.Control, 4, 4,
            Tokens.Accent.Alpha(.5f)));
        theme.SetColor("font_color", "ItemList", Tokens.Text);
        theme.SetColor("font_selected_color", "ItemList", Tokens.Text);
        theme.SetConstant("v_separation", "ItemList", Tokens.Space.Sm);

        theme.SetStylebox("slider", "HSlider", Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Pill, 0, 2));
        theme.SetStylebox("grabber_area", "HSlider", Surfaces.Fill(Tokens.Accent.Alpha(.75f), Tokens.Radius.Pill, 0, 2));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Surfaces.Fill(Tokens.Accent, Tokens.Radius.Pill, 0, 2));
        theme.SetConstant("center_grabber", "HSlider", 0);

        theme.SetStylebox("panel", "TabContainer", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Card));
        theme.SetStylebox("panel", "Tree", Surfaces.Fill(Tokens.Sunken, Tokens.Radius.Control, 4, 4, Tokens.Line));
        theme.SetStylebox("selected", "Tree", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Chip));
        theme.SetStylebox("selected_focus", "Tree", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Chip, 0, 0, Tokens.Accent));
        theme.SetColor("font_color", "Tree", Tokens.Text2);
        theme.SetColor("font_selected_color", "Tree", Tokens.Accent);
        theme.SetStylebox("separator", "HSeparator", Surfaces.Fill(Tokens.Line));
        theme.SetStylebox("tab_selected", "TabContainer", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, 10, 5));
        theme.SetStylebox("tab_unselected", "TabContainer", Surfaces.Fill(Colors.Transparent, Tokens.Radius.Control, 10, 5));
        theme.SetStylebox("tab_hovered", "TabContainer", Surfaces.Fill(Tokens.Hover, Tokens.Radius.Control, 10, 5));
        theme.SetColor("font_selected_color", "TabContainer", Tokens.Text);
        theme.SetColor("font_unselected_color", "TabContainer", Tokens.Text2);

        theme.SetConstant("separation", "VBoxContainer", Tokens.Space.Sm);
        theme.SetConstant("separation", "HBoxContainer", Tokens.Space.Sm);
        theme.SetConstant("h_separation", "HFlowContainer", Tokens.Space.Sm);
        theme.SetConstant("v_separation", "HFlowContainer", Tokens.Space.Sm);
        theme.SetConstant("h_separation", "GridContainer", Tokens.Space.Md);
        theme.SetConstant("v_separation", "GridContainer", Tokens.Space.Md);

        foreach (var type in new[] { "SplitContainer", "HSplitContainer", "VSplitContainer" })
        {
            theme.SetConstant("separation", type, Tokens.Space.Sm - 2);
            theme.SetConstant("minimum_grab_thickness", type, Tokens.Space.Sm - 2);
            theme.SetStylebox("split_bar_background", type, Surfaces.Fill(Tokens.Bg, 0));
        }

        return theme;
    }

    private static void RegisterVariation(Theme theme, string name, string baseType) => theme.SetTypeVariation(name, baseType);
}
