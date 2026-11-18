using System.Numerics;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.MovieScene;
using CUE4Parse.UE4.Objects.UObject;

namespace Animanager.CueImporter;

internal sealed record BoneTrack(string Name,float[][] Positions,float[][] Rotations,float[][] Scales);
internal sealed record Clip(string Name,int Frames,float Duration,string AdditiveType,BoneTrack[] Tracks,string Encoding);

internal static class AnimationReader
{
    private enum FkMode {Replace,Additive,Direct}
    public static Clip Read(Package package,UAnimSequence anim,int major,int minor)
    {
        if(anim.bForceRootLock)throw new NotSupportedException("Force root lock evaluation is not supported.");
        if((int)anim.Interpolation!=0)throw new NotSupportedException("Non-linear sequence interpolation is not supported.");
        if(anim.AdditiveAnimType is not (EAdditiveAnimationType.AAT_None or EAdditiveAnimationType.AAT_RotationOffsetMeshSpace))throw new NotSupportedException("This additive animation type is not validated.");
        if(major==4&&minor is 23 or 27&&anim.RawAnimationData is {Length:>0})
        {
            var names=anim.GetOrDefault<FName[]>("AnimationTrackNames",[]).Select(x=>x.Text).ToArray();
            if(names.Length!=anim.RawAnimationData.Length)throw new InvalidDataException("Raw track name count differs from data count.");
            return Validate(new(anim.Name,anim.NumFrames,anim.SequenceLength,anim.AdditiveAnimType.ToString(),anim.RawAnimationData.Select((t,i)=>new BoneTrack(names[i],t.PosKeys.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray(),t.RotKeys.Select(q=>new[]{q.X,q.Y,q.Z,q.W}).ToArray(),t.ScaleKeys.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray())).ToArray(),"ue4-raw"));
        }
        if(major==5&&minor==0)return ReadEarlyDataModel(anim);
        if(major!=5||minor!=6)throw new NotSupportedException($"Editor animation reader is not validated for UE {major}.{minor}.");
        var model=anim.GetOrDefault<FScriptInterface>("DataModelInterface")?.Object?.Load();
        if(model?.ExportType!="AnimationSequencerDataModel")throw new NotSupportedException("Only UE5.6 AnimationSequencerDataModel is validated; this asset uses another data model.");
        var movie=model.GetOrDefault<FPackageIndex>("MovieScene")?.Load()??throw new InvalidDataException("DataModel MovieScene is missing.");
        var trackRefs=movie.GetOrDefault<FPackageIndex[]>("Tracks",[]);
        if(trackRefs.Length!=1)throw new NotSupportedException("Only a single FK animation data track is supported.");
        var track=trackRefs[0].Load()!;
        if(track.ExportType!="MovieSceneControlRigParameterTrack")throw new NotSupportedException("Data model does not contain an FK parameter track.");
        var sections=track.GetOrDefault<FPackageIndex[]>("Sections",[]);
        if(sections.Length!=1)throw new NotSupportedException("Multiple animation sections require evaluation not implemented by this reader.");
        var section=sections[0].Load()!;
        var rig=section.GetOrDefault<FPackageIndex>("ControlRig")?.Load();
        if(rig?.ExportType!="FKControlRig"||rig.GetOrDefault<FkMode>("ApplyMode")!=FkMode.Direct)throw new NotSupportedException("Only Direct-mode FKControlRig source tracks are supported.");
        var active=rig.GetOrDefault<bool[]>("IsControlActive",[]);
        if(active.Any(a=>!a))throw new NotSupportedException("Disabled FK controls are not supported.");
        var blend=section.GetOrDefault<FStructFallback>("BlendType");
        if(blend!=null&&blend.Properties.Any(p=>p.Name.Text=="BlendType"))throw new NotSupportedException("Non-default blend mode is not supported.");
        if(section.GetOrDefault<bool>("bIsActive",true)==false)throw new NotSupportedException("Inactive animation section.");
        var easing=section.GetOrDefault<FStructFallback>("Easing");
        if(easing?.Properties.Any(p=>p.Name.Text.Contains("Duration",StringComparison.Ordinal))==true||section.Properties.Any(p=>p.Name.Text is "ControlsMask" or "TransformMask" or "Weight"))throw new NotSupportedException("Section easing, transform masks and weights require evaluation not supported by this reader.");
        var rate=movie.GetOrDefault<FStructFallback>("TickResolution")??throw new InvalidDataException("Missing tick rate.");
        double fps=(double)rate.GetOrDefault<int>("Numerator")/rate.GetOrDefault("Denominator",1);
        if(!double.IsFinite(fps)||fps<1||fps>600)throw new NotSupportedException("Invalid or excessive animation sample rate.");
        int frames=(int)Math.Round(anim.SequenceLength*fps)+1;
        if(frames<1||frames>100000)throw new NotSupportedException("Animation exceeds the source frame limit.");
        if(Math.Abs((frames-1)/fps-anim.SequenceLength)>.0001)throw new NotSupportedException("Animation duration is not aligned with source frame rate.");
        var range=section.GetOrDefault<FMovieSceneFrameRange>("SectionRange").Value;
        if(range.LowerBound.Type!=ERangeBoundTypes.Inclusive||range.UpperBound.Type!=ERangeBoundTypes.Inclusive||range.LowerBound.Value.Value!=0||range.UpperBound.Value.Value!=frames-1)throw new NotSupportedException("Trimmed or offset animation sections are not supported.");
        var tracks=new List<BoneTrack>();
        var transforms=section.GetOrDefault<FStructFallback[]>("TransformParameterNamesAndCurves",[]);
        if(transforms.Length>1024||(long)transforms.Length*frames>2000000)throw new NotSupportedException("Animation exceeds the 2,000,000 bone-frame budget.");
        foreach(var transform in transforms)
        {
            var control=transform.GetOrDefault<FName>("ParameterName").Text;
            if(!control.EndsWith("_CONTROL",StringComparison.Ordinal))throw new NotSupportedException("Unrecognized bone control: "+control);
            float[][] Values(string property,float fallback)
            {
                if(!transform.TryGetAllValues<FMovieSceneChannel<float>>(out var channels,property)||channels.Length!=3)throw new NotSupportedException("Incomplete transform channels: "+control+" "+property);
                foreach(var channel in channels)ValidateChannel(channel,frames,fps);
                return Enumerable.Range(0,frames).Select(frame=>channels.Select(c=>Evaluate(c,frame,fallback)).ToArray()).ToArray();
            }
            var positions=Values("Translation",0);var eulers=Values("Rotation",0);var scales=Values("Scale",1);
            var rotations=eulers.Select(e=>{var q=new FRotator(e[1],e[2],e[0]).Quaternion();return new[]{q.X,q.Y,q.Z,q.W};}).ToArray();
            tracks.Add(new(control[..^8],positions,rotations,scales));
        }
        return Validate(new(anim.Name,frames,anim.SequenceLength,anim.AdditiveAnimType.ToString(),tracks.ToArray(),"ue5-sequencer-fk-direct"));
    }
    private static Clip ReadEarlyDataModel(UAnimSequence anim)
    {
        var model=anim.GetOrDefault<FPackageIndex>("DataModel")?.Load();
        if(model?.ExportType!="AnimDataModel")throw new NotSupportedException("UE5.0 source AnimDataModel is missing.");
        var duration=model.GetOrDefault<float>("PlayLength");var intervals=model.GetOrDefault<int>("NumberOfFrames");var keys=model.GetOrDefault<int>("NumberOfKeys");
        var rate=model.GetOrDefault<FStructFallback>("FrameRate")??throw new InvalidDataException("Missing source frame rate.");
        double fps=(double)rate.GetOrDefault<int>("Numerator")/rate.GetOrDefault("Denominator",1);
        if(!double.IsFinite(fps)||fps<1||fps>600||intervals<1||intervals>=100000||keys!=intervals+1||Math.Abs(intervals/fps-duration)>.0001||Math.Abs(anim.SequenceLength-duration)>.0001)throw new NotSupportedException("Invalid or inconsistent DataModel timing.");
        var tracks=model.GetOrDefault<FStructFallback[]>("BoneAnimationTracks",[]);
        if(tracks.Length>1024||(long)tracks.Length*keys>2000000)throw new NotSupportedException("Animation exceeds the bone-frame budget.");
        return Validate(new(anim.Name,keys,duration,anim.AdditiveAnimType.ToString(),tracks.Select(t=>
        {
            var raw=t.GetOrDefault<FStructFallback>("InternalTrackData")??throw new NotSupportedException("Expected tagged UE5.0 source transform arrays.");
            var p=raw.GetOrDefault<TIntVector3<float>[]>("PosKeys",[]);var r=raw.GetOrDefault<FQuat[]>("RotKeys",[]);var s=raw.GetOrDefault<TIntVector3<float>[]>("ScaleKeys",[]);
            // Some authored pose assets have fewer keys than their timeline declares. Only collapse exactly identical values; never stretch moving tracks.
            float[][] Constant(float[][] values)=>values.Length>1&&values.Length!=keys&&values.Skip(1).All(v=>v.SequenceEqual(values[0]))?[values[0]]:values;
            return new BoneTrack(t.GetOrDefault<FName>("Name").Text,Constant(p.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray()),Constant(r.Select(q=>new[]{q.X,q.Y,q.Z,q.W}).ToArray()),Constant(s.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray()));
        }).ToArray(),"ue5-tagged-anim-data-model"));
    }
    private static void ValidateChannel(FMovieSceneChannel<float> channel,int frames,double fps)
    {
        if(channel.StructFallback!=null||channel.Times.Length!=channel.Values.Length)throw new NotSupportedException("Unsupported movie-scene channel serialization.");
        if(channel.Times.Length==0&&!channel.bHasDefaultValue)throw new InvalidDataException("Animation channel has no keys or default value.");
        if(channel.Times.Length>1&&(channel.Times[0].Value!=0||channel.Times[^1].Value!=frames-1))throw new NotSupportedException("Channel extrapolation outside its keyed range is not supported.");
        if(channel.Times.Length>0&&(channel.TickResolution.Denominator<=0||Math.Abs((double)channel.TickResolution.Numerator/channel.TickResolution.Denominator-fps)>.001))throw new NotSupportedException("Mixed channel tick rates are not supported.");
        for(int i=0;i<channel.Times.Length;i++)
        {
            if(channel.Times[i].Value<0||channel.Times[i].Value>=frames||(i>0&&channel.Times[i].Value<=channel.Times[i-1].Value))throw new InvalidDataException("Invalid channel key time.");
            // Native source assets are densely sampled, linear curves; reject cubic/weighted evaluation for now.
            if((int)channel.Values[i].InterpMode!=0)throw new NotSupportedException("Only linear FK source curves have been validated.");
        }
    }
    private static float Evaluate(FMovieSceneChannel<float> channel,int time,float fallback)
    {
        if(channel.Times.Length==0)return channel.bHasDefaultValue?channel.DefaultValue:fallback;
        int low=0,high=channel.Times.Length-1,index=-1;
        while(low<=high){int mid=low+(high-low)/2;if(channel.Times[mid].Value==time){index=mid;break;}if(channel.Times[mid].Value<time)low=mid+1;else high=mid-1;}
        if(index<0)index=~low;
        if(index>=0)return channel.Values[index].Value;
        index=~index;if(index==0)return channel.Values[0].Value;if(index==channel.Times.Length)return channel.Values[^1].Value;
        float alpha=(float)(time-channel.Times[index-1].Value)/(channel.Times[index].Value-channel.Times[index-1].Value);
        return channel.Values[index-1].Value+(channel.Values[index].Value-channel.Values[index-1].Value)*alpha;
    }
    private static Clip Validate(Clip clip)
    {
        if(clip.Frames<1||clip.Frames>100000||!float.IsFinite(clip.Duration)||clip.Duration<=0||(clip.Frames-1)/clip.Duration>600||clip.Tracks.Length==0||clip.Tracks.Length>1024)throw new InvalidDataException("Invalid animation size, duration, or sample rate.");
        if(clip.Tracks.Select(t=>t.Name).Distinct().Count()!=clip.Tracks.Length)throw new InvalidDataException("Duplicate bone tracks.");
        foreach(var t in clip.Tracks)
        {
            if(t.Positions.Length==0||t.Rotations.Length==0)throw new InvalidDataException("Missing bone transform keys.");
            foreach(var keys in new[]{t.Positions,t.Rotations,t.Scales})if(keys.Length!=0&&keys.Length!=1&&keys.Length!=clip.Frames)throw new NotSupportedException("Unexpected raw key count.");
            if(new[]{t.Positions,t.Rotations,t.Scales}.SelectMany(a=>a).SelectMany(a=>a).Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Non-finite animation keys.");
            if(t.Rotations.Any(q=>new Quaternion(q[0],q[1],q[2],q[3]).LengthSquared()<.0001f))throw new InvalidDataException("Invalid zero quaternion.");
        }
        return clip;
    }
}
