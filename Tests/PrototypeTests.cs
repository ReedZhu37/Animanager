using Godot;
using Animanager.Core.Storage;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;

namespace Animanager.Tests;

public static class PrototypeTests
{
    public static async Task Run(Node owner,Preview3DViewport preview)
    {
        int checks=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;GD.Print("PASS "+label);}
        var root=ProjectSettings.GlobalizePath("res://TestResults/run-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath=Path.Combine(root,"index.sqlite");var db=new AssetDatabase(dbPath);
            var storage=new LocalStorageProvider(Path.Combine(root,"library"));var importer=new ImportService(db,storage);
            var samples=ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples");
            var result=await importer.ImportAsync([samples]);
            Check(result.Added==4&&result.Errors.Count==0,"import four real GLB assets");
            result=await importer.ImportAsync([samples]);Check(result.Added==0&&result.Skipped==4,"content deduplication");
            Check(db.Search("walk").Total==1,"case insensitive name search");
            Check(db.Search("", "Animation").Total==3,"animation classification");
            Check(db.Search("", "Model").Total==1,"model classification");
            Check(db.Search("", "All",0,2).Items.Count==2&&db.Search("","All",1,2).Items.Count==2,"paged asset search");
            var asset=db.Search("Walk").Items.Single();asset.Tags="行走, locomotion";asset.Favorite=true;db.Update(asset);
            Check(db.Search("行走").Total==1&&db.Search("locom").Total==1,"Chinese tags and FTS prefix search");
            Check(db.Search("", "Favorites").Total==1,"favorite filter");
            Check(db.Search("%_' OR 1=1 --").Total==0,"search punctuation and SQL escaping");
            Check(new AssetDatabase(dbPath).Search("行走").Items.Single().Favorite,"database survives reopening");
            bool rejected=false;try{storage.Resolve("../escape.glb");}catch(IOException){rejected=true;}
            Check(rejected,"storage traversal rejected");
            var canceled=new CancellationTokenSource();canceled.Cancel();rejected=false;
            try{await importer.ImportAsync([samples],ct:canceled.Token);}catch(OperationCanceledException){rejected=true;}
            Check(rejected,"import cancellation");
            var invalid=Path.Combine(root,"missing.gltf");await File.WriteAllTextAsync(invalid,"{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"absent.bin\",\"byteLength\":1}]}");
            result=await importer.ImportAsync([invalid]);Check(result.Errors.Count==1&&db.Search().Total==4,"missing glTF dependency does not enter index");
            var source=await storage.AcquireLocalFileAsync(asset.VirtualPath);
            preview.LoadFile(source);
            Check(preview.BoneCount==16&&preview.MeshCount>0,"runtime GLB skin and mesh loading");
            Check(preview.Clips.Length==1&&preview.Duration>1,"runtime animation discovery");
            preview.Seek(0.4);Check(Math.Abs(preview.Playhead-.4)<.01&&!preview.Playing,"timeline scrubbing pauses at requested time");
            preview.TogglePlay();Check(preview.Playing,"animation playback starts");
            preview.TogglePlay();Check(!preview.Playing,"animation playback pauses");
            var exported=ExportService.Export(preview,root,"walk",true);
            using var library=ResourceLoader.Load<AnimationLibrary>(exported);
            Check(library!=null&&library.GetAnimationList().Count==1,"AnimationLibrary export reloads");
            Check(library!.GetAnimation(library.GetAnimationList()[0]).GetTrackCount()>0,"export retains animation tracks");
            var second=ExportService.Export(preview,root,"walk",true);Check(exported!=second&&File.Exists(exported),"export does not overwrite existing files");
            var glb=ExportService.Export(preview,root,"walk_scene",false);preview.LoadFile(glb);
            Check(preview.BoneCount==16&&preview.Clips.Length==1,"exported GLB roundtrip preserves skeleton and animation");
            var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
            preview.Seek(0);var pose=skeleton.GetBonePoseRotation(skeleton.FindBone("LeftUpperLeg"));
            preview.Seek(.3);var pose2=skeleton.GetBonePoseRotation(skeleton.FindBone("LeftUpperLeg"));
            Check(!pose.IsEqualApprox(pose2),"animation changes actual bone pose");
            // External .gltf must remain usable after the original buffer is moved away.
            var gltfDir=Path.Combine(root,"external");Directory.CreateDirectory(gltfDir);
            byte[] jsonBytes,binBytes;
            using(var input=new BinaryReader(File.OpenRead(Path.Combine(samples,"StudioBot_Idle.glb"))))
            {
                input.ReadBytes(12);var jsonLength=input.ReadInt32();input.ReadInt32();jsonBytes=input.ReadBytes(jsonLength);
                var binLength=input.ReadInt32();input.ReadInt32();binBytes=input.ReadBytes(binLength);
            }
            var document=System.Text.Json.Nodes.JsonNode.Parse(jsonBytes)!;
            document["buffers"]![0]!["uri"]="motion.bin";
            var gltf=Path.Combine(gltfDir,"external.gltf");await File.WriteAllTextAsync(gltf,document.ToJsonString());
            await File.WriteAllBytesAsync(Path.Combine(gltfDir,"motion.bin"),binBytes);
            result=await importer.ImportAsync([gltf]);
            File.Move(Path.Combine(gltfDir,"motion.bin"),Path.Combine(gltfDir,"motion.moved"));
            var imported=db.Search("external").Items.Single();preview.LoadFile(await storage.AcquireLocalFileAsync(imported.VirtualPath));
            Check(result.Added==1&&preview.BoneCount==16&&preview.Clips.Length==1,"external glTF dependency copied independently");
            document["buffers"]![0]!["uri"]="../motion.bin";await File.WriteAllTextAsync(gltf,document.ToJsonString());
            result=await importer.ImportAsync([gltf]);Check(result.Errors.Count==1,"glTF traversal dependency rejected");
            preview.LoadFile(await storage.AcquireLocalFileAsync(db.Search("Rig").Items.Single().VirtualPath));
            Check(preview.Clips.Length==0&&preview.BoneCount==16&&!preview.Playing,"static rig clears previous animation state");
            var report=$"PASS: {checks} integration checks\nOutput: {root}\n";
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("SELF TEST FAILED: "+ex);await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }
}

