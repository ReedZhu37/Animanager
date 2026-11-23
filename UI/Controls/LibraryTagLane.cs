using Animanager.Core.Storage;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.UI.Controls;

internal partial class LibraryTagLane : PanelContainer
{
    private readonly StyleBoxFlat _surface = Surfaces.Fill(Tokens.Panel, Tokens.Radius.Control, Tokens.Space.Md, Tokens.Space.Md, Tokens.Line);
    private readonly StyleBoxFlat _dropSurface = Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Control, Tokens.Space.Md, Tokens.Space.Md, Tokens.Accent);

    internal LibraryTagLane? OtherLane { get; set; }
    internal event Action<LibraryTag>? TagDropped;

    public override void _Ready() => AddThemeStyleboxOverride("panel", _surface);

    public override bool _CanDropData(Vector2 atPosition, Variant data) => data.VariantType == Variant.Type.Object &&
        data.AsGodotObject() is LibraryTagChip chip && chip.Lane == OtherLane;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!_CanDropData(atPosition, data) || data.AsGodotObject() is not LibraryTagChip chip) return;
        TagDropped?.Invoke(chip.Tag);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragBegin && IsVisibleInTree() && _CanDropData(Vector2.Zero, GetViewport().GuiGetDragData()))
            AddThemeStyleboxOverride("panel", _dropSurface);
        else if (what == NotificationDragEnd)
            AddThemeStyleboxOverride("panel", _surface);
    }
}

internal partial class LibraryTagChip : Button
{
    internal LibraryTag Tag { get; }
    internal LibraryTagLane Lane { get; }

    internal LibraryTagChip(LibraryTag tag, LibraryTagLane lane, bool assigned)
    {
        Tag = tag;
        Lane = lane;
        Text = Typography.Fit(Typography.Regular, tag.Name, Shell.Type.Caption, 128);
        ThemeTypeVariation = "Chip";
        MouseDefaultCursorShape = CursorShape.Drag;
        TooltipText = $"{tag.Name}\n{(assigned ? "拖到其他库可复用；拖回左栏或按回车移除" : "拖到库上添加；按回车添加到当前库")}";
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var preview = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        preview.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.AccentWash, Tokens.Radius.Pill, 10, 3, Tokens.Accent));
        preview.AddChild(Shell.Type.CaptionLabel(Text, Tokens.Text));
        SetDragPreview(preview);
        return Variant.From(this);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => Lane._CanDropData(atPosition, data);
    public override void _DropData(Vector2 atPosition, Variant data) => Lane._DropData(atPosition, data);

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey || !@event.IsActionPressed("ui_accept")) return;
        Lane.OtherLane?._DropData(Vector2.Zero, Variant.From(this));
        AcceptEvent();
    }
}
