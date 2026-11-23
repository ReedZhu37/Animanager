using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Models;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Animanager.UI.Components;
using System.Text.Json;

namespace Animanager.Tests;

/// <summary>Real Limbo, filesystem, SQLite and Blender batch acceptance tests in an isolated library.</summary>
public static class BatchTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var root=ProjectSettings.GlobalizePath("res://TestResults/batch-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);var checks=new List<string>();
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks.Add(message);GD.Print("PASS "+message);}
        string Sample(string name)=>ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_"+name+".glb");
        string Q(string path)=>"\""+path.Replace('\\','/')+"\"";
        var console=owner.GetNode("/root/LimboConsole");
        async Task Execute(string command,bool wait=true)
        {
            console.Call("execute_command",command);
            if(wait)await commands.PendingTask.WaitAsync(TimeSpan.FromMinutes(3));
            if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);
        }
        async Task<JobRecord> Done(string id)=>await api.Jobs.WaitAsync(id).WaitAsync(TimeSpan.FromMinutes(3));
        try
        {
            var inputs=Path.Combine(root,"中文 输入");Directory.CreateDirectory(Path.Combine(inputs,"nested"));
            var walk=Path.Combine(inputs,"Walk 中文.glb");File.Copy(Sample("Walk"),walk);
            File.Copy(Sample("Idle"),Path.Combine(inputs,"nested","Idle.glb"));
            File.WriteAllText(Path.Combine(inputs,"notes.txt"),"Ignored auxiliary file");
            var missing=Path.Combine(root,"Repaired.glb");
            var manifest=Path.Combine(root,"imports.json");
            File.WriteAllText(manifest,JsonSerializer.Serialize(new[]{"中文 输入","中文 输入/Walk 中文.glb","Repaired.glb"}));
            await Execute("batch scan "+Q(inputs));
            Check(api.Search().Total==0&&commands.LastResult.Contains("2 items"),"scan is a dry run and finds nested inputs");
            var planned=await api.Planner.ImportManifestAsync(manifest);
            Check(planned.Items.Count==3&&planned.IgnoredFiles==1,"manifest paths resolve relative to JSON, overlapping inputs deduplicate");
            await Execute("batch import_list "+Q(manifest));
            var imported=await Done(api.Jobs.LatestId!);
            Check(imported.State==JobState.CompletedWithErrors&&imported.Succeeded==2&&imported.Failed==1,"batch import retains successes and reports missing files");
            File.Copy(Sample("Run"),missing);
            await Execute("job retry "+imported.Id);
            var importRetry=await Done(api.Jobs.LatestId!);
            Check(importRetry.Total==1&&importRetry.Succeeded==1&&importRetry.RetryOf==imported.Id,"retry imports only the repaired failed item");
            await Execute("batch import "+Q(inputs));
            Check(api.Jobs.Get(api.Jobs.LatestId!).Skipped==2&&api.Search().Total==3,"repeated import reports SHA-256 skips without duplicates");
            await Execute("asset favorite \"Walk 中文\" true");
            Check(api.Planner.Export("*",Path.Combine(root,"favorites"),true).Items.Count==1,"favorite export selects only favorites");

            var output=Path.Combine(root,"FBX 输出");
            await Execute("export plan \"*\" "+Q(output));
            Check(!Directory.Exists(output),"export planning creates no destination files or directory");
            var exportPlan=api.Planner.Export("*",output);
            Check(api.Planner.Load(exportPlan.Id).Items.Select(i=>i.AssetId).SequenceEqual(exportPlan.Items.Select(i=>i.AssetId)),"saved export plan preserves the selection and order");
            // A broken source between two valid files proves one failure does not abort subsequent conversions.
            var bad=Path.Combine(root,"Broken.glb");File.WriteAllText(bad,"not a GLB");
            Check((await api.ImportAsync([bad])).Errors.Count==1,"unreadable GLB reports an import error");
            const string legacyPath="assets/legacy-broken/Broken.glb";
            await api.Storage.PutFileAsync(bad,legacyPath);
            api.Database.Insert(new(){Name="Broken",SourceFormat=".glb",VirtualPath=legacyPath,FileHash="legacy-broken"});
            var a=api.Find("Walk 中文");var b=api.Find("Broken");var c=api.Find("Idle");
            PlanItem Item(AssetItem asset)=>new(){AssetId=asset.Id,Name=asset.Name,Input=asset.VirtualPath};
            var mixed=new BatchPlan{Kind=JobKind.ExportFbx,OutputFolder=output,Items=[Item(a),Item(b),Item(c)]};api.Planner.Save(mixed);
            Directory.CreateDirectory(output);var sentinel=Path.Combine(output,"Walk 中文.fbx");File.WriteAllText(sentinel,"KEEP EXISTING FILE");
            await Execute("export run "+mixed.Id,false);var mixedId=api.Jobs.LatestId!;
            await Execute("export fbx Repaired "+Q(output),false);var secondId=api.Jobs.LatestId!;
            var cancelFolder=Path.Combine(root,"never-exported");
            await Execute("batch export_favorites "+Q(cancelFolder),false);var cancelledId=api.Jobs.LatestId!;
            await Execute("job cancel_id "+cancelledId,false);
            Check(api.Jobs.Get(cancelledId).State==JobState.Cancelled&&!Directory.Exists(cancelFolder),"queued cancellation prevents any conversion");
            await Execute("job status "+mixedId,false);
            Check(commands.LastResult.Contains(mixedId),"job status remains usable while exports are queued");
            var mixedResult=await Done(mixedId);var second=await Done(secondId);
            Check(mixedResult.State==JobState.CompletedWithErrors&&mixedResult.Succeeded==2&&mixedResult.Failed==1,"bad FBX conversion does not stop later files in the batch");
            Check(second.State==JobState.Completed&&second.StartedUtc>=mixedResult.FinishedUtc,"queued jobs run sequentially and continue after a failed batch item");
            Check(File.ReadAllText(sentinel)=="KEEP EXISTING FILE"&&File.Exists(Path.Combine(output,"Walk 中文_2.fbx")),"existing outputs are preserved and collisions numbered");
            Check(mixedResult.Items.All(i=>File.Exists(i.Log))&&File.ReadAllText(mixedResult.Items[1].Log).Contains("FAILED:"),"each item retains a log including failed Blender output");
            Check(File.ReadAllText(mixedResult.ReportPath).Contains("CompletedWithErrors"),"final JSON report persists mixed results");
            preview.LoadFile(mixedResult.Items[0].Output);
            Check(preview.BoneCount>=16&&preview.Clips.Length>0&&preview.Duration>0,"batch FBX result reloads with skeleton and animation");
            Check(!Directory.EnumerateFileSystemEntries(Path.Combine(output,".animanager-staging")).Any(),"successful and failed conversions clean their private staging directories");
            // Repair the isolated library fixture, never the user's source or production library.
            File.Copy(Sample("Rig"),api.Storage.Resolve(b.VirtualPath),true);
            await Execute("job retry "+mixedId);
            var retried=await Done(api.Jobs.LatestId!);
            Check(retried.Total==1&&retried.Succeeded==1&&retried.Items[0].Source.AssetId==b.Id,"FBX retry exports only the failed asset");
            await Execute("job report last");
            Check(JsonSerializer.Deserialize<JobRecord>(commands.LastResult)!.Items[0].Output==retried.Items[0].Output,"Limbo exposes full persisted output paths in JSON reports");
            Check(console.Get("_output").As<RichTextLabel>().GetParsedText().Contains("\"Items\": ["),"JSON array brackets survive Limbo BBCode rendering");
            await Execute("job list");Check(commands.LastResult.Contains(mixedId)&&commands.LastResult.Contains(cancelledId),"job list includes completed, failed and cancelled history");

            // More than one UI page: synthetic records in a separate database, no Blender work required.
            var largeRoot=Path.Combine(root,"large-library");
            var largeApi=new WorkspaceApi(new AssetDatabase(Path.Combine(largeRoot,"index.sqlite")),new LocalStorageProvider(largeRoot),api.Blender);
            for(int i=0;i<73;i++)largeApi.Database.Insert(new(){Name="Batch_"+i,FileHash="fixture_"+i,VirtualPath="assets/fixture.glb",SourceFormat=".glb"});
            var full=largeApi.Planner.Export("Batch",Path.Combine(root,"large-outputs"));
            Check(full.Items.Count==73&&largeApi.Search().Items.Count==60,"export snapshot contains all matches beyond the 60-item UI page");
            largeApi.Database.Insert(new(){Name="Batch_later",FileHash="later",VirtualPath="assets/fixture.glb"});
            Check(largeApi.Planner.Load(full.Id).Items.Count==73,"saved selection stays fixed when the library changes");
            var recovered=new JobRecord{Kind=JobKind.Import,State=JobState.Running,Items=[new(){State=ItemState.Succeeded,Source=new(){Input=walk}},new(){State=ItemState.Running,Source=new(){Input=missing}}]};
            var reports=Path.Combine(largeRoot,"jobs","reports");Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports,recovered.Id+".json"),JsonSerializer.Serialize(recovered));
            File.WriteAllText(Path.Combine(reports,"broken.json"),"{ broken JSON");
            var resumed=largeApi.Jobs.Get(recovered.Id);
            Check(resumed.State==JobState.Interrupted&&resumed.Items[0].State==ItemState.Succeeded&&resumed.Items[1].State==ItemState.Interrupted&&!largeApi.Jobs.Busy,"restart marks unfinished work interrupted without re-running successes");
            var recoveredResult=await largeApi.Jobs.WaitAsync(largeApi.Jobs.Retry(recovered.Id));
            Check(recoveredResult.Total==1&&recoveredResult.Succeeded==1,"interrupted job retries only unfinished work");

            // A real Blender process deliberately pauses after writing a partial output; cancel at its READY line.
            var slowScript=Path.Combine(root,"slow_conversion.py");
            File.WriteAllText(slowScript,"import sys, pathlib, time\np = pathlib.Path(sys.argv[sys.argv.index('--out') + 1])\np.write_bytes(b'partial output')\nprint('BATCH_TEST_READY', flush=True)\ntime.sleep(30)\n");
            var cancelRoot=Path.Combine(root,"cancel-library");var pipeline=new BlenderPipeline(slowScript);pipeline.Configure(api.Blender.Executable);
            var cancelApi=new WorkspaceApi(new AssetDatabase(Path.Combine(cancelRoot,"index.sqlite")),new LocalStorageProvider(cancelRoot),pipeline);
            await cancelApi.ImportAsync([Sample("Walk")]);bool ready=false;
            cancelApi.Jobs.Changed+=u=>{if(u.Message=="BATCH_TEST_READY"){ready=true;cancelApi.Jobs.Cancel(u.Id);}};
            var runningFolder=Path.Combine(root,"running-cancel");var timer=System.Diagnostics.Stopwatch.StartNew();
            var activeId=cancelApi.Jobs.Enqueue(cancelApi.Planner.Export("*",runningFolder));
            var cancelled=await cancelApi.Jobs.WaitAsync(activeId).WaitAsync(TimeSpan.FromSeconds(25));
            Check(ready&&cancelled.State==JobState.Cancelled&&cancelled.Cancelled==1&&timer.Elapsed.TotalSeconds<25,"running cancellation terminates a real Blender process before it finishes");
            Check(!Directory.EnumerateFiles(runningFolder,"*.fbx",SearchOption.AllDirectories).Any()&&!Directory.EnumerateFileSystemEntries(Path.Combine(runningFolder,".animanager-staging")).Any(),"cancellation removes partial output and staging directory");
            File.WriteAllText(slowScript,"import sys, pathlib\npathlib.Path(sys.argv[sys.argv.index('--out') + 1]).write_bytes(b'not FBX')\nprint('ANIMANAGER_CONVERT_OK {}', flush=True)\n");
            var invalid=await cancelApi.Jobs.WaitAsync(cancelApi.Jobs.Retry(activeId));
            Check(invalid.Failed==1&&invalid.Items[0].Error.Contains("binary FBX header")&&!Directory.EnumerateFiles(runningFolder,"*.fbx",SearchOption.AllDirectories).Any(),"invalid output is rejected even if Blender exits successfully");
            await cancelApi.Jobs.ShutdownAsync();await largeApi.Jobs.ShutdownAsync();
            var summary=$"PASS: {checks.Count} batch checks\n"+string.Join("\n",checks)+"\nLibrary: "+api.Storage.Root;
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),summary);GD.Print(summary);owner.GetTree().Quit(0);
        }
        catch(Exception ex)
        {
            await api.Jobs.ShutdownAsync();GD.PushError("BATCH TEST FAILED: "+ex);
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),$"{checks.Count} passed\n"+ex);owner.GetTree().Quit(1);
        }
    }
}
