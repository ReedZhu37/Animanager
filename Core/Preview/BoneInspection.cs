using Godot;
using System.Text;
using Animanager.UI.Components;

namespace Animanager.Core.Preview;

public readonly record struct BoneId(int Skeleton,int Index);
public sealed record SkeletonEntry(string Path,Skeleton3D Node);
public sealed record TrajectorySample(double Time,Vector3 Position);
public sealed record BoneTrajectory(string Clip,BoneId Bone,BoneId? Reference,int Rate,TrajectorySample[] Samples);

/// <summary>Session-local bone selection and cancellable main-thread trajectory evaluation.</summary>
public sealed partial class BoneInspection
{
    private readonly Preview3DViewport _preview;
    private readonly Node3D _stage;
    private readonly BoneOverlay _overlay;
    private readonly List<BoneId> _selection=[];
    private readonly List<PinnedBoneTrail> _pins=[];
    private int _nextPinId=1;
    private CancellationTokenSource? _sampling;
    private Node3D? _model;
    private AnimationPlayer? _player;
    private PoseSnapshot? _baseline;
    private bool _showSkeleton, _skeletonOnly;
    public IReadOnlyList<SkeletonEntry> Skeletons { get; private set; }=[];
    public BoneId? Selected=>_selection.Count>0?_selection[^1]:null;
    public IReadOnlyList<BoneId> SelectedBones { get; }
    public IReadOnlyList<PinnedBoneTrail> Pins { get; }
    public BoneId? Reference { get; private set; }
    public bool TrailEnabled { get; private set; }
    public int SampleRate { get; private set; }=30;
    public IReadOnlyList<DisplayedBoneTrail> Trajectories { get; private set; }=[];
    public BoneTrajectory? Trajectory=>TrailEnabled&&Selected is {} bone?Trajectories.Select(t=>t.Trajectory)
        .FirstOrDefault(t=>t.Bone==bone&&t.Reference==Reference&&t.Rate==SampleRate):null;
    public BoneId? RootMotionSource { get; private set; }
    public bool RootMotionEnabled { get; private set; }
    public RootMotionTrack? RootMotion { get; private set; }
    public string Status { get; private set; }="Off";
    public Task PendingSampling { get; private set; }=Task.CompletedTask;
    public event Action<string>? Notice;
    // Structural/settings changes only; pose updates must not rebuild UI trees every frame.
    public event Action? Changed;
    public long ModelRevision { get; private set; }
    public int SamplingRuns { get; private set; }
    public BoneOverlay Overlay=>_overlay;

    public BoneInspection(Preview3DViewport preview,Node3D stage)
    {
        _preview=preview;_stage=stage;SelectedBones=_selection.AsReadOnly();Pins=_pins.AsReadOnly();
        _overlay=new BoneOverlay{Name="BoneInspectionOverlay"};stage.AddChild(_overlay);
    }

    public void Bind(Node3D model,AnimationPlayer? player,PoseSnapshot baseline,bool skeletonOnly)
    {
        Clear();_model=model;_player=player;_baseline=baseline;
        Skeletons=Preview3DViewport.Descendants(model).OfType<Skeleton3D>()
            .Select(s=>new SkeletonEntry(model.GetPathTo(s).ToString(),s)).ToArray();
        _skeletonOnly = skeletonOnly && Skeletons.Count > 0;
        _overlay.ShowSkeleton = _skeletonOnly || _showSkeleton;
        _overlay.SetScaleFromSkeletons(Skeletons);
        if(player!=null)player.MixerApplied+=RefreshPose;
        RefreshPose();Changed?.Invoke();
    }

    public void Clear()
    {
        CancelSampling();
        if(_player!=null&&GodotObject.IsInstanceValid(_player))_player.MixerApplied-=RefreshPose;
        _model=null;_player=null;_baseline=null;Skeletons=[];_selection.Clear();_pins.Clear();Reference=null;
        RootMotionSource=null;RootMotionEnabled=false;RootMotion=null;TrailEnabled=false;Trajectories=[];Status="Off";ClearCache();
        _skeletonOnly = false;
        _overlay.ShowSkeleton = _showSkeleton;
        _overlay.Clear();ModelRevision++;Changed?.Invoke();
    }

    public BoneId Resolve(string name)
    {
        var parts=name.Split(':');
        if(parts.Length==2&&int.TryParse(parts[0],out int s)&&int.TryParse(parts[1],out int b))
        {
            if(s>=0&&s<Skeletons.Count&&b>=0&&b<Skeletons[s].Node.GetBoneCount())return new(s,b);
            throw new ArgumentException("Bone ID is out of range. Use preview bones.");
        }
        var matches=Skeletons.SelectMany((entry,i)=>Enumerable.Range(0,entry.Node.GetBoneCount())
            .Where(b=>entry.Node.GetBoneName(b).ToString()==name).Select(b=>new BoneId(i,b))).ToArray();
        return matches.Length switch{1=>matches[0],0=>throw new ArgumentException("Unknown bone: "+name),
            _=>throw new ArgumentException("Ambiguous bone name; use skeleton:bone ID: "+string.Join(", ",matches.Select(IdText)))};
    }
    public static string IdText(BoneId bone)=>$"{bone.Skeleton}:{bone.Index}";
    public string Name(BoneId bone)=>Skeletons[bone.Skeleton].Node.GetBoneName(bone.Index).ToString();
    public Transform3D WorldPose(BoneId bone)
    {
        var s=Skeletons[bone.Skeleton].Node;return s.GlobalTransform*s.GetBoneGlobalPose(bone.Index);
    }
    public Transform3D ReferenceTransform=>Reference is {} r?WorldPose(r):Transform3D.Identity;

    public string Tree(string query="*")
    {
        if(_model==null)throw new InvalidOperationException("Load an asset first.");
        if(Skeletons.Count==0)return "No skeletons in this asset.";
        var output=new StringBuilder();int matches=0;
        for(int s=0;s<Skeletons.Count;s++)
        {
            var entry=Skeletons[s];var skeleton=entry.Node;var included=new HashSet<int>();var direct=new HashSet<int>();
            for(int b=0;b<skeleton.GetBoneCount();b++)
                if(query=="*"||skeleton.GetBoneName(b).ToString().Contains(query,StringComparison.OrdinalIgnoreCase))
                {
                    matches++;direct.Add(b);
                    for(int ancestor=b;ancestor>=0;ancestor=skeleton.GetBoneParent(ancestor))included.Add(ancestor);
                }
            if(included.Count==0)continue;
            output.AppendLine($"Skeleton {s}: {entry.Path} ({skeleton.GetBoneCount()} bones)");
            var stack=new Stack<(int Bone,int Depth)>();
            foreach(int b in skeleton.GetParentlessBones().Reverse())stack.Push((b,0));
            while(stack.TryPop(out var item))
            {
                if(!included.Contains(item.Bone))continue;
                var id=new BoneId(s,item.Bone);
                output.Append(' ',Math.Min(item.Depth,32)*2).Append(Selected==id?"> ":_selection.Contains(id)?"+ ":"- ")
                    .Append(IdText(id)).Append(' ').Append(Name(id))
                    .AppendLine(direct.Contains(item.Bone)?"":" (ancestor)");
                foreach(int child in skeleton.GetBoneChildren(item.Bone).Reverse())stack.Push((child,item.Depth+1));
            }
        }
        return output.Length>0?output.Append($"{matches} matching bones").ToString():"No matching bones.";
    }

    public void Select(string name)
    {
        SetSelection([name]);
    }
    /// <summary>Atomic replace. The final unique item is the primary bone used by the existing UI.</summary>
    public void SetSelection(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);var next=names.Select(Resolve).Distinct().ToArray();
        if(_selection.SequenceEqual(next))return;
        _selection.Clear();_selection.AddRange(next);if(next.Length==0)TrailEnabled=false;
        RefreshPose();RestartTrail();
    }
    public void AddSelection(string name)
    {
        var id=Resolve(name);if(_selection.Contains(id))return;
        _selection.Add(id);RefreshPose();RestartTrail();
    }
    public void RemoveSelection(string name)
    {
        var id=Resolve(name);if(!_selection.Remove(id))return;
        if(_selection.Count==0)TrailEnabled=false;RefreshPose();RestartTrail();
    }
    public void ClearSelection(){_selection.Clear();TrailEnabled=false;RefreshPose();RestartTrail();}
    public string SelectionInfo()=>_selection.Count==0?"No selected bones.":string.Join("\n",_selection.Select(id=>$"{(id==Selected?">":"+")} {IdText(id)} {Name(id)}"));

    public IReadOnlyList<PinnedBoneTrail> PinSelected()
    {
        if(_selection.Count==0)throw new InvalidOperationException("Select bones before pinning.");ValidateClip(SampleRate);
        var result=new List<PinnedBoneTrail>();
        foreach(var id in _selection)
        {
            var pin=_pins.FirstOrDefault(p=>p.Bone==id&&p.Reference==Reference&&p.Rate==SampleRate);
            if(pin==null){pin=new(_nextPinId++,id,Reference,SampleRate);_pins.Add(pin);}
            else if(!pin.Visible){_pins[_pins.IndexOf(pin)]=pin=pin with{Visible=true};}
            result.Add(pin);
        }
        RestartTrail();return result.AsReadOnly();
    }
    public void Unpin(int id){int index=PinIndex(id);_pins.RemoveAt(index);RestartTrail();}
    public void SetPinVisible(int id,bool visible)
    {int index=PinIndex(id);if(_pins[index].Visible==visible)return;_pins[index]=_pins[index] with{Visible=visible};RestartTrail();}
    private int PinIndex(int id){int index=_pins.FindIndex(p=>p.Id==id);return index>=0?index:throw new ArgumentException("Unknown pin ID: "+id);}
    public void ClearPins(){_pins.Clear();RestartTrail();}
    public string PinsInfo()=>_pins.Count==0?"No pinned trajectories.":string.Join("\n",_pins.Select(p=>$"pin {p.Id}: {IdText(p.Bone)} {Name(p.Bone)} | {(p.Reference is {} r?"relative to "+IdText(r)+" "+Name(r):"world")} | {p.Rate}/s | visible={p.Visible}"));
    public void SetRootMotionSource(string name)
    {
        var id=Resolve(name);
        if(Skeletons[id.Skeleton].Node.GetBoneParent(id.Index)>=0)throw new ArgumentException("Root Motion source must be a parentless bone. Use preview bones to choose a root explicitly.");
        if(RootMotionSource==id)return;RootMotionSource=id;RestartTrail();
    }
    public void ShowRootMotion(bool enabled)
    {
        if(enabled){if(RootMotionSource==null)throw new InvalidOperationException("Choose a root first: rootmotion source <name_or_id>");ValidateClip(SampleRate);}
        RootMotionEnabled=enabled;RestartTrail();
    }
    public RootMotionSample RootMotionAt(double time)=>RootMotion?.At(time)??throw new InvalidOperationException("Root Motion samples are not ready.");
    public string RootMotionInfo()
    {
        var header=$"Root Motion enabled={RootMotionEnabled} | source={(RootMotionSource is {} id?IdText(id)+" "+Name(id):"unset")} | {Status}";
        if(RootMotion is not {} motion)return header+"\nNo Root Motion displayed.";
        var last=motion.Samples[^1];return header+$"\n{motion.Clip} | {motion.Samples.Count} samples | {last.Time:F3} s\nDisplacement in initial root frame: {last.Displacement}\nRotation delta quaternion: {last.RotationDelta}\nWorld path; full XYZ motion; no loop accumulation or in-place extraction.";
    }
    public string Info()
    {
        if(Selected is not {} id)throw new InvalidOperationException("Select a bone first: bone select <name_or_id>");
        var s=Skeletons[id.Skeleton].Node;int parent=s.GetBoneParent(id.Index);var local=s.GetBonePose(id.Index);var world=WorldPose(id);
        return $"{IdText(id)} {Name(id)} | skeleton {Skeletons[id.Skeleton].Path}\nParent: {(parent<0?"none":IdText(new(id.Skeleton,parent))+" "+s.GetBoneName(parent))}\nTime: {_preview.Playhead:F4} s\nLocal position: {local.Origin}; rotation (Euler XYZ degrees): {local.Basis.Orthonormalized().GetEuler(EulerOrder.Xyz)*180/Mathf.Pi}\nWorld position: {world.Origin}; rotation (Euler XYZ degrees): {world.Basis.Orthonormalized().GetEuler(EulerOrder.Xyz)*180/Mathf.Pi}";
    }
    public void ShowSkeleton(bool enabled)
    {
        if (!_skeletonOnly) _showSkeleton = enabled;
        _overlay.ShowSkeleton = enabled;
        RefreshPose();Changed?.Invoke();
    }
    public void SetXray(bool enabled){_overlay.SetXray(enabled);_preview.Redraw();Changed?.Invoke();}
    public void Focus()
    {
        if(Selected is not {} id)throw new InvalidOperationException("Select a bone first.");
        _preview.FocusPoint(WorldPose(id).Origin);
    }
    public void ShowTrail(bool enabled)
    {
        if(enabled){if(Selected==null)throw new InvalidOperationException("Select bones first.");ValidateClip(SampleRate);}
        TrailEnabled=enabled;RestartTrail();
    }
    public void SetReference(string? name)
    {
        BoneId? next=name==null?null:Resolve(name);
        if(next==Reference)return;
        Reference=next;RestartTrail();
    }
    public void SetRate(int rate)
    {
        if(rate is not (30 or 60))throw new ArgumentException("Use a sample rate of 30 or 60 samples/second.");
        if(rate==SampleRate)return;SampleRate=rate;RestartTrail();
    }
    public string TrailInfo()=> $"{Status} | enabled={TrailEnabled} | selected={_selection.Count} | pinned={_pins.Count} | displayed={Trajectories.Count} | {SampleRate} samples/s | space={(Reference is {} r?"relative to "+IdText(r)+" "+Name(r):"world")}\n"+
        (Trajectory is {} t?$"{t.Clip} | {Name(t.Bone)} | {t.Samples.Length} samples | {t.Samples[0].Time:F3}..{t.Samples[^1].Time:F3} s\nStart: {t.Samples[0].Position}; end: {t.Samples[^1].Position}\nGreen=start, red=end, white=current. Relative paths follow their reference bone's current pose.":Trajectories.Count>0?"Pinned trajectories displayed. Use trail pins for details.":"No bone trajectory displayed.");
    public void ClipChanged(){ClearCache();RestartTrail();RefreshPose();}
    public void RefreshPose(){_overlay.UpdatePose(this,_preview.Playhead);_preview.Redraw();}
}
