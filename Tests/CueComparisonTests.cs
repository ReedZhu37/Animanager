using Godot;
using Animanager.UI.Components;
using Animanager.Core.Pipeline;
using System.Text.Json;
namespace Animanager.Tests;

public static class CueComparisonTests
{
    public static async Task Compare(Node owner,Preview3DViewport preview,string folder)
    {
        var rows=new List<object>();var watch=System.Diagnostics.Stopwatch.StartNew();int failed=0;
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"conversion-report.json")));
        var reference=ProjectSettings.GlobalizePath("res://.runtime/movement-animset/ConvertedFbx");
        var mapFile=Path.Combine(folder,"reference-map.json");
        var referenceMap=File.Exists(mapFile)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(mapFile)):null;
        var bindMapFile=Path.Combine(folder,"bind-reference-map.json");
        var bindMap=File.Exists(bindMapFile)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(bindMapFile)):null;
        var clips=report.RootElement.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("status").GetString()=="converted").ToArray();
        foreach(var item in clips)
        {
            string name=item.GetProperty("name").GetString()!,error="";double maxPosition=0,maxAngle=0,maxRest=0,durationError=0;string worst="";int samples=0;
            var diagnostic=new List<object>();
            try
            {
                var stem=name.Replace("MovementAnimsetPro__","");if(stem=="Idle")stem="InPlace__Idle";
                preview.LoadFile(referenceMap!=null?referenceMap[name]:Path.Combine(reference,stem+".fbx"));preview.SetLoop(false);
                var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
                var names=Enumerable.Range(0,sk.GetBoneCount()).Select(i=>(string)sk.GetBoneName(i)).ToArray();
                var parents=Enumerable.Range(0,sk.GetBoneCount()).Select(sk.GetBoneParent).ToArray();
                var rests=Enumerable.Range(0,sk.GetBoneCount()).Select(i=>sk.GlobalTransform*sk.GetBoneGlobalRest(i)).ToArray();
                double duration=preview.Duration;
                var referenceAnimation=preview.Player!.GetAnimation(preview.Clips[0]);
                var times=Enumerable.Range(0,referenceAnimation.GetTrackCount()).SelectMany(t=>Enumerable.Range(0,referenceAnimation.TrackGetKeyCount(t)).Select(k=>referenceAnimation.TrackGetKeyTime(t,k))).Append(0).Append(duration).Distinct().Order().ToArray();
                int frames=times.Length;
                if(rows.Count<2)GD.Print("REFERENCE_TIMES "+string.Join(",",times.Take(12)));
                var poses=new Transform3D[frames][];
                for(int f=0;f<frames;f++){preview.Seek(times[f]);poses[f]=Enumerable.Range(0,sk.GetBoneCount()).Select(i=>sk.GlobalTransform*sk.GetBoneGlobalPose(i)).ToArray();}
                if(bindMap!=null)
                {
                    preview.LoadFile(bindMap[name]);
                    var bindSkeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
                    rests=names.Select(n=>bindSkeleton.GlobalTransform*bindSkeleton.GetBoneGlobalRest(bindSkeleton.FindBone(n))).ToArray();
                }
                preview.LoadFile(item.GetProperty("output").GetString()!);preview.SetLoop(false);
                sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
                var mapping=names.Select(n=>sk.FindBone(n)).ToArray();
                if(mapping.Any(i=>i<0))throw new Exception("Reference bones are missing.");
                for(int i=0;i<names.Length;i++)if(sk.GetBoneParent(mapping[i])!=(parents[i]<0?-1:mapping[parents[i]]))throw new Exception("Bone hierarchy differs: "+names[i]);
                if(item.TryGetProperty("skeletonBones",out var expectedCount)&&sk.GetBoneCount()!=expectedCount.GetInt32())throw new Exception("Full source skeleton was not preserved.");
                durationError=Math.Abs(preview.Duration-duration);
                for(int i=0;i<names.Length;i++)maxRest=Math.Max(maxRest,rests[i].Origin.DistanceTo((sk.GlobalTransform*sk.GetBoneGlobalRest(mapping[i])).Origin));
                for(int f=0;f<frames;f++)
                {
                    preview.Seek(times[f]);
                    for(int i=0;i<names.Length;i++)
                    {
                        var actual=sk.GlobalTransform*sk.GetBoneGlobalPose(mapping[i]);var expected=poses[f][i];samples++;
                        double position=actual.Origin.DistanceTo(expected.Origin);if(position>maxPosition){maxPosition=position;worst=names[i]+"@"+f;}
                        var a=actual.Basis.Orthonormalized().GetRotationQuaternion();var b=expected.Basis.Orthonormalized().GetRotationQuaternion();
                        double angle=2*Math.Acos(Math.Clamp(Math.Abs(a.Dot(b)),0,1))*180/Math.PI;maxAngle=Math.Max(maxAngle,angle);
                        if(i is 0 or 1 or 8)diagnostic.Add(new{bone=names[i],time=times[f],position,angle,expected=new[]{expected.Origin.X,expected.Origin.Y,expected.Origin.Z},actual=new[]{actual.Origin.X,actual.Origin.Y,actual.Origin.Z}});
                    }
                }
                if(maxPosition>.003||maxAngle>.5||maxRest>.0001||durationError>.001)throw new Exception("Comparison tolerance exceeded.");
            }
            catch(Exception ex){error=ex.Message;failed++;}
            rows.Add(new{name,maxPosition,maxAngle,maxRest,durationError,worst,samples,error});
            if(clips.Length<5)File.WriteAllText(Path.Combine(folder,name+"-diagnostic.json"),JsonSerializer.Serialize(diagnostic,new JsonSerializerOptions{WriteIndented=true}));
            GD.Print($"CUE_COMPARE {rows.Count}/{clips.Length} {name} pos={maxPosition:F6}m angle={maxAngle:F4}deg {error}");
            await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        File.WriteAllText(Path.Combine(folder,"comparison.json"),JsonSerializer.Serialize(new{reference,referenceMap,bindMap,elapsed=watch.Elapsed.TotalSeconds,failed,clips=rows},new JsonSerializerOptions{WriteIndented=true}));
        owner.GetTree().Quit(failed==0?0:1);
    }
    public static void Prepare(Node owner,Preview3DViewport preview)
    {
        var folder=ProjectSettings.GlobalizePath("res://.runtime/cue-baseline");Directory.CreateDirectory(folder);
        var baseline=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--cue-baseline="))?[15..]??"UE4_Mannequin";
        preview.LoadFile(ProjectSettings.GlobalizePath("res://BuiltinAssets/Baselines/"+baseline+".fbx"));
        var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
        float[] Vec(Vector3 v)=>[v.X,v.Y,v.Z];
        float[] Quat(Quaternion q)=>[q.X,q.Y,q.Z,q.W];
        var bones=Enumerable.Range(0,skeleton.GetBoneCount()).Select(i=>new{name=(string)skeleton.GetBoneName(i),parent=skeleton.GetBoneParent(i),p=Vec(skeleton.GetBoneRest(i).Origin),r=Quat(skeleton.GetBoneRest(i).Basis.GetRotationQuaternion()),s=Vec(skeleton.GetBoneRest(i).Basis.Scale),world=Vec(skeleton.GlobalTransform*skeleton.GetBoneGlobalRest(i).Origin)});
        File.WriteAllText(Path.Combine(folder,baseline+"-godot-rig.json"),JsonSerializer.Serialize(bones,new JsonSerializerOptions{WriteIndented=true}));
        ExportService.Export(preview,folder,baseline,false);
        GD.Print("CUE_BASELINE_READY "+folder);owner.GetTree().Quit();
    }
}
