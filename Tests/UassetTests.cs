using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using System.IO.Compression;
using System.Text.Json;

namespace Animanager.Tests;

public static class UassetTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands,string zip)
    {
        var checks=new List<string>();var console=owner.GetNode("/root/LimboConsole");
        var report=Path.Combine(api.Storage.Root,"uasset-tests.json");
        void Check(bool ok,string description){if(!ok)throw new Exception(description);checks.Add(description);GD.Print("PASS "+description);}
        async Task Execute(string command){console.Call("execute_command",command);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        void Save(string error)=>File.WriteAllText(report,JsonSerializer.Serialize(new{source=zip,editor=OS.HasFeature("editor"),checks,error},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            Check(api.Uasset.Available,"self-contained CUE4Parse helper is present");
            await Execute("uasset status");Check(commands.LastResult.Contains("No Unreal installation required"),"Limbo reports the independent backend and explicit baseline");
            var count=api.Search().Total;
            await Execute($"uasset inspect \"{zip.Replace('\\','/')}\"");
            Check(commands.LastResult.Contains("386 supported, 6 unsupported, 0 failed"),"ZIP inspection recognizes 386 AnimSequence and 6 unsupported Montage");
            Check(api.Search().Total==count,"inspection does not import assets");
            await Execute($"uasset import \"{zip.Replace('\\','/')}\"");
            var job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.ImportedAssets)==386&&job.Items.Sum(i=>i.UnsupportedAssets)==6,"Limbo imports the original ZIP through the durable batch queue");
            Check(api.Search().Total==count+386&&api.Search("uasset","Animation").Total==386,"all 386 clips are indexed as animations with provenance tags");
            Check(File.Exists(job.Items[0].Output)&&File.Exists(Path.Combine(Path.GetDirectoryName(job.Items[0].Output)!,"import-report.json")),"source hashes, skipped classes and library mappings are retained");
            foreach(var name in new[]{"MovementAnimsetPro__RootMotion__WalkFwdLoop","MovementAnimsetPro__InPlace__WalkFwdLoop","MovementAnimsetPro__AimOffsets__Idle_U"})
            {
                await Execute("preview load "+name);preview.SetLoop(false);
                Check(preview.BoneCount==68&&preview.MeshCount==1&&preview.Clips.Length==1,"preview loads "+name);
                var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();var root=sk.FindBone("root");preview.Seek(0);var start=sk.GlobalTransform*sk.GetBoneGlobalPose(root).Origin;
                preview.Seek(preview.Duration);var travel=(sk.GlobalTransform*sk.GetBoneGlobalPose(root).Origin).DistanceTo(start);
                Check(name.Contains("__RootMotion__")?Math.Abs(travel-1.7416954)<.001:travel<.001,"root displacement is preserved: "+name);
            }
            await Execute($"batch import \"{zip.Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);
            Check(job.State==JobState.Completed&&job.Items.Sum(i=>i.DuplicateAssets)==386&&api.Search().Total==count+386,"standard batch import routes ZIPs and skips all duplicates");
            var fixtures=Path.Combine(api.Storage.Root,"fixtures");Directory.CreateDirectory(fixtures);
            var good=Path.Combine(fixtures,"WalkFwdLoop.uasset");var bad=Path.Combine(fixtures,"broken.uasset");
            using(var archive=ZipFile.OpenRead(zip)){var entry=archive.Entries.Single(e=>e.FullName.EndsWith("/RootMotion/WalkFwdLoop.uasset"));entry.ExtractToFile(good);}
            File.WriteAllBytes(bad,[0,1,2,3]);
            var id=api.Jobs.Enqueue(api.Planner.ImportUasset([bad,good]));job=await api.Jobs.WaitAsync(id);
            Check(job.State==JobState.CompletedWithErrors&&job.Failed==1&&job.Skipped==1,"malformed input fails visibly while later valid items still complete");
            File.Copy(good,bad,true);var retry=api.Jobs.Retry(id);job=await api.Jobs.WaitAsync(retry);
            Check(job.Total==1&&job.State==JobState.Completed&&job.Skipped==1,"retry processes only the failed item and deduplicates repaired input");
            var cancel=api.Jobs.Enqueue(api.Planner.ImportUasset([zip]));
            for(int i=0;i<100&&api.Jobs.Get(cancel).State==JobState.Queued;i++)await Task.Delay(10);
            Check(api.Jobs.Get(cancel).State==JobState.Running,"cancellation test reaches a running converter");
            api.Jobs.Cancel(cancel);job=await api.Jobs.WaitAsync(cancel);
            Check(job.State==JobState.Cancelled&&api.Search().Total==count+386,"cancellation stops the helper without adding partial assets");
            // Exercise the actual GUI handler, including its ZIP routing and job status rendering.
            var method=owner.GetType().GetMethod("Import",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(owner,[new[]{good},false])!;
            Check(api.Jobs.Get(api.Jobs.LatestId!).State==JobState.Completed,"GUI import uses the same tested independent backend");
            var unsafeZip=Path.Combine(fixtures,"unsafe.zip");
            using(var archive=ZipFile.Open(unsafeZip,ZipArchiveMode.Create)){using var entry=archive.CreateEntry("../escape.uasset").Open();entry.Write([0,1,2,3]);}
            bool rejected=false;try{await api.Uasset.ConvertAsync(unsafeZip,Path.Combine(fixtures,"unsafe-output"),false,null,default);}catch(IOException){rejected=true;}
            Check(rejected&&!File.Exists(Path.Combine(api.Storage.Root,"escape.uasset")),"ZIP traversal entries are rejected without extraction");
            if(api.Blender.Available)
            {
                await Execute($"export fbx MovementAnimsetPro__RootMotion__WalkFwdLoop \"{Path.Combine(api.Storage.Root,"roundtrip").Replace('\\','/')}\"");
                job=api.Jobs.Get(api.Jobs.LatestId!);Check(job.State==JobState.Completed,"CUE animation can use the existing FBX export queue");
                preview.LoadFile(job.Items[0].Output);preview.SetLoop(false);var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();var root=sk.FindBone("root");preview.Seek(0);var start=sk.GlobalTransform*sk.GetBoneGlobalPose(root).Origin;preview.Seek(preview.Duration);
                Check(preview.BoneCount==68&&Math.Abs((sk.GlobalTransform*sk.GetBoneGlobalPose(root).Origin).DistanceTo(start)-1.7416954)<.001,"CUE to FBX round trip preserves root motion and all 68 bones");
            }
            Save("");GD.Print("UASSET_TEST_COMPLETE "+checks.Count);owner.GetTree().Quit(0);
        }
        catch(Exception ex){Save(ex.ToString());GD.PushError(ex.ToString());owner.GetTree().Quit(1);}
    }
}
