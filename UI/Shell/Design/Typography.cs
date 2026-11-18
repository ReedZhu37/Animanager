using Godot;

namespace Animanager.UI.Shell;

/// <summary>Cached typefaces. Resolved once, shared by every widget.</summary>
public static class Typography
{
    private static SystemFont? _regular, _semi, _mono;

    // The CJK fallbacks are not decoration: without them Chinese text falls back to a
    // bitmap face and the whole UI looks unprofessional at any scale.
    private static readonly string[] UiFamilies =
        ["Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "Noto Sans CJK SC", "Noto Sans"];
    private static readonly string[] MonoFamilies =
        ["Cascadia Mono", "Consolas", "JetBrains Mono", "DejaVu Sans Mono"];

    public static SystemFont Regular => _regular ??= Make(400, UiFamilies);
    public static SystemFont Semi => _semi ??= Make(600, UiFamilies);
    public static SystemFont Mono => _mono ??= Make(400, MonoFamilies);

    private static SystemFont Make(int weight, string[] families) => new()
    {
        FontNames = families,
        FontWeight = weight,
        MultichannelSignedDistanceField = true,
        Antialiasing = TextServer.FontAntialiasing.Gray,
        SubpixelPositioning = TextServer.SubpixelPositioning.Auto
    };

    public static float Width(Font font, string text, int size) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;

    /// <summary>Truncates to fit, appending an ellipsis. Pure measurement, no Control involved.</summary>
    public static string Fit(Font font, string text, int size, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (Width(font, text, size) <= maxWidth) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (Width(font, text[..mid] + "…", size) <= maxWidth) low = mid;
            else high = mid - 1;
        }
        return low <= 0 ? "…" : text[..low] + "…";
    }
}

/// <summary>
/// Type scale. Every label in the shell goes through one of these so size drift
/// cannot creep in through ad-hoc AddThemeFontSizeOverride calls.
/// </summary>
public static class Type
{
    public const int DialogTitle = 19;
    public const int SectionTitle = 15;
    public const int Brand = 14;
    public const int Body = 13;
    public const int Row = 12;
    public const int RowStrong = 12;
    public const int Caption = 11;
    public const int Micro = 10;

    public static Label DialogTitleLabel(string text) => Build(text, Typography.Semi, DialogTitle, Tokens.Text);
    public static Label SectionTitleLabel(string text) => Build(text, Typography.Semi, SectionTitle, Tokens.Text);
    public static Label BodyLabel(string text, Color? color = null) =>
        Build(text, Typography.Regular, Body, color ?? Tokens.Text);
    public static Label CaptionLabel(string text, Color? color = null) =>
        Build(text, Typography.Regular, Caption, color ?? Tokens.Text3);
    public static Label MicroLabel(string text, Color? color = null) =>
        Build(text, Typography.Regular, Micro, color ?? Tokens.Text3);
    public static Label MonoLabel(string text, Color? color = null, int size = Micro) =>
        Build(text, Typography.Mono, size, color ?? Tokens.Text3);
    public static Label StrongLabel(string text, Color? color = null, int size = Row) =>
        Build(text, Typography.Semi, size, color ?? Tokens.Text);

    public static Label Build(string text, Font font, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
