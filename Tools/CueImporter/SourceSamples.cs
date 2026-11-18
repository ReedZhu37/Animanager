using System.Text.Json;
using CUE4Parse;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace Animanager.CueImporter;

// Validation reference taken directly from the original property arrays, independently of AnimationReader and GlbWriter.
internal static class SourceSamples
{
    public static int Run(string input,string output)
    {
        if(File.Exists(output))throw new IOException("Reference already exists.");
        Globals.FatalObjectSerializationErrors=true;Globals.WarnMissingImportPackage=false;
        using var catalog=new AssetCatalog(input);var rows=new List<object>();
        foreach(var asset in catalog.Assets)
        {
            var package=AssetCatalog.Load(asset,out _);int index=Array.FindIndex(package.ExportMap,e=>e.ClassIndex.Name=="AnimSequence");
            if(index<0)continue;
            var anim=(UAnimSequence)package.ExportsLazy[index].Value;
            if(package.Summary.SavedByEngineVersion is {Major:4,Minor:23 or 27})
            {
                var names=anim.GetOrDefault<FName[]>("AnimationTrackNames",[]);
                var raw=anim.RawAnimationData??throw new InvalidDataException("Source raw tracks are missing.");
                int count4=anim.NumFrames;
                var samples4=new[]{0,(count4-1)/4,(count4-1)/2,(count4-1)*3/4,count4-1}.Distinct().Select(frame=>new
                {
                    frame,time=(double)anim.SequenceLength*frame/(count4-1),bones=raw.Select((t,i)=>
                    {
                        var p=t.PosKeys[Math.Min(frame,t.PosKeys.Length-1)];var q=t.RotKeys[Math.Min(frame,t.RotKeys.Length-1)];
                        var s=t.ScaleKeys.Length==0?new[]{1f,1f,1f}:new[]{t.ScaleKeys[Math.Min(frame,t.ScaleKeys.Length-1)].X,t.ScaleKeys[Math.Min(frame,t.ScaleKeys.Length-1)].Y,t.ScaleKeys[Math.Min(frame,t.ScaleKeys.Length-1)].Z};
                        return new{name=names[i].Text,p=new[]{p.X,p.Y,p.Z},r=new[]{q.X,q.Y,q.Z,q.W},s};
                    }).ToArray()
                }).ToArray();
                rows.Add(new{source=asset.Relative,name=Path.ChangeExtension(asset.Relative,null).Replace('\\','/').Replace("/","__"),duration=anim.SequenceLength,frames=count4,samples=samples4});
                continue;
            }
            if(package.Summary.SavedByEngineVersion is not {Major:5,Minor:0})throw new NotSupportedException("Reference sampler supports UE4.23/4.27 raw tracks and tagged UE5.0 DataModels.");
            var model=anim.GetOrDefault<FPackageIndex>("DataModel")!.Load()!;
            var count=model.GetOrDefault<int>("NumberOfKeys");var duration=model.GetOrDefault<float>("PlayLength");
            var tracks=model.GetOrDefault<FStructFallback[]>("BoneAnimationTracks",[]);
            var frames=new[]{0,(count-1)/4,(count-1)/2,(count-1)*3/4,count-1}.Distinct().ToArray();
            var samples=frames.Select(frame=>new{frame,time=(double)duration*frame/(count-1),bones=tracks.Select(t=>
            {
                var raw=t.GetOrDefault<FStructFallback>("InternalTrackData")!;
                var ps=raw.GetOrDefault<TIntVector3<float>[]>("PosKeys",[]);var qs=raw.GetOrDefault<FQuat[]>("RotKeys",[]);var ss=raw.GetOrDefault<TIntVector3<float>[]>("ScaleKeys",[]);
                var p=ps[Math.Min(frame,ps.Length-1)];var q=qs[Math.Min(frame,qs.Length-1)];var s=ss.Length==0?new TIntVector3<float>(1,1,1):ss[Math.Min(frame,ss.Length-1)];
                return new{name=t.GetOrDefault<FName>("Name").Text,p=new[]{p.X,p.Y,p.Z},r=new[]{q.X,q.Y,q.Z,q.W},s=new[]{s.X,s.Y,s.Z}};
            }).ToArray()}).ToArray();
            rows.Add(new{source=asset.Relative,name=Path.ChangeExtension(asset.Relative,null).Replace('\\','/').Replace("/","__"),duration,frames=count,samples});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{input,semantics="Original UE centimetre-space local keys; no retargeting, blending or Unreal execution.",clips=rows}));
        Console.WriteLine("SOURCE_REFERENCE "+rows.Count+" clips: "+output);return 0;
    }
}
