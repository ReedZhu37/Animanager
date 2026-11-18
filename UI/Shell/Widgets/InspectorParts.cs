using Godot;

namespace Animanager.UI.Shell;

/// <summary>Collapsible inspector group: 30px header with chevron, then an indented body.</summary>
public partial class InspectorSection : VBoxContainer
{
    public VBoxContainer Body { get; }

    private readonly MarginContainer _wrapper;
    private readonly IconView _chevron;
    private bool _open;

    public InspectorSection(string title, bool open = true, string? actionIcon = null, Action? onAction = null)
    {
        AddThemeConstantOverride("separation", 0);
        _open = open;

        var header = new Button
        {
            CustomMinimumSize = new Vector2(0, 30), FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        header.AddThemeStyleboxOverride("normal", Surfaces.Fill(Colors.Transparent, 0));
        header.AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Hover.Alpha(.5f), 0));
        header.AddThemeStyleboxOverride("pressed", Surfaces.Fill(Colors.Transparent, 0));
        header.AddThemeStyleboxOverride("focus", Surfaces.Fill(Colors.Transparent, 0));
        AddChild(header);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        header.AddChild(row);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 12;
        row.OffsetRight = actionIcon == null ? -12 : -32;

        _chevron = new IconView(open ? Icons.ChevronDown : Icons.ChevronRight, 12, Tokens.Text3);
        row.AddChild(_chevron);
        var titleLabel = Type.Build(title, Typography.Semi, Type.Micro, Tokens.Text3);
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(titleLabel);

        if (actionIcon != null)
        {
            var action = new IconButton(actionIcon, 12, "");
            action.CustomMinimumSize = new Vector2(20, 20);
            action.Pressed += () => onAction?.Invoke();
            header.AddChild(action);
            header.Resized += () => action.Position = new Vector2(header.Size.X - 26, 5);
        }

        header.Pressed += () =>
        {
            _open = !_open;
            Apply();
        };

        _wrapper = new MarginContainer();
        _wrapper.AddThemeConstantOverride("margin_left", 14);
        _wrapper.AddThemeConstantOverride("margin_right", 14);
        _wrapper.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(_wrapper);

        Body = new VBoxContainer();
        Body.AddThemeConstantOverride("separation", 6);
        _wrapper.AddChild(Body);
        Apply();
    }

    private void Apply()
    {
        _wrapper.Visible = _open;
        _chevron.Set(_open ? Icons.ChevronDown : Icons.ChevronRight);
    }
}

/// <summary>Two-column field: muted label, right-aligned value.</summary>
public partial class FieldRow : HBoxContainer
{
    private readonly Label _value;

    public FieldRow(string label, string value, bool mono = false, int labelWidth = 70)
    {
        AddThemeConstantOverride("separation", 8);
        CustomMinimumSize = new Vector2(0, 20);

        var name = Type.Build(label, Typography.Regular, Type.Caption, Tokens.Text3);
        name.CustomMinimumSize = new Vector2(labelWidth, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        AddChild(name);

        _value = Type.Build(value, mono ? Typography.Mono : Typography.Regular, mono ? Type.Micro : Type.Caption,
            Tokens.Text);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        _value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _value.VerticalAlignment = VerticalAlignment.Center;
        _value.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        AddChild(_value);
    }

    public void SetValue(string value)
    {
        _value.Text = value;
        TooltipText = value;
    }
}

/// <summary>Colour-coded count summary used above a list of clips.</summary>
public partial class ClipList : VBoxContainer
{
    public event Action<string>? ClipSelected;
    public ClipList()
    {
        AddThemeConstantOverride("separation", 1);
    }

    public void SetClips(IEnumerable<(string Name, double? Seconds)> clips, string? current = null)
    {
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        int shown = 0, total = 0;
        foreach (var (name, seconds) in clips)
        {
            total++;
            if (shown >= 6) continue;
            shown++;

            bool active = name == current;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.CustomMinimumSize = new Vector2(0, 20);
            row.AddChild(new StatusDot(8) { Modulate = active ? Tokens.Accent : Tokens.Text3 });
            var label = Type.Build(name, Typography.Mono, Type.Micro, active ? Tokens.Accent : Tokens.Text2);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(label);
            var play = new IconButton(Icons.Play, 12, "播放片段 " + name);
            play.Pressed += () => ClipSelected?.Invoke(name);
            row.AddChild(play);
            if (seconds is { } length)
            {
                var time = Type.MonoLabel($"{length:0.##}s", Tokens.Text3);
                time.VerticalAlignment = VerticalAlignment.Center;
                row.AddChild(time);
            }
            AddChild(row);
        }

        if (total == 0)
        {
            AddChild(Type.CaptionLabel("此资产没有动画片段", Tokens.Text3));
        }
        else if (total > shown)
        {
            AddChild(Type.MicroLabel($"另有 {total - shown} 个片段", Tokens.Text3));
        }
    }
}
