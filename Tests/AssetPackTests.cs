using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;
using System.Text.Json;
using System.Diagnostics;

namespace Animanager.Tests;

/// <summary>Real external-pack acceptance: import via Limbo, decode every clip, sample poses and round-trip motion.</summary>
public static class AssetPackTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands,string folder)
    {
        var reportFolder=Path.Combine(api.Storage.Root,"pack-validation");Directory.CreateDirectory(reportFolder);
        var rows=new List<ClipResult>();var checks=new List<string>();var clock=Stopwatch.StartNew();string failure="";
        var console=owner.GetNode("/root/LimboConsole");
        async Task Execute(string command){console.Call("execute_command",command);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new IOException(commands.LastResult);}
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks.Add(message);GD.Print("PASS "+message);}
        void Save()=>File.WriteAllText(Path.Combine(reportFolder,"results.json"),JsonSerializer.Serialize(new{source=folder,library=api.Storage.Root,elapsedSeconds=clock.Elapsed.TotalSeconds,checks,error=failure,clips=rows},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            var files=Directory.GetFiles(folder,"*.fbx");Check(files.Length>0,"conversion produced FBX inputs");
            using var conversion=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"conversion-report.json")));
            var metadata=conversion.RootElement.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("status").GetString()=="exported")
                .ToDictionary(i=>Path.GetFileNameWithoutExtension(i.GetProperty("output").GetString()!),i=>i.Clone());
            var expected=files.Length;
            await Execute($"batch import \"{folder.Replace('\\','/')}\"");
            var imported=api.Jobs.Get(api.Jobs.LatestId!);
            Check(imported.Failed==0&&imported.Succeeded+imported.Skipped==expected,"all converted files enter the app through the Limbo batch queue");
            var items=api.Database.SearchAll().Where(a=>metadata.ContainsKey(a.Name)).OrderBy(a=>a.Name).ToArray();
            Check(items.Length==expected,"library count matches converted input count");
            foreach(var asset in items)
            {
                var row=new ClipResult{Name=asset.Name};rows.Add(row);
                try
                {
                    await Execute("preview load "+asset.Id);
                    row.Bones=preview.BoneCount;row.Meshes=preview.MeshCount;row.Clips=preview.Clips;row.Duration=preview.Duration;
                    row.SourceDuration=metadata[asset.Name].GetProperty("duration").GetDouble();
                    var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
                    var root=skeleton.FindBone("root");
                    preview.SetLoop(false);preview.Seek(0);
                    var initial=Enumerable.Range(0,skeleton.GetBoneCount()).Select(skeleton.GetBonePose).ToArray();
                    var rootInitial=root>=0?skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin:Vector3.Zero;
                    var maxRoot=0f;bool animated=false;
                    foreach(var fraction in new[]{.25,.5,.75,1.0})
                    {
                        preview.Seek(preview.Duration*fraction);
                        animated|=Enumerable.Range(0,skeleton.GetBoneCount()).Any(i=>!skeleton.GetBonePose(i).IsEqualApprox(initial[i]));
                        if(root>=0)maxRoot=Math.Max(maxRoot,(skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin).DistanceTo(rootInitial));
                    }
                    row.Animated=animated;row.RootTravel=maxRoot;
                    row.Height=Preview3DViewport.CalculateWorldBounds(preview.Model!)?.Size.Y??0;
                    if(row.Bones<50||row.Meshes==0||row.Clips.Length==0||Math.Abs(row.Duration-row.SourceDuration)>.08||!float.IsFinite(row.Height)||row.Height<=0)
                        throw new Exception($"Invalid preview: bones={row.Bones}, meshes={row.Meshes}, duration={row.Duration}/{row.SourceDuration}, height={row.Height}");
                    api.SetTags(asset.Id,"MovementAnimsetPro "+asset.Name.Split("__")[0]);
                    row.Valid=true;
                }
                catch(Exception ex){row.Error=ex.Message;GD.PushWarning("PACK CLIP FAILED "+asset.Name+": "+ex.Message);}
                // Let queued model frees run between files; large packs must not retain every scene.
                await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
                if(rows.Count%20==0){GD.Print($"PACK_PROGRESS {rows.Count}/{expected}");Save();}
            }
            Check(rows.All(r=>r.Valid),"every imported FBX decodes with a mesh, skeleton and matching clip duration");
            Check(api.Search("MovementAnimsetPro","Animation").Total==expected,"all validated clips are searchable in the animation category");
            var inPlace=rows.FirstOrDefault(r=>r.Name=="InPlace__WalkFwdLoop");var rootMotion=rows.FirstOrDefault(r=>r.Name=="RootMotion__WalkFwdLoop");
            if(inPlace!=null&&rootMotion!=null)
            {
                Check(inPlace.Animated&&rootMotion.Animated,"walking samples animate real bone transforms");
                Check(rootMotion.RootTravel>.2f&&inPlace.RootTravel<.1f,"root-motion walking translates while in-place walking stays near its origin");
            }
            await Execute($"batch import \"{folder.Replace('\\','/')}\"");
            var repeated=api.Jobs.Get(api.Jobs.LatestId!);Check(repeated.Skipped==expected&&repeated.Succeeded==0&&repeated.Failed==0,"reimport skips the entire pack without duplicates");
            var samples=new[]{"InPlace__WalkFwdLoop","RootMotion__WalkFwdLoop","AimOffsets__Idle_U"};
            foreach(var name in samples.Where(n=>rows.Any(r=>r.Name==n)))
            {
                var source=rows.Single(r=>r.Name==name);var destination=Path.Combine(reportFolder,"roundtrip");
                await Execute($"export fbx {name} \"{destination.Replace('\\','/')}\"");
                var job=api.Jobs.Get(api.Jobs.LatestId!);Check(job.Failed==0&&job.Succeeded==1,"FBX export through the app queue: "+name);
                preview.LoadFile(job.Items[0].Output);preview.SetLoop(false);
                Check(preview.BoneCount==source.Bones&&preview.Clips.Length>0&&Math.Abs(preview.Duration-source.Duration)<.08,"FBX round trip retains skeleton and duration: "+name);
                if(name.StartsWith("RootMotion"))
                {
                    var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();var root=skeleton.FindBone("root");preview.Seek(0);var start=skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin;
                    preview.Seek(preview.Duration);var travel=(skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(root).Origin).DistanceTo(start);
                    GD.Print($"ROOT_MOTION_ROUNDTRIP source={source.RootTravel} output={travel} skeletonScale={skeleton.GlobalTransform.Basis.Scale}");
                    Check(Math.Abs(travel-source.RootTravel)<.05,"FBX round trip preserves root-motion distance");
                }
            }
            Save();GD.Print($"PACK_COMPLETE {expected} clips, {checks.Count} checks, {clock.Elapsed.TotalSeconds:F1}s; report: {reportFolder}");owner.GetTree().Quit(0);
        }
        catch(Exception ex){failure=ex.ToString();Save();GD.PushError("ASSET PACK FAILED: "+failure);owner.GetTree().Quit(1);}
    }
    private sealed class ClipResult
    {
        public string Name{get;set;}="";public int Bones{get;set;}public int Meshes{get;set;}public string[] Clips{get;set;}=[];
        public double Duration{get;set;}public double SourceDuration{get;set;}public bool Animated{get;set;}public float RootTravel{get;set;}public float Height{get;set;}
        public bool Valid{get;set;}public string Error{get;set;}="";
    }
}
