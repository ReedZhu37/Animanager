using Godot;

namespace Animanager.UI.Shell;

internal sealed partial class ComparisonDropHint : CenterContainer
{
    private readonly Label _message;
    private readonly string _prompt;
    private Color _outline = Tokens.Accent;

    public ComparisonDropHint(string name, string prompt)
    {
        Name = name;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        _prompt = prompt;
        _message = Type.Build(prompt, Typography.Semi, Type.Body, Tokens.Accent);
        _message.AddThemeStyleboxOverride("normal", Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control,
            Tokens.Space.Md, Tokens.Space.Xs));
        AddChild(_message);
        Resized += QueueRedraw;
    }

    public void ShowForDrag(bool sameAnimation)
    {
        _outline = sameAnimation ? Tokens.Danger : Tokens.Accent;
        _message.Text = sameAnimation ? "不能比较同一个动画" : _prompt;
        _message.AddThemeColorOverride("font_color", _outline);
        Show();
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Tokens.Sunken.Alpha(.32f));
        const float inset = Tokens.Space.Xs;
        if (Size.X <= 2 * inset || Size.Y <= 2 * inset) return;

        var topLeft = new Vector2(inset, inset);
        var topRight = new Vector2(Size.X - inset, inset);
        var bottomRight = Size - topLeft;
        var bottomLeft = new Vector2(inset, Size.Y - inset);
        DrawDashedLine(topLeft, topRight, _outline, 1.5f, 7, true, true);
        DrawDashedLine(topRight, bottomRight, _outline, 1.5f, 7, true, true);
        DrawDashedLine(bottomRight, bottomLeft, _outline, 1.5f, 7, true, true);
        DrawDashedLine(bottomLeft, topLeft, _outline, 1.5f, 7, true, true);
    }
}
