using Godot;

namespace Animanager.Core.Preview;

public sealed record PinnedBoneTrail(int Id,BoneId Bone,BoneId? Reference,int Rate,bool Visible=true);
public sealed record DisplayedBoneTrail(string Id,BoneTrajectory Trajectory,Color Color,string Label,int? PinId);
public readonly record struct RootMotionSample(double Time,Vector3 WorldPosition,Quaternion WorldRotation,
    Vector3 Displacement,Quaternion RotationDelta);

/// <summary>Rigid motion relative to the first sample; never accumulates across playback loops.</summary>
public sealed record RootMotionTrack(string Clip,BoneId Source,int Rate,IReadOnlyList<RootMotionSample> Samples)
{
    public RootMotionSample At(double time)
    {
        if(!double.IsFinite(time)||time<0||time>Samples[^1].Time)throw new ArgumentOutOfRangeException(nameof(time));
        int lo=0,hi=Samples.Count-1;
        while(lo<hi){int mid=(lo+hi)/2;if(Samples[mid].Time<time)lo=mid+1;else hi=mid;}
        if(lo==0||Samples[lo].Time==time)return Samples[lo];
        var a=Samples[lo-1];var b=Samples[lo];float weight=(float)((time-a.Time)/(b.Time-a.Time));
        return new(time,a.WorldPosition.Lerp(b.WorldPosition,weight),a.WorldRotation.Slerp(b.WorldRotation,weight),
            a.Displacement.Lerp(b.Displacement,weight),a.RotationDelta.Slerp(b.RotationDelta,weight));
    }
}
