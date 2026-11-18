using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using System.Text.Json;

namespace Animanager.Tests;

public static class StandardUassetTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands,string fixture)
    {
        var checks=new List<string>();var console=owner.GetNode("/root/LimboConsole");
        void Check(bool ok,string description){if(!ok)throw new Exception(description);checks.Add(description);GD.Print("PASS "+description);}
        async Task Execute(string command){console.Call("execute_command",command);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        void Save(string error)=>File.WriteAllText(Path.Combine(api.Storage.Root,"standard-uasset-tests.json"),JsonSerializer.Serialize(new{fixture,editor=OS.HasFeature("editor"),checks,error},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            var count=api.Search().Total;
            await Execute("uasset status");Check(commands.LastResult.Contains("UE 5.6")&&commands.LastResult.Contains("No Unreal installation required"),"Console advertises the validated independent UE5 reader");
            await Execute($"uasset inspect \"{fixture.Replace('\\','/')}\"");
            Check(commands.LastResult.Contains("2 supported, 0 unsupported, 0 failed")&&commands.LastResult.Contains("ue4-mannequin=1")&&commands.LastResult.Contains("ue5-mannequin=1"),"Mixed UE4 / UE5 source inspection identifies both families");
            Check(api.Search().Total==count,"inspection leaves the library unchanged");
            await Execute($"uasset import \"{fixture.Replace('\\','/')}\"");var job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.ImportedAssets)==2,"Limbo imports both families in one queued directory job");
            var report=JsonDocument.Parse(File.ReadAllText(job.Items[0].Output));
            var items=report.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var ue5=items.Single(i=>i.GetProperty("profile").GetString()=="ue5-mannequin");var name=ue5.GetProperty("name").GetString()!;
            Check(!ue5.GetProperty("dependencySubstituted").GetBoolean()&&ue5.GetProperty("skeletonBones").GetInt32()==161&&ue5.GetProperty("previewBones").GetInt32()==89,"UE5 source Skeleton is validated and distinguished from preview joints");
            Check(items.Single(i=>i.GetProperty("profile").GetString()=="ue4-mannequin").GetProperty("dependencySubstituted").GetBoolean(),"missing UE4 dependency substitution is explicit in the report");
            Check(api.Find(name).Tags.Contains("UE5_Mannequin")&&api.Find("UE4_WalkFwdLoop").Tags.Contains("UE4_Mannequin"),"library tags retain each source skeleton family");
            await Execute("preview load "+name);preview.SetLoop(false);
            Check(preview.BoneCount==161&&preview.MeshCount==1&&preview.Clips.Length==1&&Math.Abs(preview.Duration-7.5666666)<.001,"UE5 preview retains all 161 bones and the complete source clip");
            var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
            var names=Enumerable.Range(0,sk.GetBoneCount()).Select(i=>(string)sk.GetBoneName(i)).ToArray();
            var times=new[]{0d,1.25,3.5,preview.Duration};var poses=new List<Transform3D[]>();
            foreach(var time in times){preview.Seek(time);poses.Add(names.Select(n=>sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone(n))).ToArray());}
            Check(poses[0][sk.FindBone("head")].Origin.DistanceTo(poses[2][sk.FindBone("head")].Origin)>.001,"UE5 source curves produce a changing pose");
            await Execute("bone select spine_05");await Execute("trail show true");await preview.Bones.PendingSampling;
            Check(preview.Bones.Trajectory!=null&&preview.Bones.Status=="Ready","UE5 skeleton is available to the shared bone and trajectory API");
            await Execute($"uasset import \"{fixture.Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.DuplicateAssets)==2&&api.Search().Total==count+2,"repeated mixed import deduplicates both families");
            await ((Animanager.UI.MainShell)owner).Import([fixture],true,autoCharacters:false);job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.DuplicateAssets)==2,"App directory import uses the same automatic profile pipeline");
            if(api.Blender.Available)
            {
                await Execute($"export fbx {name} \"{Path.Combine(api.Storage.Root,"roundtrip").Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);
                Check(job.State==JobState.Completed,"UE5 animation exports through the existing FBX queue");
                preview.LoadFile(job.Items[0].Output);preview.SetLoop(false);sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
                Check(sk.GetBoneCount()==161&&names.All(n=>sk.FindBone(n)>=0),"FBX export preserves the full UE5 skeleton, including unweighted bones");
                double maxPosition=0,maxAngle=0;
                for(int t=0;t<times.Length;t++)
                {
                    preview.Seek(times[t]);for(int b=0;b<names.Length;b++)
                    {
                        var actual=sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone(names[b]));var expected=poses[t][b];
                        maxPosition=Math.Max(maxPosition,actual.Origin.DistanceTo(expected.Origin));
                        var a=actual.Basis.Orthonormalized().GetRotationQuaternion();var e=expected.Basis.Orthonormalized().GetRotationQuaternion();
                        maxAngle=Math.Max(maxAngle,2*Math.Acos(Math.Clamp(Math.Abs(a.Dot(e)),0,1))*180/Math.PI);
                    }
                }
                Check(maxPosition<.003&&maxAngle<.5,$"UE5 FBX round trip preserves sampled poses: {maxPosition:F7} m, {maxAngle:F4} deg");
            }
            Save("");GD.Print("STANDARD_UASSET_TEST_COMPLETE "+checks.Count);owner.GetTree().Quit();
        }
        catch(Exception ex){Save(ex.ToString());GD.PushError(ex.ToString());owner.GetTree().Quit(1);}
    }
}
