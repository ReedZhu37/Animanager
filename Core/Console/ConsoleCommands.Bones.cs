using Godot;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private void RegisterBoneCommands()
    {
        Bind("bone tree",nameof(BoneTree),"Search names, retaining ancestors: bone tree <query_or_*>");
        Bind("bone select",nameof(BoneSelect),"Highlight by exact name or skeleton:bone ID, e.g. 0:12");
        Bind("bone select_many",nameof(BoneSelectMany),"Replace selection atomically: bone select_many \"LeftFoot,RightFoot\"");
        Bind("bone add",nameof(BoneAdd),"Add one bone to the selection");
        Bind("bone remove",nameof(BoneRemove),"Remove one bone from the selection");
        Bind("bone selection",nameof(BoneSelection),"List all selected bones; > marks the primary bone");
        Bind("bone info",nameof(BoneInfo),"Selected bone parent, local/world position and rotation");
        Bind("bone clear",nameof(BoneClear),"Clear selection; pinned trajectories and Root Motion remain");
        Bind("bone focus",nameof(BoneFocus),"Center the camera on the selected bone");
        Bind("bone show",nameof(BoneShow),"Show all skeleton lines: bone show <bool>");
        Bind("bone xray",nameof(BoneXray),"Draw bone/trajectory overlays through the mesh: bone xray <bool>");
        Bind("trail show",nameof(TrailShow),"Show/hide all selected bone trajectories: <bool>; pins remain");
        Bind("trail world",nameof(TrailWorld),"Trajectory in preview world coordinates");
        Bind("trail relative",nameof(TrailRelative),"Trajectory relative to a named bone or skeleton:bone ID");
        Bind("trail rate",nameof(TrailRate),"Sampling rate, 30 or 60 samples/second (not source FPS)");
        Bind("trail status",nameof(TrailStatus),"Trajectory status, reference space, sample count and endpoints");
        Bind("trail pin",nameof(TrailPin),"Pin selected bones with their current reference space and sample rate");
        Bind("trail pins",nameof(TrailPins),"List pin IDs, bones, reference spaces, rates and visibility");
        Bind("trail unpin",nameof(TrailUnpin),"Remove a pinned trajectory by pin ID");
        Bind("trail visible",nameof(TrailVisible),"Set pin visibility: trail visible <pin_id> <bool>");
        Bind("trail clear_pins",nameof(TrailClearPins),"Remove all pinned trajectories");
        Bind("rootmotion source",nameof(RootMotionSource),"Explicitly choose a parentless root bone by name or skeleton:bone ID");
        Bind("rootmotion show",nameof(RootMotionShow),"Show/hide world-space Root Motion path and headings: <bool>");
        Bind("rootmotion status",nameof(RootMotionStatus),"Root Motion source, displacement and rotation delta; no extraction");
        Bind("rootmotion sample",nameof(RootMotionSample),"Query interpolated Root Motion at clip time in seconds");
        foreach(var command in new[]{"bone select","bone add","bone remove","trail relative","rootmotion source"})
            _console.Call("add_argument_autocomplete_source",command,0,Callable.From(()=>
                _preview.Bones.Skeletons.SelectMany(s=>Enumerable.Range(0,s.Node.GetBoneCount()).Select(i=>s.Node.GetBoneName(i).ToString())).Distinct().ToArray()));
        _preview.Bones.Notice+=Write;
    }
    public void BoneTree(string query)=>Guard(()=>Info(_preview.Bones.Tree(query)));
    public void BoneSelect(string bone)=>Guard(()=>{_preview.Bones.Select(bone);PendingTask=_preview.Bones.PendingSampling;Info(_preview.Bones.Info());});
    private void SelectionAction(Action action)=>Guard(()=>{action();PendingTask=_preview.Bones.PendingSampling;Info(_preview.Bones.SelectionInfo());});
    public void BoneSelectMany(string bones)=>SelectionAction(()=>_preview.Bones.SetSelection(bones.Split(',').Select(s=>s.Trim())));
    public void BoneAdd(string bone)=>SelectionAction(()=>_preview.Bones.AddSelection(bone));
    public void BoneRemove(string bone)=>SelectionAction(()=>_preview.Bones.RemoveSelection(bone));
    public void BoneSelection()=>Guard(()=>Info(_preview.Bones.SelectionInfo()));
    public void BoneInfo()=>Guard(()=>Info(_preview.Bones.Info()));
    public void BoneClear()=>SelectionAction(_preview.Bones.ClearSelection);
    public void BoneFocus()=>Guard(()=>{_preview.Bones.Focus();Info("Camera centered on selected bone.");});
    public void BoneShow(bool enabled)=>Guard(()=>{_preview.Bones.ShowSkeleton(enabled);Info("Skeleton overlay: "+enabled);});
    public void BoneXray(bool enabled)=>Guard(()=>{_preview.Bones.SetXray(enabled);Info("Overlay X-ray: "+enabled);});
    private void TrailAction(Action action)=>Guard(()=>{action();PendingTask=_preview.Bones.PendingSampling;Info(_preview.Bones.TrailInfo());});
    public void TrailShow(bool enabled)=>TrailAction(()=>_preview.Bones.ShowTrail(enabled));
    public void TrailWorld()=>TrailAction(()=>_preview.Bones.SetReference(null));
    public void TrailRelative(string bone)=>TrailAction(()=>_preview.Bones.SetReference(bone));
    public void TrailRate(int rate)=>TrailAction(()=>_preview.Bones.SetRate(rate));
    public void TrailStatus()=>Guard(()=>Info(_preview.Bones.TrailInfo()));
    public void TrailPin()=>Guard(()=>{_preview.Bones.PinSelected();PendingTask=_preview.Bones.PendingSampling;Info(_preview.Bones.PinsInfo());});
    public void TrailPins()=>Guard(()=>Info(_preview.Bones.PinsInfo()));
    public void TrailUnpin(int id)=>TrailAction(()=>_preview.Bones.Unpin(id));
    public void TrailVisible(int id,bool visible)=>TrailAction(()=>_preview.Bones.SetPinVisible(id,visible));
    public void TrailClearPins()=>TrailAction(_preview.Bones.ClearPins);
    private void RootMotionAction(Action action)=>Guard(()=>{action();PendingTask=_preview.Bones.PendingSampling;Info(_preview.Bones.RootMotionInfo());});
    public void RootMotionSource(string bone)=>RootMotionAction(()=>_preview.Bones.SetRootMotionSource(bone));
    public void RootMotionShow(bool enabled)=>RootMotionAction(()=>_preview.Bones.ShowRootMotion(enabled));
    public void RootMotionStatus()=>Guard(()=>Info(_preview.Bones.RootMotionInfo()));
    public void RootMotionSample(double seconds)=>Guard(()=>{var s=_preview.Bones.RootMotionAt(seconds);Info($"Time {s.Time:F4}s | World position {s.WorldPosition} | World rotation {s.WorldRotation}\nDisplacement {s.Displacement} | Rotation delta {s.RotationDelta}");});
}
