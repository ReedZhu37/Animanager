using Godot;
using System.Runtime.InteropServices;

namespace Animanager.UI.Workspace;

/// <summary>Per-window scaling. Windows DPI is read from the actual HWND, never inferred from resolution.</summary>
public partial class DisplayScaleController : Node
{
    public static bool IsComposing => Window.GetFocusedWindow()?.GuiGetFocusOwner() switch
    {
        LineEdit line => line.HasImeText(),
        TextEdit text => text.HasImeText(),
        _ => false
    };
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    public double RequestedScale { get; private set; }
    public float EffectiveScale { get; private set; }=1;
    public string Source { get; private set; }="100%";
    public event Action? Changed;
    private double _elapsed;
    private Window _window=null!;
    public override void _Ready(){_window=GetWindow();_window.DpiChanged+=Refresh;Refresh();}
    public void SetScale(double value)
    {
        RequestedScale=value==0?0:Math.Clamp(value,1,2);Refresh();Changed?.Invoke();
    }
    private float SystemScale()
    {
        if(DisplayServer.GetName()=="headless"){Source="Headless";return 1;}
        if(OperatingSystem.IsWindows())
        {
            try
            {
                var handle=(nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle,_window.GetWindowId());
                var dpi=GetDpiForWindow(handle);if(dpi>0){Source=$"Windows {dpi} DPI";return dpi/96f;}
            }
            catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException){}
            Source="系统 DPI 不可用，回退 100%";return 1;
        }
        Source="系统显示比例";return DisplayServer.ScreenGetScale(_window.CurrentScreen);
    }
    public void Refresh()
    {
        var scale=RequestedScale>0?(float)RequestedScale:SystemScale();if(RequestedScale>0)Source="手动";
        scale=Math.Clamp(scale,1,4);
        if(Math.Abs(EffectiveScale-scale)<.001&&_window.ContentScaleFactor==scale)return;
        EffectiveScale=scale;_window.ContentScaleMode=Window.ContentScaleModeEnum.Disabled;_window.ContentScaleFactor=scale;
        ApplyChildren(_window,scale);ClampWindow(_window,false);Changed?.Invoke();
    }
    public static void ApplyChildren(Node root,float scale)
    {
        foreach(var child in root.GetChildren())
        {
            if(child is Window window){window.ContentScaleMode=Window.ContentScaleModeEnum.Disabled;window.ContentScaleFactor=scale;}
            ApplyChildren(child,scale);
        }
    }
    public static Rect2I ClampRect(Rect2I window,Rect2I usable)
    {
        var size=new Vector2I(Math.Clamp(window.Size.X,1,Math.Max(1,usable.Size.X)),Math.Clamp(window.Size.Y,1,Math.Max(1,usable.Size.Y)));
        return new(new Vector2I(Math.Clamp(window.Position.X,usable.Position.X,usable.End.X-size.X),Math.Clamp(window.Position.Y,usable.Position.Y,usable.End.Y-size.Y)),size);
    }
    public static void ClampWindow(Window window,bool center)
    {
        if(DisplayServer.GetName()=="headless")return;
        var screen=DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
        // Reserve room for native decorations; the returned screen rectangle already excludes the taskbar.
        var usable=new Rect2I(screen.Position+new Vector2I(8,36),new Vector2I(Math.Max(1,screen.Size.X-16),Math.Max(1,screen.Size.Y-48)));
        // The minimum is in logical UI units, then bounded by the actual monitor.
        // A small display still wins, so no window is forced beyond its usable area.
        var scale=window.ContentScaleFactor;
        window.MinSize=new Vector2I(Math.Min(usable.Size.X,Math.Min(1024,(int)Math.Ceiling(720*scale))),Math.Min(usable.Size.Y,Math.Min(700,(int)Math.Ceiling(400*scale))));
        if(window.Mode!=Window.ModeEnum.Windowed)return;
        var rect=ClampRect(new(window.Position,window.Size),usable);
        if(center)rect.Position=usable.Position+(usable.Size-rect.Size)/2;
        window.Size=rect.Size;window.Position=rect.Position;
    }
    public override void _Process(double delta){_elapsed+=delta;if(_elapsed<.75)return;_elapsed=0;Refresh();}
}
