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
            if(package.Summary.SavedByEngineVersion is not {Major:5,Minor:0})throw new NotSupportedException("Reference sampler is for tagged UE5.0 DataModels.");
            var anim=(UAnimSequence)package.ExportsLazy[index].Value;
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
