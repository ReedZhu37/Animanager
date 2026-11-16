using Godot;
using Animanager.Core.Preview;
using Animanager.UI.Components;
using Animanager.UI.Workspace;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI.Panels;

/// <summary>A view over the same inspection session used by Console. Reparenting preserves UI state.</summary>
public partial class BoneTreePanel : VBoxContainer
{
    public Preview3DViewport Preview { get; set; }=null!;
    private BoneInspection Bones=>Preview.Bones;
    private LineEdit _search=null!;
    private Tree _tree=null!;
    private Label _count=null!,_empty=null!,_selected=null!,_pose=null!,_status=null!;
    private CheckButton _trail=null!,_skeleton=null!,_xray=null!;
    private OptionButton _reference=null!,_rate=null!;
    private Button _focus=null!,_locate=null!,_clear=null!;
    private readonly Dictionary<BoneId,TreeItem> _items=[];
    private readonly Dictionary<string,bool> _collapsed=[];
    private readonly List<BoneId?> _references=[];
    private long _revision=-1;
    private BoneId[] _lastSelection=[];
    private BoneId? _gesturePrimary;
    private string _query="",_error="";
    private bool _built,_syncing,_queued,_selectionQueued,_replaceSelection;
    private double _elapsed;
    // Windows IME has no focused native window during initial panel construction.
    private bool SearchComposing=>GetWindow().HasFocus()&&_search.HasFocus()&&DisplayScaleController.IsComposing;

    public override void _Ready()
    {
        _search=Input("搜索骨骼名称…");_search.Name="BoneSearch";AddChild(_search);
        _search.TextChanged+=_=>QueueSync();
        _count=Label("",true,12);AddChild(_count);
        AddChild(Wrapped("Ctrl 点击增减选择 · Shift 点击连续选择"));
        _tree=new Tree{Name="BoneTree",HideRoot=true,SelectMode=Tree.SelectModeEnum.Multi,AllowReselect=true,
            SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill,CustomMinimumSize=new(0,170)};
        AddChild(_tree);_tree.MultiSelected+=(_,_,_)=>QueueSelection();_tree.ItemActivated+=()=>{CommitSelection();Act(Bones.Focus);};
        _tree.GuiInput+=ev=>
        {
            // Read modifiers on the whole input gesture; commit after all native per-item signals.
            if(ev is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:true} mouse)
            {
                _replaceSelection=!mouse.CtrlPressed&&!mouse.MetaPressed&&!mouse.ShiftPressed;
                var clicked=_tree.GetItemAtPosition(mouse.Position);var pair=_items.FirstOrDefault(p=>p.Value==clicked);
                _gesturePrimary=pair.Value!=null?pair.Key:null;
            }
            if(ev is InputEventKey {Pressed:true} key){_replaceSelection=!key.CtrlPressed&&!key.MetaPressed&&!key.ShiftPressed;_gesturePrimary=null;}
        };
        _tree.ItemCollapsed+=item=>{if(!_syncing&&_query.Length==0)_collapsed[item.GetMetadata(0).AsString()]=item.Collapsed;};
        _empty=Wrapped("");_empty.Name="BoneEmpty";AddChild(_empty);
        var actions=new HFlowContainer();AddChild(actions);
        _focus=Button("聚焦",()=>Act(Bones.Focus));_focus.Name="BoneFocus";actions.AddChild(_focus);
        _locate=Button("定位选中",Locate);_locate.Name="BoneLocate";actions.AddChild(_locate);
        _clear=Button("清除",()=>Act(Bones.ClearSelection));_clear.Name="BoneClear";actions.AddChild(_clear);
        _selected=Wrapped("尚未选中骨骼",false);_selected.Name="BoneSelection";AddChild(_selected);
        _pose=Wrapped("");_pose.Name="BonePose";AddChild(_pose);
        var display=new HFlowContainer();AddChild(display);
        _skeleton=Toggle(display,"显示骨架","BoneShow",Bones.ShowSkeleton);
        _xray=Toggle(display,"穿透显示","BoneXray",Bones.SetXray);
        AddChild(new HSeparator());
        _trail=Toggle(this,"显示选中骨骼轨迹","BoneTrail",Bones.ShowTrail);
        _trail.TooltipText="控制所有选中骨骼的轨迹；固定轨迹和 Root Motion 独立显示。";
        AddChild(Label("参考空间",true,12));
        _reference=new OptionButton{Name="BoneReference",FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(0,32)};AddChild(_reference);
        _reference.TooltipText="世界空间保留角色整体移动；相对空间抵消指定骨骼的位移、旋转与缩放。";
        _reference.ItemSelected+=i=>{if(!_syncing)Act(()=>Bones.SetReference(_references[(int)i] is {} bone?BoneInspection.IdText(bone):null));};
        var sampling=Row(this);sampling.AddChild(Label("采样率",true,12));
        _rate=new OptionButton{Name="BoneRate",FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(0,32)};
        _rate.AddItem("30 次 / 秒",30);_rate.AddItem("60 次 / 秒",60);sampling.AddChild(_rate);
        _rate.ItemSelected+=i=>{if(!_syncing)Act(()=>Bones.SetRate(_rate.GetItemId((int)i)));};
        _status=Wrapped("");_status.Name="BoneStatus";AddChild(_status);
        BuildTrajectoryControls();
        _built=true;Bones.Changed+=OnChanged;VisibilityChanged+=()=>{SetProcess(IsVisibleInTree());if(IsVisibleInTree())QueueSync();};
        SetProcess(IsVisibleInTree());Sync();
    }
    public override void _EnterTree(){if(_built){Bones.Changed+=OnChanged;QueueSync();}}
    public override void _ExitTree(){if(_built)Bones.Changed-=OnChanged;}
    private CheckButton Toggle(Node parent,string text,string name,Action<bool> action)
    {
        var button=new CheckButton{Name=name,Text=text,TooltipText=text,ClipText=true,SizeFlagsHorizontal=SizeFlags.ExpandFill,FocusMode=FocusModeEnum.All};parent.AddChild(button);
        button.Toggled+=value=>{if(!_syncing)Act(()=>action(value));};return button;
    }
    private void OnChanged(){_error="";QueueSync();}
    private void QueueSync()
    {
        if(_queued||!_built)return;_queued=true;
        Callable.From(()=>{_queued=false;if(IsInsideTree())Sync();}).CallDeferred();
    }
    private void Act(Action action)
    {
        try{_error="";action();}catch(Exception ex){_error=ex.Message;}
        QueueSync();
    }
    private void QueueSelection()
    {
        if(_syncing||_selectionQueued)return;_selectionQueued=true;
        long revision=Bones.ModelRevision;
        Callable.From(()=>
        {
            if(!IsInsideTree()||revision!=Bones.ModelRevision){_selectionQueued=false;return;}
            CommitSelection();
        }).CallDeferred();
    }
    private void CommitSelection()
    {
        if(!_selectionQueued||_syncing)return;_selectionQueued=false;
        var visible=_items.Where(pair=>pair.Value.IsSelected(0)).Select(pair=>pair.Key).ToHashSet();
        // Filtering alone preserves selection. Ctrl/Shift gestures also preserve filtered-out items.
        var next=_replaceSelection?new List<BoneId>():Bones.SelectedBones.Where(id=>!_items.ContainsKey(id)||visible.Contains(id)).ToList();
        foreach(var id in visible)if(!next.Contains(id))next.Add(id);
        var cursor=_tree.GetSelected();
        if(_gesturePrimary is {} clicked&&visible.Contains(clicked)){next.Remove(clicked);next.Add(clicked);}
        else if(cursor!=null&&cursor.IsSelected(0))
        {
            var primary=_items.FirstOrDefault(p=>p.Value==cursor);
            if(primary.Value!=null){next.Remove(primary.Key);next.Add(primary.Key);}
        }
        _replaceSelection=false;_gesturePrimary=null;Act(()=>Bones.SetSelection(next.Select(BoneInspection.IdText)));
        // The native tree already has the correct cells selected. Preserve its Shift-range anchor.
        _lastSelection=Bones.SelectedBones.ToArray();
    }
    private void Sync()
    {
        SetProcess(IsVisibleInTree());
        bool modelChanged=_revision!=Bones.ModelRevision;
        if(modelChanged)
        {
            _revision=Bones.ModelRevision;_collapsed.Clear();_search.Text="";_query="";_lastSelection=[];
            _gesturePrimary=null;_replaceSelection=false;
            _reference.Clear();_references.Clear();_reference.AddItem("世界空间");_references.Add(null);
            for(int s=0;s<Bones.Skeletons.Count;s++)
                for(int b=0;b<Bones.Skeletons[s].Node.GetBoneCount();b++)
                {
                    var id=new BoneId(s,b);_reference.AddItem($"相对 · {Bones.Name(id)} [{BoneInspection.IdText(id)}]");
                    _reference.SetItemTooltip(_references.Count,Bones.Skeletons[s].Path+" / "+Bones.Name(id));_references.Add(id);
                }
            RebuildRootChoices();
        }
        string query=SearchComposing?_query:_search.Text.Trim();
        bool rebuild=modelChanged||query!=_query;_query=query;
        _syncing=true;
        try
        {
            if(rebuild)BuildTree();
            bool selectionChanged=!Bones.SelectedBones.SequenceEqual(_lastSelection);
            if(rebuild||selectionChanged)
            {
                _tree.DeselectAll();
                foreach(var id in Bones.SelectedBones)
                {
                    if(!_items.TryGetValue(id,out var item))continue;
                    if(selectionChanged&&!_lastSelection.Contains(id))ExpandAncestors(item);
                    item.Select(0,id==Bones.Selected);
                    if(selectionChanged&&id==Bones.Selected)_tree.ScrollToItem(item);
                }
            }
            _lastSelection=Bones.SelectedBones.ToArray();
            _focus.Disabled=_locate.Disabled=_clear.Disabled=Bones.Selected==null;
            _skeleton.Disabled=_xray.Disabled=Bones.Skeletons.Count==0;
            _skeleton.SetPressedNoSignal(Bones.Overlay.ShowSkeleton);_xray.SetPressedNoSignal(Bones.Overlay.Xray);
            _trail.SetPressedNoSignal(Bones.TrailEnabled);
            _trail.Disabled=Bones.Selected==null||Preview.Duration<=0;
            _reference.Disabled=Bones.Skeletons.Count==0;_reference.Select(Math.Max(0,_references.IndexOf(Bones.Reference)));
            _rate.Disabled=Preview.Duration<=0;_rate.Select(Bones.SampleRate==60?1:0);
            _status.Text=_error.Length>0?"操作失败："+_error:StatusText();
            SyncTrajectoryControls();
            UpdateInfo();
        }
        finally{_syncing=false;}
    }
    private void BuildTree()
    {
        _tree.Clear();_items.Clear();var root=_tree.CreateItem();int matches=0,total=0;
        for(int s=0;s<Bones.Skeletons.Count;s++)
        {
            var entry=Bones.Skeletons[s];var skeleton=entry.Node;var included=new HashSet<int>();var direct=new HashSet<int>();total+=skeleton.GetBoneCount();
            for(int b=0;b<skeleton.GetBoneCount();b++)
                if(_query.Length==0||Bones.Name(new(s,b)).Contains(_query,StringComparison.OrdinalIgnoreCase))
                {direct.Add(b);matches++;for(int p=b;p>=0;p=skeleton.GetBoneParent(p))included.Add(p);}
            if(included.Count==0)continue;
            var group=_tree.CreateItem(root);string groupId="skeleton:"+s;
            group.SetText(0,$"骨架 {s} · {skeleton.Name}");group.SetTooltipText(0,entry.Path);group.SetSelectable(0,false);group.SetMetadata(0,groupId);
            group.SetCustomColor(0,StudioTheme.Muted);group.Collapsed=_query.Length==0&&_collapsed.GetValueOrDefault(groupId);
            var stack=new Stack<(int Bone,TreeItem Parent)>();
            foreach(int b in skeleton.GetParentlessBones().Reverse())stack.Push((b,group));
            while(stack.TryPop(out var next))
            {
                if(!included.Contains(next.Bone))continue;
                var id=new BoneId(s,next.Bone);var item=_tree.CreateItem(next.Parent);string key=BoneInspection.IdText(id);_items[id]=item;
                item.SetText(0,Bones.Name(id));item.SetMetadata(0,key);item.SetTooltipText(0,$"{Bones.Name(id)} [{key}]\n{entry.Path}");
                item.SetTextOverrunBehavior(0,TextServer.OverrunBehavior.TrimEllipsis);
                if(!direct.Contains(next.Bone))item.SetCustomColor(0,StudioTheme.Muted);
                item.Collapsed=_query.Length==0&&_collapsed.GetValueOrDefault(key);
                foreach(int child in skeleton.GetBoneChildren(next.Bone).Reverse())stack.Push((child,item));
            }
        }
        _count.Text=_query.Length==0?$"{Bones.Skeletons.Count} 个骨架 · {total} 根骨骼":$"匹配 {matches} / {total} · 保留父级层级";
        _empty.Visible=_items.Count==0;
        _empty.Text=Preview.Model==null?"选择一个资产以查看骨骼。":total==0?"此资产没有骨架。":"没有匹配的骨骼，请调整搜索条件。";
    }
    private static void ExpandAncestors(TreeItem item){for(var parent=item.GetParent();parent!=null;parent=parent.GetParent())parent.Collapsed=false;}
    private void Locate()
    {
        if(Bones.Selected is not {} id)return;
        _search.Text="";Sync();
        foreach(var bone in Bones.SelectedBones)if(_items.TryGetValue(bone,out var selected))ExpandAncestors(selected);
        if(_items.TryGetValue(id,out var item))
        {
            _syncing=true;try{item.Select(0);}finally{_syncing=false;}
            _tree.ScrollToItem(item);_tree.GrabFocus();
        }
    }
    private string StatusText()
    {
        if(Bones.Skeletons.Count==0)return "";
        if(Preview.Duration<=0)return "此资产没有可用动画，可查看和选择骨骼。";
        if(Bones.Status=="Sampling")return "正在计算轨迹… 可继续播放或切换骨骼。";
        if(Bones.Status.StartsWith("Failed:")||Bones.Status.StartsWith("Unavailable:"))return "轨迹不可用："+Bones.Status[(Bones.Status.IndexOf(':')+1)..].Trim();
        if(Bones.Trajectories.Count>0)return $"显示 {Bones.Trajectories.Count} 条骨骼轨迹 · {Bones.Trajectories.Sum(t=>t.Trajectory.Samples.Length):N0} 个采样点\n绿：起点　红：终点　白：当前";
        return Bones.Selected==null?"选择骨骼后可开启或固定轨迹。":"选中骨骼的轨迹已关闭。";
    }
    private void UpdateInfo()
    {
        if(Bones.Selected is not {} id){_selected.Text="尚未选中骨骼";_pose.Text="";_pose.Visible=false;return;}
        int filtered=Bones.SelectedBones.Count(b=>!_items.ContainsKey(b));
        _selected.Text=$"已选 {Bones.SelectedBones.Count} 根 · 主选 {Bones.Name(id)} [{BoneInspection.IdText(id)}]"+(filtered>0?$"\n{filtered} 根选中骨骼不在筛选结果中":"");
        _selected.TooltipText=Bones.Skeletons[id.Skeleton].Path;var p=Bones.WorldPose(id).Origin;
        _pose.Visible=true;_pose.Text=$"世界位置  {p.X:F3}, {p.Y:F3}, {p.Z:F3}\n当前时间  {Preview.Playhead:F2} 秒";
    }
    public override void _Process(double delta)
    {
        _elapsed+=delta;if(_elapsed<.1)return;_elapsed=0;
        if(!SearchComposing&&_query!=_search.Text.Trim())QueueSync();
        if(Bones.Selected!=null)UpdateInfo();
        UpdateRootInfo();
    }
}
