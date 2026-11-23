using Godot;

namespace Animanager.UI.Components;

/// <summary>World anchor only. Text is rasterized in the window's 2D canvas, never in the 3D render texture.</summary>
public partial class BoneLabelAnchor : Node3D
{
    public string Text { get; set; } = "";
    public Color Tint { get; set; } = Colors.White;
}

public partial class BoneScreenLabels : Control
{
    public const int FontPixels = 13;
    public Preview3DViewport Preview { get; set; } = null!;
    public override void _Draw()
    {
        if (Preview.Bones == null || !Preview.IsInsideTree()) return;
        var font = GetThemeDefaultFont();
        var occupied = new List<Rect2>();
        // Keep annotations readable beside navigation controls and nearby bone/path labels.
        if (Preview.GetNodeOrNull<Control>("Navigation") is { } navigation)
            occupied.AddRange(navigation.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree())
                .Select(c => new Rect2(c.GlobalPosition - GlobalPosition, c.Size).Grow(3)));
        float ascent = font.GetAscent(FontPixels), lineHeight = font.GetHeight(FontPixels) + 4;
        var labels = Preview.AssetComparison?.Labels ?? Preview3DViewport.Descendants(Preview.Bones.Overlay).OfType<BoneLabelAnchor>();
        foreach (var anchor in labels)
        {
            if (!anchor.IsVisibleInTree() || Preview.ProjectLabel(anchor.GlobalPosition) is not { } point) continue;
            var textSize = font.GetStringSize(anchor.Text, fontSize: FontPixels);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int row = attempt % 8;
                float dy = (row % 2 == 0 ? -row / 2 : (row + 1) / 2) * lineHeight;
                var origin = point + new Vector2(attempt < 8 ? 8 : -textSize.X - 8, -8 - ascent + dy);
                origin.X = Math.Clamp(origin.X, 4, Math.Max(4, Size.X - textSize.X - 4));
                origin.Y = Math.Clamp(origin.Y, 4, Math.Max(4, Size.Y - textSize.Y - 4));
                var rect = new Rect2(origin, textSize).Grow(2);
                if (occupied.Any(r => r.Intersects(rect))) continue;
                occupied.Add(rect);
                var baseline = origin + new Vector2(0, ascent);
                DrawStringOutline(font, baseline, anchor.Text, fontSize: FontPixels, size: 4, modulate: new Color(0, 0, 0, .85f));
                DrawString(font, baseline, anchor.Text, fontSize: FontPixels, modulate: anchor.Tint);
                break;
            }
        }
    }
}
