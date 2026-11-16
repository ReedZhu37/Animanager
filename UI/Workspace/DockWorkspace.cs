using Godot;

namespace Animanager.UI.Workspace;

/// <summary>Runtime docking host. Panel instances survive every layout change.</summary>
public partial class DockWorkspace : Control
{
    public WorkspaceLayout Layout { get; private set; }=new();
    public bool Compact { get; private set; }
    public string? MaximizedPanel { get; private set; }
    public bool DragActive { get; private set; }
    public event Action? LayoutChanged;
    public event Action<string>? Notice;
    private readonly Dictionary<string,(string Title,Control Content)> _panels=[];
    private readonly List<DockGroupView> _groups=[];
    private Control _parking=null!,_surface=null!;
    private DockDropOverlay _overlay=null!;
    private bool _queued,_built;
    private double _resizeDelay;
    public IReadOnlyList<DockGroupView> Groups=>_groups;
    public override void _Ready()
    {
        _parking=new Control{Visible=false,Name="PanelParking"};AddChild(_parking);
        _surface=new Control{Name="DockSurface"};AddChild(_surface);_surface.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlay=new DockDropOverlay{Host=this,Visible=false};AddChild(_overlay);_overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized+=()=>_resizeDelay=.12;
        GetWindow().FocusExited+=CancelDrag;
    }
    public void Register(string id,string title,Control content)
    {
        content.Name="Panel_"+id;content.SizeFlagsHorizontal=SizeFlags.ExpandFill;content.SizeFlagsVertical=SizeFlags.ExpandFill;
        _panels[id]=(title,content);_parking.AddChild(content);
    }
    public void Initialize(WorkspaceLayout layout){Layout=layout;Layout.Normalize();Rebuild();}
    public string Title(string id)=>_panels.TryGetValue(id,out var p)?p.Title:id;
    public Control Panel(string id)=>_panels[id].Content;
    public bool IsPanelVisible(string id)=>_panels[id].Content.IsVisibleInTree();
    public void UsePreset(string preset){Layout.Preset(preset);MaximizedPanel=null;Changed();}
    public void Restore(WorkspaceLayout layout){Layout=layout.Clone();Layout.Normalize();MaximizedPanel=null;Changed();}
    public void ShowPanel(string panel){Layout.Show(panel);MaximizedPanel=null;Changed();}
    public void HidePanel(string panel)
    {
        if(!Layout.Hide(panel)){Notice?.Invoke("至少保留一个面板；可通过工作区菜单恢复布局。");return;}
        if(MaximizedPanel==panel)MaximizedPanel=null;Changed();
    }
    public void Maximize(string panel){MaximizedPanel=MaximizedPanel==panel?null:panel;Layout.Select(panel);QueueRebuild();}
    public void Select(string panel){Layout.Select(panel);Changed();}
    public void MovePanel(string panel,string target,DockEdge edge,int index=-1)
    {
        if(Compact||MaximizedPanel!=null)
        {
            if(edge!=DockEdge.Center){Notice?.Invoke("当前空间使用标签模式；放大窗口后可分区停靠。");return;}
            var order=Layout.CompactOrder;order.Remove(panel);order.Insert(index<0?order.Count:Math.Clamp(index,0,order.Count),panel);Layout.Select(panel);Changed();return;
        }
        if(Layout.Move(panel,target,edge,index))Changed();
    }
    public void MoveBeside(string panel,DockEdge edge)
    {
        var target=Layout.GroupFor("assets")??Layout.Root.Groups().First();
        if(target.Panels.Count==1&&target.Panels[0]==panel)target=Layout.Root.Groups().FirstOrDefault(g=>g!=target)??target;
        MovePanel(panel,target.Id,edge);
    }
    public void CycleFocus()
    {
        var focused=GetViewport().GuiGetFocusOwner();var index=_groups.FindIndex(g=>focused!=null&&g.IsAncestorOf(focused));
        if(_groups.Count>0)_groups[(index+1)%_groups.Count].FocusActive();
    }
    public void BeginDrag(string panel)
    {
        DragActive=true;_overlay.Visible=true;_overlay.Source=panel;_overlay.Clear();
    }
    public void CancelDrag(){if(DragActive)GetViewport().GuiCancelDrag();EndDrag();}
    private void EndDrag(){DragActive=false;if(_overlay!=null){_overlay.Clear();_overlay.Hide();}}
    public override void _Notification(int what){if(what==NotificationDragEnd)EndDrag();}
    public override void _Input(InputEvent ev)
    {
        if(DragActive&&ev is InputEventKey{Pressed:true,Keycode:Key.Escape}){CancelDrag();GetViewport().SetInputAsHandled();}
    }
    public override void _Process(double delta)
    {
        if(!_built||_resizeDelay<=0)return;
        _resizeDelay-=delta;if(_resizeDelay>0)return;
        if(DragActive||DisplayScaleController.IsComposing){_resizeDelay=.12;return;}CheckMode();
    }
    public static Vector2 RequiredSize(DockNode node)
    {
        if(node.IsGroup)return new Vector2(node.Panels.Contains("preview")?260:190,node.Panels.Contains("preview")?230:150);
        var a=RequiredSize(node.First!);var b=RequiredSize(node.Second!);
        return node.Axis=="h"?new(a.X+b.X+8,Math.Max(a.Y,b.Y)):new(Math.Max(a.X,b.X),a.Y+b.Y+8);
    }
    private bool NeedsCompact()
    {
        var needed=RequiredSize(Layout.Root);float margin=Compact?24:0;
        return Size.X<Math.Max(1000,needed.X)+margin||Size.Y<Math.Max(360,needed.Y)+margin;
    }
    private void CheckMode(){if(NeedsCompact()!=Compact)QueueRebuild();}
    private void Changed(){LayoutChanged?.Invoke();QueueRebuild();}
    private void QueueRebuild(){if(_queued)return;_queued=true;Callable.From(Rebuild).CallDeferred();}
    private void Rebuild()
    {
        _queued=false;if(!IsInsideTree())return;
        var focus=GetViewport().GuiGetFocusOwner();
        foreach(var panel in _panels.Values){if(panel.Content.GetParent()!=_parking)panel.Content.Reparent(_parking,false);panel.Content.Hide();}
        foreach(var child in _surface.GetChildren()){_surface.RemoveChild(child);child.QueueFree();}
        _groups.Clear();Compact=NeedsCompact();
        DockNode render=Layout.Root;
        if(MaximizedPanel!=null)render=DockNode.Group(MaximizedPanel);
        else if(Compact)
        {
            render=DockNode.Group(Layout.CompactOrder.Where(Layout.Root.AllPanels().Contains).ToArray());
            render.Active=render.Panels.Contains(Layout.CompactActive)?Layout.CompactActive:render.Panels.First();
        }
        var control=Build(render);_surface.AddChild(control);control.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);_built=true;
        Callable.From(()=>{if(IsInstanceValid(focus)&&focus!.IsVisibleInTree())focus.GrabFocus();}).CallDeferred();
    }
    private Control Build(DockNode node)
    {
        if(node.IsGroup)
        {
            var view=new DockGroupView{Host=this,Data=node};_groups.Add(view);return view;
        }
        var split=new RatioSplit{Vertical=node.Axis=="v",Data=node,SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};
        split.AddThemeConstantOverride("separation",8);split.AddThemeConstantOverride("minimum_grab_thickness",8);
        split.AddChild(Build(node.First!));split.AddChild(Build(node.Second!));split.Committed+=()=>LayoutChanged?.Invoke();return split;
    }
    internal Control Mount(string id,Node parent)
    {
        var content=_panels[id].Content;content.Reparent(parent,false);content.Show();return content;
    }
}

public partial class RatioSplit : SplitContainer
{
    public DockNode Data { get; set; }=null!;
    public event Action? Committed;
    private bool _dragging,_pending;
    public override void _Ready()
    {
        foreach(var c in GetChildren().OfType<Control>()){c.SizeFlagsHorizontal=SizeFlags.ExpandFill;c.SizeFlagsVertical=SizeFlags.ExpandFill;}
        Resized+=Schedule;SortChildren+=Schedule;DragStarted+=()=>_dragging=true;
        DragEnded+=()=>{_dragging=false;var child=GetChild<Control>(0);var available=(Vertical?Size.Y:Size.X)-8;
            if(available>0)Data.Ratio=Math.Clamp((Vertical?child.Size.Y:child.Size.X)/available,.05f,.95f);Committed?.Invoke();};
        Schedule();
    }
    private void Schedule(){if(_pending||_dragging)return;_pending=true;Callable.From(ApplyRatio).CallDeferred();}
    private void ApplyRatio()
    {
        _pending=false;if(!IsInsideTree()||_dragging||GetChildCount()<2)return;
        var child=GetChild<Control>(0);float available=(Vertical?Size.Y:Size.X)-8;if(available<=0)return;
        var a=child.GetCombinedMinimumSize();var b=GetChild<Control>(1).GetCombinedMinimumSize();
        var desired=Math.Clamp(available*Data.Ratio,Vertical?a.Y:a.X,Math.Max(Vertical?a.Y:a.X,available-(Vertical?b.Y:b.X)));
        var difference=(int)Math.Round(desired-(Vertical?child.Size.Y:child.Size.X));
        if(Math.Abs(difference)<=1)return;
        var old=SplitOffsets.Length>0?SplitOffsets[0]:0;SplitOffsets=[old+difference];ClampSplitOffset();
    }
}

public partial class DockGroupView : Control
{
    public DockWorkspace Host { get; set; }=null!;
    public DockNode Data { get; set; }=null!;
    public List<DockTab> Tabs { get; }=[];
    private HBoxContainer _header=null!;
    private ScrollContainer _body=null!;
    public override Vector2 _GetMinimumSize()=>new(180,140);
    public override void _Ready()
    {
        ClipContents=true;MouseFilter=MouseFilterEnum.Pass;
        _header=new HBoxContainer();_header.AddThemeConstantOverride("separation",2);AddChild(_header);
        var tabScroll=new ScrollContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Auto,VerticalScrollMode=ScrollContainer.ScrollMode.Disabled};_header.AddChild(tabScroll);
        var row=new HBoxContainer();row.AddThemeConstantOverride("separation",1);tabScroll.AddChild(row);
        foreach(var id in Data.Panels)
        {
            var tab=new DockTab{Host=Host,PanelId=id,Text=Host.Title(id),ToggleMode=true,ButtonPressed=id==Data.Active,TooltipText="拖动标签调整布局 · "+Host.Title(id),CustomMinimumSize=new(0,30),ThemeTypeVariation="DockTab"};
            tab.Pressed+=()=>Host.Select(id);row.AddChild(tab);Tabs.Add(tab);
        }
        var menu=new MenuButton{Text="⋮",CustomMinimumSize=new(28,30),TooltipText="面板操作",FocusMode=FocusModeEnum.All};_header.AddChild(menu);
        var popup=menu.GetPopup();popup.AddItem("最大化 / 恢复",0);popup.AddItem("隐藏面板",1);popup.AddSeparator();
        popup.AddItem("移动到左侧",2);popup.AddItem("移动到右侧",3);popup.AddItem("移动到上方",4);popup.AddItem("移动到下方",5);popup.AddItem("合并到资产标签组",6);
        popup.IdPressed+=id=>{var panel=Data.Active;switch(id){case 0:Host.Maximize(panel);break;case 1:Host.HidePanel(panel);break;case 2:Host.MoveBeside(panel,DockEdge.Left);break;case 3:Host.MoveBeside(panel,DockEdge.Right);break;case 4:Host.MoveBeside(panel,DockEdge.Top);break;case 5:Host.MoveBeside(panel,DockEdge.Bottom);break;case 6:Host.MoveBeside(panel,DockEdge.Center);break;}};
        menu.AboutToPopup+=()=>{foreach(var id in new[]{2,3,4,5,6})popup.SetItemDisabled(popup.GetItemIndex(id),Host.Compact||Host.MaximizedPanel!=null);};
        _body=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,VerticalScrollMode=ScrollContainer.ScrollMode.Auto};AddChild(_body);
        Host.Mount(Data.Active,_body);Resized+=Arrange;Arrange();
    }
    private void Arrange(){_header.Position=new(4,2);_header.Size=new(Math.Max(0,Size.X-8),36);_body.Position=new(8,42);_body.Size=new(Math.Max(0,Size.X-16),Math.Max(0,Size.Y-50));QueueRedraw();}
    public void FocusActive()=>Tabs.FirstOrDefault(t=>t.PanelId==Data.Active)?.GrabFocus();
    public override void _Draw(){DrawStyleBox(StudioTheme.Box(StudioTheme.Surface,0,4,StudioTheme.Border),new Rect2(Vector2.Zero,Size));DrawLine(new(0,38),new(Size.X,38),StudioTheme.Border);}
}

public partial class DockTab : Button
{
    public DockWorkspace Host { get; set; }=null!;
    public string PanelId { get; set; }="";
    public override Variant _GetDragData(Vector2 atPosition)
    {
        var label=new Label{Text="  "+Text+"  ",Modulate=StudioTheme.Accent};SetDragPreview(label);Host.BeginDrag(PanelId);return "animanager-panel:"+PanelId;
    }
}

public partial class DockDropOverlay : Control
{
    public DockWorkspace Host { get; set; }=null!;
    public string Source { get; set; }="";
    private DockGroupView? _target;
    private DockEdge _edge;
    private int _index=-1;
    private Rect2 _highlight;
    public void Clear(){_target=null;QueueRedraw();}
    public override bool _CanDropData(Vector2 atPosition,Variant data)
    {
        if(data.VariantType!=Variant.Type.String||data.AsString()!="animanager-panel:"+Source)return false;
        var global=GetGlobalTransform()*atPosition;
        _target=Host.Groups.FirstOrDefault(g=>g.GetGlobalRect().HasPoint(global));if(_target==null){QueueRedraw();return false;}
        var rect=_target.GetGlobalRect();var local=global-rect.Position;_edge=DockEdge.Center;_index=-1;
        if(local.Y<40)
        {
            _index=_target.Tabs.FindIndex(t=>global.X<t.GetGlobalRect().GetCenter().X);
            if(_index<0)_index=_target.Tabs.Count;
        }
        else if(!Host.Compact&&Host.MaximizedPanel==null)
        {
            if(local.X<rect.Size.X*.22f)_edge=DockEdge.Left;else if(local.X>rect.Size.X*.78f)_edge=DockEdge.Right;
            else if(local.Y<rect.Size.Y*.25f)_edge=DockEdge.Top;else if(local.Y>rect.Size.Y*.75f)_edge=DockEdge.Bottom;
        }
        rect.Position-=GlobalPosition;
        _highlight=_edge switch
        {
            DockEdge.Left=>new(rect.Position,new(rect.Size.X*.5f,rect.Size.Y)),DockEdge.Right=>new(rect.Position+new Vector2(rect.Size.X*.5f,0),new(rect.Size.X*.5f,rect.Size.Y)),
            DockEdge.Top=>new(rect.Position,new(rect.Size.X,rect.Size.Y*.5f)),DockEdge.Bottom=>new(rect.Position+new Vector2(0,rect.Size.Y*.5f),new(rect.Size.X,rect.Size.Y*.5f)),_=>rect
        };
        QueueRedraw();return true;
    }
    public override void _DropData(Vector2 atPosition,Variant data)
    {
        if(!_CanDropData(atPosition,data)||_target==null)return;
        Host.MovePanel(Source,_target.Data.Id,_edge,_index);Clear();
    }
    public override void _Draw()
    {
        if(_target==null)return;var color=StudioTheme.Accent;color.A=.2f;DrawRect(_highlight.Grow(-3),color);DrawRect(_highlight.Grow(-3),StudioTheme.Accent,false,2);
        var title=_edge switch{DockEdge.Left=>"停靠左侧",DockEdge.Right=>"停靠右侧",DockEdge.Top=>"停靠上方",DockEdge.Bottom=>"停靠下方",_=>"加入标签组"};
        DrawString(GetThemeDefaultFont(),_highlight.Position+new Vector2(14,62),title,HorizontalAlignment.Left,-1,14,StudioTheme.Text);
    }
}
