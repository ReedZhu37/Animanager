using Godot;

namespace Animanager.UI.Controls;

public static class AppControls
{
    public static Label Label(string text,bool muted=false,int size=14)
    {
        var label=new Label{Text=text,MouseFilter=Control.MouseFilterEnum.Ignore};
        label.AddThemeFontSizeOverride("font_size",size);if(muted)label.AddThemeColorOverride("font_color",Animanager.UI.Shell.Tokens.Text3);return label;
    }
    public static Button Button(string text,Action action,bool primary=false)
    {
        var button=new Button{Text=text,FocusMode=Control.FocusModeEnum.All,MouseDefaultCursorShape=Control.CursorShape.PointingHand,ThemeTypeVariation=primary?"PrimaryButton":"Button",CustomMinimumSize=new(0,32)};
        button.Pressed+=action;return button;
    }
    public static LineEdit Input(string placeholder)=>new(){PlaceholderText=placeholder,ClearButtonEnabled=true,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,CustomMinimumSize=new(0,32)};
    public static VBoxContainer Column(Node parent){var box=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,SizeFlagsVertical=Control.SizeFlags.ExpandFill};parent.AddChild(box);return box;}
    public static HBoxContainer Row(Node parent){var box=new HBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};parent.AddChild(box);return box;}
    public static Label Wrapped(string text,bool muted=true)
    {
        var label=Label(text,muted,13);label.AutowrapMode=TextServer.AutowrapMode.WordSmart;label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;return label;
    }
}
