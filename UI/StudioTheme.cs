using Godot;

namespace Animanager.UI;

public static class StudioTheme
{
    public static readonly Color Background=new("20242a"),Surface=new("272c33"),Border=new("3d4652"),Text=new("e8ecf1"),Muted=new("aab4c0"),Accent=new("8cc8b8");
    public static StyleBoxFlat Box(Color color,int padding=8,int radius=4,Color? border=null)=>new()
    {
        BgColor=color,ContentMarginLeft=padding,ContentMarginRight=padding,ContentMarginTop=padding,ContentMarginBottom=padding,
        CornerRadiusTopLeft=radius,CornerRadiusTopRight=radius,CornerRadiusBottomLeft=radius,CornerRadiusBottomRight=radius,
        BorderWidthLeft=border.HasValue?1:0,BorderWidthRight=border.HasValue?1:0,BorderWidthTop=border.HasValue?1:0,BorderWidthBottom=border.HasValue?1:0,BorderColor=border??Border
    };
    public static Theme Create(bool comfortable=false)
    {
        var theme=new Theme{DefaultFontSize=14,DefaultFont=new SystemFont{FontNames=["Microsoft YaHei UI","Segoe UI","Noto Sans CJK SC","Noto Sans","sans-serif"],MultichannelSignedDistanceField=true}};
        var pad=comfortable?12:7;
        theme.SetColor("font_color","Label",Text);
        foreach(var type in new[]{"Button","OptionButton","MenuButton"})
        {
            theme.SetStylebox("normal",type,Box(new("303741"),pad,4,Border));
            theme.SetStylebox("hover",type,Box(new("3a4652"),pad,4,Muted));
            theme.SetStylebox("pressed",type,Box(new("36564e"),pad,4,Accent));
            theme.SetStylebox("disabled",type,Box(Surface,pad,4,Border));
            theme.SetStylebox("focus",type,Box(Colors.Transparent,0,4,Accent));
            theme.SetColor("font_color",type,Text);theme.SetColor("font_hover_color",type,Text);theme.SetColor("font_pressed_color",type,Text);theme.SetColor("font_disabled_color",type,new("78828e"));
        }
        theme.SetTypeVariation("PrimaryButton","Button");theme.SetStylebox("normal","PrimaryButton",Box(Accent,pad));theme.SetStylebox("hover","PrimaryButton",Box(new("a4d9cb"),pad));
        theme.SetColor("font_color","PrimaryButton",Background);theme.SetColor("font_hover_color","PrimaryButton",Background);theme.SetColor("font_pressed_color","PrimaryButton",Text);
        theme.SetTypeVariation("DockTab","Button");theme.SetStylebox("normal","DockTab",Box(Surface,6,2));theme.SetStylebox("pressed","DockTab",Box(new("36564e"),6,2,Accent));
        theme.SetStylebox("normal","LineEdit",Box(new("1d2228"),pad,4,Border));theme.SetStylebox("focus","LineEdit",Box(Colors.Transparent,0,4,Accent));theme.SetStylebox("read_only","LineEdit",Box(Surface,pad,4,Border));
        theme.SetColor("font_color","LineEdit",Text);theme.SetColor("font_placeholder_color","LineEdit",Muted);theme.SetColor("caret_color","LineEdit",Accent);theme.SetColor("selection_color","LineEdit",new("395a52"));
        theme.SetStylebox("panel","ItemList",Box(Background,6));theme.SetStylebox("selected","ItemList",Box(new("35574d"),4,3));theme.SetStylebox("selected_focus","ItemList",Box(new("35574d"),4,3,Accent));theme.SetStylebox("cursor","ItemList",Box(Colors.Transparent,0,3,Accent));
        theme.SetColor("font_color","ItemList",Text);theme.SetColor("font_selected_color","ItemList",Text);theme.SetConstant("v_separation","ItemList",comfortable?14:8);
        theme.SetStylebox("panel","Tree",Box(Background,4));theme.SetStylebox("selected","Tree",Box(new("35574d"),2,2));theme.SetStylebox("selected_focus","Tree",Box(new("35574d"),2,2,Accent));
        theme.SetStylebox("focus","Tree",Box(Colors.Transparent,0,3,Accent));theme.SetColor("font_color","Tree",Text);theme.SetColor("font_selected_color","Tree",Text);theme.SetColor("guide_color","Tree",Border);
        theme.SetConstant("v_separation","Tree",comfortable?8:5);theme.SetConstant("item_margin","Tree",14);
        theme.SetStylebox("panel","PopupMenu",Box(new("303741"),8,4,Border));theme.SetStylebox("hover","PopupMenu",Box(new("36564e"),6));theme.SetColor("font_color","PopupMenu",Text);
        theme.SetStylebox("panel","PanelContainer",Box(Surface,8));theme.SetStylebox("panel","AcceptDialog",Box(Surface,12));theme.SetStylebox("panel","Window",Box(Surface,12));
        theme.SetColor("default_color","RichTextLabel",Text);theme.SetStylebox("normal","TextEdit",Box(Background,8,4,Border));theme.SetColor("font_color","TextEdit",Text);
        theme.SetConstant("separation","VBoxContainer",8);theme.SetConstant("separation","HBoxContainer",6);theme.SetConstant("separation","HFlowContainer",6);
        foreach(var type in new[]{"HSplitContainer","VSplitContainer"}){theme.SetConstant("separation",type,8);theme.SetConstant("minimum_grab_thickness",type,8);theme.SetStylebox("split_bar_background",type,Box(Background,0,0));}
        return theme;
    }
}
