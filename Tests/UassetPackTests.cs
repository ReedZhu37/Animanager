using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using System.Text.Json;

namespace Animanager.Tests;

public static class UassetPackTests
{
    public static async Task Render(Animanager.UI.MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var output=Path.Combine(api.Storage.Root,"pack-validation");Directory.CreateDirectory(output);
        try
        {
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);owner.Workspace.ShowPanel("bones");
            var assets=api.Database.SearchAll();
            var chosen=new[]{assets.First(a=>a.Name.EndsWith("__AS_Idle_Seq")),assets.First(a=>a.Name.EndsWith("__AS_Combo_Attack_01_All_Seq")),assets.First(a=>a.Name.Contains("__07_Roll__"))};
            for(int i=0;i<chosen.Length;i++)
            {
                commands.Load(chosen[i].Id);await commands.PendingTask;preview.SetLoop(false);preview.Seek(0);preview.TogglePlay();
                for(int f=0;f<20;f++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
                if(preview.Playhead<=0)throw new Exception("Playback did not advance");preview.Seek(preview.Duration*.4);
                preview.Bones.Select("hand_r");var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
                preview.FocusPoint(sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone("pelvis")).Origin+Vector3.Up*.15f);
                for(int f=0;f<12;f++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
                await owner.ToSignal(owner.GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
                preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var screenshot=owner.GetViewport().GetTexture().GetImage();screenshot.SavePng(Path.Combine(output,new[]{"idle.png","attack.png","roll.png"}[i]));
            }
            File.WriteAllText(Path.Combine(output,"render-results.txt"),"PASS: actual playback and screenshots for Nodachi idle, attack and roll; 162-bone skeleton; graphical Godot runtime.");
            owner.GetTree().Quit();
        }
        catch(Exception ex){GD.PushError(ex.ToString());owner.GetTree().Quit(1);}
    }
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands,string folder,string referenceFile)
    {
        var output=Path.Combine(api.Storage.Root,"pack-validation");Directory.CreateDirectory(output);
        var checks=new List<string>();var rows=new List<ClipResult>();string failure="";var watch=System.Diagnostics.Stopwatch.StartNew();
        var console=owner.GetNode("/root/LimboConsole");
        async Task Execute(string text){console.Call("execute_command",text);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new IOException(commands.LastResult);}
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);GD.Print("PASS "+text);}
        void Save()=>File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{source=folder,referenceFile,editor=OS.HasFeature("editor"),elapsed=watch.Elapsed.TotalSeconds,checks,error=failure,clips=rows},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            if(OS.GetCmdlineUserArgs().Contains("--uasset-pack-fbx-only"))
            {
                var previous=File.ReadAllText(Path.Combine(output,"results.json"));using var saved=JsonDocument.Parse(previous);
                if(saved.RootElement.GetProperty("source").GetString()!=folder)throw new Exception("Resume input differs");
                rows.AddRange(JsonSerializer.Deserialize<List<ClipResult>>(saved.RootElement.GetProperty("clips").GetRawText())!);
                var passed=saved.RootElement.GetProperty("checks").EnumerateArray().Select(c=>c.GetString()!).ToArray();
                if(rows.Count==0||rows.Any(r=>!r.Valid)||!passed.Any(c=>c.StartsWith("whole-pack reimport")))throw new Exception("Source validation must have passed before retrying FBX only");
                File.WriteAllText(Path.Combine(output,"results-before-fbx-fix.json"),previous);
                checks.AddRange(passed.TakeWhile(c=>!c.StartsWith("FBX")));
            }
            else
            {
            using var source=JsonDocument.Parse(File.ReadAllText(referenceFile));
            var references=source.RootElement.GetProperty("clips").EnumerateArray().ToDictionary(i=>i.GetProperty("source").GetString()!,i=>i.Clone());
            await Execute($"uasset import \"{folder.Replace('\\','/')}\"");var job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Failed==0,"source directory imports through the actual Limbo job queue");
            using var report=JsonDocument.Parse(File.ReadAllText(job.Items[0].Output));
            var converted=report.RootElement.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("status").GetString()=="converted").ToArray();
            Check(converted.Length==references.Count,"every source animation has a converted result");
            foreach(var item in converted)
            {
                var row=new ClipResult{Source=item.GetProperty("source").GetString()!};rows.Add(row);
                try
                {
                    var asset=api.Database.FindByHash(item.GetProperty("outputSha256").GetString()!)??throw new Exception("Asset missing from database");
                    row.Name=asset.Name;row.Id=asset.Id;var reference=references[row.Source];
                    preview.LoadFile(await api.Storage.AcquireLocalFileAsync(asset.VirtualPath));preview.SetLoop(false);
                    row.Bones=preview.BoneCount;row.Duration=preview.Duration;
                    if(row.Bones!=item.GetProperty("skeletonBones").GetInt32()||preview.MeshCount<1||preview.Clips.Length!=1||Math.Abs(preview.Duration-reference.GetProperty("duration").GetDouble())>.001)throw new Exception("Preview skeleton, mesh or timing mismatch");
                    var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();var root=skeleton.FindBone("root");preview.Seek(0);
                    var start=skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin;
                    foreach(var sample in reference.GetProperty("samples").EnumerateArray())
                    {
                        preview.Seek(sample.GetProperty("time").GetDouble());
                        row.RootTravel=Math.Max(row.RootTravel,(skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin).DistanceTo(start));
                        foreach(var bone in sample.GetProperty("bones").EnumerateArray())
                        {
                            string name=bone.GetProperty("name").GetString()!;int index=skeleton.FindBone(name);if(index<0)throw new Exception("Missing source bone "+name);
                            float[] Vector(string field)=>bone.GetProperty(field).EnumerateArray().Select(v=>v.GetSingle()).ToArray();
                            var p=Vector("p");var r=Vector("r");var s=Vector("s");
                            var pose=skeleton.GetBonePose(index);var expectedPosition=new Vector3(p[0],-p[1],p[2])*.01f;
                            var expectedRotation=new Quaternion(-r[0],r[1],-r[2],r[3]).Normalized();var rotation=pose.Basis.Orthonormalized().GetRotationQuaternion();
                            if(!pose.IsFinite())throw new Exception("Non-finite pose");
                            row.MaxPosition=Math.Max(row.MaxPosition,pose.Origin.DistanceTo(expectedPosition));
                            row.MaxAngle=Math.Max(row.MaxAngle,2*Math.Acos(Math.Clamp(Math.Abs(rotation.Dot(expectedRotation)),0,1))*180/Math.PI);
                            row.MaxScale=Math.Max(row.MaxScale,pose.Basis.Scale.DistanceTo(new Vector3(s[0],s[1],s[2])));row.Samples++;
                        }
                    }
                    if(row.MaxPosition>.0001||row.MaxAngle>.2||row.MaxScale>.0001)throw new Exception("Source key comparison tolerance exceeded");
                    api.SetTags(asset.Id,asset.Tags+" Nodachi");row.Valid=true;
                }
                catch(Exception ex){row.Error=ex.Message;GD.PushWarning(row.Source+": "+ex.Message);}
                await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
                if(rows.Count%25==0){Save();GD.Print($"UASSET_PACK_PROGRESS {rows.Count}/{converted.Length}");}
            }
            Check(rows.All(r=>r.Valid),"all imported animations preserve sampled original local keys, full skeletons and duration");
            await Execute($"uasset import \"{folder.Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.ImportedAssets)==0&&job.Items.Sum(i=>i.DuplicateAssets)==converted.Length,"whole-pack reimport deduplicates all converted animations");
            }
            foreach(var row in new[]{rows.First(),rows.First(r=>r.Source.Contains("Combo_Attack_01_All")),rows.MaxBy(r=>r.RootTravel)!}.Distinct())
            {
                var asset=api.Find(row.Id);preview.LoadFile(await api.Storage.AcquireLocalFileAsync(asset.VirtualPath));preview.SetLoop(false);
                var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();var names=Enumerable.Range(0,sk.GetBoneCount()).Select(i=>(string)sk.GetBoneName(i)).ToArray();
                var times=new[]{0d,preview.Duration*.5,preview.Duration};var poses=new List<Transform3D[]>();
                foreach(var time in times){preview.Seek(time);poses.Add(names.Select(n=>sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone(n))).ToArray());}
                await Execute($"export fbx {row.Id} \"{(output+"/roundtrip").Replace('\\','/')}\"");var job=api.Jobs.Get(api.Jobs.LatestId!);
                Check(job.State==JobState.Completed,"FBX export: "+row.Name);
                preview.LoadFile(job.Items[0].Output);preview.SetLoop(false);sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
                double position=0,angle=0;
                for(int t=0;t<times.Length;t++){preview.Seek(times[t]);for(int b=0;b<names.Length;b++){
                    var actual=sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone(names[b]));var expected=poses[t][b];position=Math.Max(position,actual.Origin.DistanceTo(expected.Origin));
                    angle=Math.Max(angle,2*Math.Acos(Math.Clamp(Math.Abs(actual.Basis.Orthonormalized().GetRotationQuaternion().Dot(expected.Basis.Orthonormalized().GetRotationQuaternion())),0,1))*180/Math.PI);
                }}
                Check(sk.GetBoneCount()==row.Bones&&position<.003&&angle<.5,$"FBX round trip: {row.Name} / {position:F7} m / {angle:F4} deg");
            }
            Save();GD.Print($"UASSET_PACK_COMPLETE {rows.Count} clips, {checks.Count} checks");owner.GetTree().Quit();
        }
        catch(Exception ex){failure=ex.ToString();Save();GD.PushError(failure);owner.GetTree().Quit(1);}
    }
    private sealed class ClipResult
    {
        public string Source{get;set;}="";public string Name{get;set;}="";public string Id{get;set;}="";public int Bones{get;set;}public double Duration{get;set;}public int Samples{get;set;}
        public double MaxPosition{get;set;}public double MaxAngle{get;set;}public double MaxScale{get;set;}public float RootTravel{get;set;}public bool Valid{get;set;}public string Error{get;set;}="";
    }
}
