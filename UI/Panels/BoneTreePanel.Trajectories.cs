using Godot;
using Animanager.Core.Preview;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI.Panels;

public partial class BoneTreePanel
{
    private Button _pin=null!,_clearPins=null!;
    private Label _pinCount=null!,_pinEmpty=null!,_rootInfo=null!;
    private ScrollContainer _pinScroll=null!;
    private VBoxContainer _pinList=null!;
    private OptionButton _rootSource=null!;
    private CheckButton _rootMotion=null!;
    private readonly List<BoneId?> _roots=[];
    private readonly Dictionary<int,PinRow> _pinRows=[];
    private sealed record PinRow(Control Root,CheckBox Visible,Label Name,Label Detail,Button Remove);

    private void BuildTrajectoryControls()
    {
        AddChild(new HSeparator());
        _pinCount=Label("固定轨迹",false,14);_pinCount.Name="BonePinCount";AddChild(_pinCount);
        var actions=new HFlowContainer();AddChild(actions);
        _pin=Button("固定选中轨迹",()=>Act(()=>Bones.PinSelected()));_pin.Name="BonePin";actions.AddChild(_pin);
        _pin.TooltipText="固定所有选中骨骼，保留当前参考空间和采样率。切换选择后继续显示。";
        _clearPins=Button("清空固定",()=>Act(Bones.ClearPins));_clearPins.Name="BoneClearPins";actions.AddChild(_clearPins);
        _pinEmpty=Wrapped("尚无固定轨迹。选中骨骼后点击“固定选中轨迹”。");_pinEmpty.Name="BonePinEmpty";AddChild(_pinEmpty);
        _pinScroll=new ScrollContainer{Name="BonePinScroll",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,SizeFlagsHorizontal=SizeFlags.ExpandFill};AddChild(_pinScroll);
        _pinList=new VBoxContainer{Name="BonePinList",SizeFlagsHorizontal=SizeFlags.ExpandFill};_pinScroll.AddChild(_pinList);
        AddChild(new HSeparator());AddChild(Label("Root Motion",false,14));
        _rootSource=new OptionButton{Name="BoneRootSource",FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(0,32)};AddChild(_rootSource);
        _rootSource.TooltipText="明确选择无父骨骼的根关节。编号用于区分不同骨架中的同名根。";
        _rootSource.ItemSelected+=i=>{if(!_syncing&&_roots[(int)i] is {} root)Act(()=>Bones.SetRootMotionSource(BoneInspection.IdText(root)));};
        _rootMotion=Toggle(this,"显示 Root Motion","BoneRootMotion",Bones.ShowRootMotion);
        _rootMotion.TooltipText="世界空间的根运动路径和朝向，独立于选中骨骼及固定轨迹。";
        _rootInfo=Wrapped("");_rootInfo.Name="BoneRootInfo";AddChild(_rootInfo);
    }
    private void RebuildRootChoices()
    {
        _rootSource.Clear();_roots.Clear();_rootSource.AddItem("选择根骨骼…");_roots.Add(null);_rootSource.SetItemDisabled(0,true);
        for(int s=0;s<Bones.Skeletons.Count;s++)
            foreach(int b in Bones.Skeletons[s].Node.GetParentlessBones())
            {
                var id=new BoneId(s,b);_rootSource.AddItem($"{Bones.Name(id)} [{BoneInspection.IdText(id)}]");
                _rootSource.SetItemTooltip(_roots.Count,Bones.Skeletons[s].Path+" / "+Bones.Name(id));_roots.Add(id);
            }
    }
    private void SyncTrajectoryControls()
    {
        _pin.Disabled=Bones.SelectedBones.Count==0||Preview.Duration<=0;
        _clearPins.Disabled=Bones.Pins.Count==0;_pinEmpty.Visible=Bones.Pins.Count==0;
        _pinCount.Text=$"固定轨迹 · {Bones.Pins.Count} 条";
        _pinScroll.Visible=Bones.Pins.Count>0;_pinScroll.CustomMinimumSize=new(0,Math.Min(180,Bones.Pins.Count*88));
        var live=Bones.Pins.Select(p=>p.Id).ToHashSet();
        foreach(int id in _pinRows.Keys.Where(id=>!live.Contains(id)).ToArray())
        {
            var row=_pinRows[id];var focus=GetViewport().GuiGetFocusOwner();bool focused=focus!=null&&row.Root.IsAncestorOf(focus);
            _pinList.RemoveChild(row.Root);row.Root.QueueFree();_pinRows.Remove(id);
            if(focused)_pin.GrabFocus();
        }
        foreach(var pin in Bones.Pins)
        {
            if(!_pinRows.TryGetValue(pin.Id,out var row))
            {
                int id=pin.Id;var container=new PanelContainer{Name="BonePinRow_"+id,SizeFlagsHorizontal=SizeFlags.ExpandFill};_pinList.AddChild(container);
                var column=new VBoxContainer();column.AddThemeConstantOverride("separation",3);container.AddChild(column);var header=Row(column);
                var visible=new CheckBox{Name="BonePinVisible_"+id,TooltipText="显示 / 隐藏此固定轨迹",FocusMode=FocusModeEnum.All};header.AddChild(visible);
                visible.Toggled+=value=>{if(!_syncing)Act(()=>Bones.SetPinVisible(id,value));};
                var name=Label("",false,13);name.Name="BonePinName_"+id;name.SizeFlagsHorizontal=SizeFlags.ExpandFill;name.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;header.AddChild(name);
                var remove=Button("移除",()=>Act(()=>Bones.Unpin(id)));remove.Name="BonePinRemove_"+id;header.AddChild(remove);
                var detail=Wrapped("");detail.Name="BonePinDetail_"+id;column.AddChild(detail);
                row=new(container,visible,name,detail,remove);_pinRows[id]=row;
            }
            row.Visible.SetPressedNoSignal(pin.Visible);row.Name.Text=$"{Bones.Name(pin.Bone)} [{BoneInspection.IdText(pin.Bone)}]";
            row.Name.AddThemeColorOverride("font_color",Bones.ColorFor(pin.Bone));
            string space=pin.Reference is {} reference?$"相对 {Bones.Name(reference)} [{BoneInspection.IdText(reference)}]":"世界空间";
            string info=$"#{pin.Id} · {space} · {pin.Rate} 次/秒";
            bool selectedPath=Bones.TrailEnabled&&Bones.SelectedBones.Contains(pin.Bone)&&Bones.Reference==pin.Reference&&Bones.SampleRate==pin.Rate;
            row.Detail.Text=info+(!pin.Visible?(selectedPath?"\n固定项已隐藏；选中轨迹仍显示":"\n已隐藏"):"");
            row.Name.TooltipText=$"{row.Name.Text}\n{Bones.Skeletons[pin.Bone.Skeleton].Path}\n{info}";
            row.Remove.TooltipText="移除固定项："+row.Name.Text;
        }
        _rootSource.Disabled=_roots.Count<=1;_rootSource.Select(Math.Max(0,_roots.IndexOf(Bones.RootMotionSource)));
        _rootMotion.SetPressedNoSignal(Bones.RootMotionEnabled);
        _rootMotion.Disabled=!Bones.RootMotionEnabled&&(Bones.RootMotionSource==null||Preview.Duration<=0);
        UpdateRootInfo();
    }
    private void UpdateRootInfo()
    {
        if(Bones.Skeletons.Count==0){_rootInfo.Text="加载带骨架的动画以查看根运动。";return;}
        if(Preview.Duration<=0){_rootInfo.Text="此资产没有可用动画。";return;}
        if(Bones.RootMotionSource==null){_rootInfo.Text="先选择根骨骼，再开启显示。";return;}
        if(!Bones.RootMotionEnabled){_rootInfo.Text="世界空间路径与朝向 · 不改变动画播放。";return;}
        if(Bones.RootMotion is not {} motion){_rootInfo.Text=Bones.Status=="Sampling"?"正在计算根运动…":"根运动不可用，请查看轨迹状态。";return;}
        var sample=motion.At(Math.Clamp(Preview.Playhead,0,motion.Samples[^1].Time));var p=sample.Displacement;
        _rootInfo.Text=$"{motion.Samples.Count} 个采样点 · 世界空间\n当前位移  {p.X:F3}, {p.Y:F3}, {p.Z:F3}";
        _rootInfo.TooltipText="位移相对片段首帧原点，以首帧根骨骼朝向表达；不跨循环累计。";
    }
}
