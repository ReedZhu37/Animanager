using Godot;
using System.Diagnostics;
using Animanager.UI.Components;

namespace Animanager.Core.Preview;

public sealed partial class BoneInspection
{
    public const int MaxSamplesPerTrack=120001;
    public const int MaxCachedPoseSamples=1000000;
    public const int MaxDisplayedSamples=1000000;
    private readonly record struct PoseKey(string Clip,BoneId Bone,int Rate);
    private readonly record struct PoseSample(double Time,Transform3D World);
    private readonly record struct TrailKey(BoneId Bone,BoneId? Reference,int Rate);
    private sealed record Request(TrailKey Key,int? PinId);
    private readonly Dictionary<PoseKey,PoseSample[]> _cache=[];
    private readonly Queue<PoseKey> _cacheOrder=[];
    private int _cachedSamples;
    private void ClearCache(){_cache.Clear();_cacheOrder.Clear();_cachedSamples=0;}
    private static int SampleCount(double duration,int rate)=>Math.Max(1,(int)Math.Ceiling(duration*rate-1e-5))+1;
    private void ValidateClip(int rate)
    {
        if(_player==null||_preview.CurrentClip.Length==0||!double.IsFinite(_preview.Duration)||_preview.Duration<=0)
            throw new InvalidOperationException("No animation clip available for trajectory sampling.");
        if(_preview.Duration*rate>MaxSamplesPerTrack-1+1e-5)
            throw new InvalidOperationException("Clip exceeds the 120,001-sample limit; use 30 samples/s or a shorter clip.");
    }
    private List<Request> Requests()
    {
        var requests=new Dictionary<TrailKey,Request>();
        foreach(var pin in _pins.Where(p=>p.Visible))
        {var key=new TrailKey(pin.Bone,pin.Reference,pin.Rate);requests[key]=new(key,pin.Id);}
        if(TrailEnabled)foreach(var bone in _selection)
        {var key=new TrailKey(bone,Reference,SampleRate);requests.TryAdd(key,new(key,null));}
        return requests.Values.ToList();
    }
    private HashSet<PoseKey> RequiredPoses(List<Request> requests)
    {
        var keys=new HashSet<PoseKey>();string clip=_preview.CurrentClip;
        foreach(var request in requests)
        {var key=request.Key;keys.Add(new(clip,key.Bone,key.Rate));if(key.Reference is {} r)keys.Add(new(clip,r,key.Rate));}
        if(RootMotionEnabled&&RootMotionSource is {} root)keys.Add(new(clip,root,SampleRate));
        return keys;
    }
    private void CancelSampling(){_sampling?.Cancel();_sampling=null;}
    private void RestartTrail()
    {
        CancelSampling();
        try
        {
            var requests=Requests();var required=RequiredPoses(requests);
            if(required.Count==0){Trajectories=[];RootMotion=null;Status="Off";UpdateOverlay();return;}
            foreach(int rate in required.Select(k=>k.Rate).Distinct())ValidateClip(rate);
            long count=required.Sum(k=>(long)SampleCount(_preview.Duration,k.Rate));
            if(count>MaxCachedPoseSamples)throw new InvalidOperationException("Visible trajectories exceed the 1,000,000 pose-sample budget; hide pins or reduce the sampling rate.");
            long displayedCount=requests.Sum(r=>(long)SampleCount(_preview.Duration,r.Key.Rate))+
                (RootMotionEnabled?SampleCount(_preview.Duration,SampleRate):0);
            if(displayedCount>MaxDisplayedSamples)throw new InvalidOperationException("Visible trajectories exceed the 1,000,000 displayed-sample budget; hide pins or reduce the sampling rate.");
            var missing=required.Where(k=>!_cache.ContainsKey(k)).ToArray();
            Status=missing.Length==0?"Ready":"Sampling";PublishAvailable(requests);
            if(missing.Length==0){PendingSampling=Task.CompletedTask;return;}
            var cancellation=new CancellationTokenSource();_sampling=cancellation;
            PendingSampling=SampleAsync(missing,requests,required,cancellation);
        }
        catch(Exception ex){Trajectories=[];RootMotion=null;Status="Unavailable: "+ex.Message;UpdateOverlay();Notice?.Invoke(Status);}
        finally{Changed?.Invoke();}
    }
    private void PublishAvailable(List<Request> requests)
    {
        var displayed=new List<DisplayedBoneTrail>();string clip=_preview.CurrentClip;
        foreach(var request in requests)
        {
            var key=request.Key;
            if(!_cache.TryGetValue(new(clip,key.Bone,key.Rate),out var poses))continue;
            PoseSample[]? references=null;
            if(key.Reference is {} r&&!_cache.TryGetValue(new(clip,r,key.Rate),out references))continue;
            var samples=new TrajectorySample[poses.Length];
            for(int i=0;i<poses.Length;i++)
            {
                var position=poses[i].World.Origin;
                if(references!=null)
                {
                    var frame=references[i].World;
                    if(Math.Abs(frame.Basis.Determinant())<1e-10f)throw new InvalidOperationException("Reference bone has a singular transform.");
                    position=frame.AffineInverse()*position;
                }
                samples[i]=new(poses[i].Time,position);
            }
            var trajectory=new BoneTrajectory(clip,key.Bone,key.Reference,key.Rate,samples);
            string id=$"{IdText(key.Bone)}@{(key.Reference is {} reference?IdText(reference):"world")}@{key.Rate}";
            string label=$"{(request.PinId is {} pin?"PIN "+pin+" · ":"")}{Name(key.Bone)} [{IdText(key.Bone)}]";
            displayed.Add(new(id,trajectory,ColorFor(key.Bone),label,request.PinId));
        }
        RootMotionTrack? motion=null;
        if(RootMotionEnabled&&RootMotionSource is {} root&&_cache.TryGetValue(new(clip,root,SampleRate),out var rootPoses))
        {
            Quaternion Rotation(Transform3D pose)
            {
                if(pose.Basis.Determinant()<=1e-10f)throw new InvalidOperationException("Root Motion requires a non-singular, non-reflected root transform.");
                return pose.Basis.Orthonormalized().GetRotationQuaternion();
            }
            var start=rootPoses[0].World;var inverseRotation=Rotation(start).Inverse();
            var samples=rootPoses.Select(p=>new RootMotionSample(p.Time,p.World.Origin,Rotation(p.World),
                inverseRotation*(p.World.Origin-start.Origin),(inverseRotation*Rotation(p.World)).Normalized())).ToArray();
            motion=new(clip,root,SampleRate,Array.AsReadOnly(samples));
        }
        Trajectories=displayed.AsReadOnly();RootMotion=motion;
        UpdateOverlay();
    }
    public Color ColorFor(BoneId bone)=>Color.FromHsv(Mathf.PosMod((bone.Index+bone.Skeleton*37)*.61803399f+.52f,1),.55f,1);
    private void UpdateOverlay(){_overlay.SetTrajectories(Trajectories,RootMotion);RefreshPose();}

    private async Task SampleAsync(PoseKey[] missing,List<Request> requests,HashSet<PoseKey> required,CancellationTokenSource cancellation)
    {
        Node3D? clone=null;
        try
        {
            await _preview.ToSignal(_preview.GetTree(),SceneTree.SignalName.ProcessFrame);cancellation.Token.ThrowIfCancellationRequested();
            var model=_model!;var player=_player!;var baseline=_baseline!;
            clone=(Node3D)model.Duplicate(0);clone.Name="TrajectorySampler";clone.Visible=false;clone.ProcessMode=Node.ProcessModeEnum.Disabled;
            var sampler=clone.GetNode<AnimationPlayer>(model.GetPathTo(player));
            foreach(var mixer in Preview3DViewport.Descendants(clone).OfType<AnimationMixer>())mixer.Active=false;
            sampler.Autoplay="";sampler.Stop();
            foreach(var library in sampler.GetAnimationLibraryList())sampler.RemoveAnimationLibrary(library);
            var animation=(Animation)player.GetAnimation(missing[0].Clip).Duplicate(true);animation.LoopMode=Animation.LoopModeEnum.None;
            for(int track=0;track<animation.GetTrackCount();track++)
                if(animation.TrackGetType(track) is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D))animation.TrackSetEnabled(track,false);
            var libraryCopy=new AnimationLibrary();libraryCopy.AddAnimation("sample",animation);sampler.AddAnimationLibrary("",libraryCopy);
            sampler.RootMotionTrack=new NodePath("");sampler.CallbackModeProcess=AnimationMixer.AnimationCallbackModeProcess.Manual;
            sampler.PlaybackAutoCapture=false;sampler.Active=true;_stage.AddChild(clone);baseline.Restore(clone);
            var skeletons=Skeletons.Select(s=>clone.GetNode<Skeleton3D>(s.Path)).ToArray();
            sampler.Play("sample",0);sampler.Advance(0);sampler.Pause();
            var results=new Dictionary<PoseKey,PoseSample[]>();SamplingRuns++;var budget=Stopwatch.StartNew();
            // One isolated scene and one seek per time/rate, regardless of the number of selected bones.
            foreach(var group in missing.GroupBy(k=>k.Rate))
            {
                var keys=group.ToArray();int count=SampleCount(animation.Length,group.Key);
                foreach(var key in keys)results[key]=new PoseSample[count];
                for(int i=0;i<count;i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();double time=i==count-1?animation.Length:(double)i/group.Key;
                    sampler.Seek(time,true,true);
                    foreach(var key in keys)
                    {
                        var skeleton=skeletons[key.Bone.Skeleton];var pose=skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(key.Bone.Index);
                        if(!pose.IsFinite())throw new InvalidOperationException("Animation produced a non-finite bone transform.");
                        results[key][i]=new(time,pose);
                    }
                    if(i<count-1&&(i%128==127||budget.ElapsedMilliseconds>=4))
                    {await _preview.ToSignal(_preview.GetTree(),SceneTree.SignalName.ProcessFrame);budget.Restart();}
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            foreach(var pair in results){_cache[pair.Key]=pair.Value;_cacheOrder.Enqueue(pair.Key);_cachedSamples+=pair.Value.Length;}
            int attempts=_cacheOrder.Count;
            while(_cachedSamples>MaxCachedPoseSamples&&attempts-->0)
            {
                var key=_cacheOrder.Dequeue();if(required.Contains(key)){_cacheOrder.Enqueue(key);continue;}
                if(_cache.Remove(key,out var removed))_cachedSamples-=removed.Length;
            }
            PublishAvailable(requests);Status="Ready";Changed?.Invoke();Notice?.Invoke(TrailInfo()+(RootMotionEnabled?"\n"+RootMotionInfo():""));
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!cancellation.IsCancellationRequested){Status="Failed: "+ex.Message;Notice?.Invoke(Status);Changed?.Invoke();}}
        finally
        {
            if(clone!=null&&GodotObject.IsInstanceValid(clone)){clone.GetParent()?.RemoveChild(clone);clone.Free();}
            if(_sampling==cancellation)_sampling=null;cancellation.Dispose();
        }
    }
}
