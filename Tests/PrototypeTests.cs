using Godot;
using Animanager.Core;
using Animanager.Core.Motion;
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
            Check(result.Added==3&&result.ExcludedWithoutAnimation==1&&result.Errors.Count==0,
                "import keeps animated meshes and skips the static rig");
            result=await importer.ImportAsync([samples]);
            Check(result.Added==0&&result.Skipped==3&&result.ExcludedWithoutAnimation==1,"content deduplication keeps static skips separate");
            Check(db.Search("walk").Total==1,"case insensitive name search");
            Check(db.Search("", "Animation").Total==3,"animation classification");
            var meshOnly = Path.Combine(root,"MeshOnly.gltf");
            var mesh = new MeshInstance3D { Name="StaticMesh", Mesh=new BoxMesh() };
            try { ExportService.WriteScene(meshOnly,mesh,null,false,0); } finally { mesh.Free(); }
            result=await importer.ImportAsync([meshOnly]);
            Check(result.Added==0&&result.ExcludedWithoutAnimation==1&&result.Errors.Count==0&&db.Search("", "Model").Total==0
                &&Directory.GetDirectories(storage.Resolve("assets")).Length==3,"mesh-only glTF and its dependencies stay outside the library");
            Check(db.Search("", "All",0,2).Items.Count==2&&db.Search("","All",1,2).Items.Count==1,"paged asset search");
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
            result=await importer.ImportAsync([invalid]);Check(result.Errors.Count==1&&db.Search().Total==3,"missing glTF dependency does not enter index");
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
            var meshTake = document.DeepClone();
            var take = meshTake["animations"]![0]!;
            var channel = take["channels"]![0]!.DeepClone();
            var nodes = meshTake["nodes"]!.AsArray();
            channel["target"]!["node"] = Enumerable.Range(0, nodes.Count).Single(i => nodes[i]!["mesh"] != null);
            take["channels"] = new System.Text.Json.Nodes.JsonArray(channel);
            take["name"] = "Unreal Take";
            string meshTakePath = Path.Combine(gltfDir, "MeshTake.gltf");
            await File.WriteAllTextAsync(meshTakePath, meshTake.ToJsonString());
            result=await importer.ImportAsync([meshTakePath]);
            Check(result.Added==0&&result.ExcludedWithoutAnimation==1&&result.Errors.Count==0,
                "a rigged mesh with a named take and no bone tracks stays outside the library");
            result=await importer.ImportAsync([gltf]);
            File.Move(Path.Combine(gltfDir,"motion.bin"),Path.Combine(gltfDir,"motion.moved"));
            var imported=db.Search("external").Items.Single();preview.LoadFile(await storage.AcquireLocalFileAsync(imported.VirtualPath));
            Check(result.Added==1&&preview.BoneCount==16&&preview.Clips.Length==1,"external glTF dependency copied independently");
            document["buffers"]![0]!["uri"]="../motion.bin";await File.WriteAllTextAsync(gltf,document.ToJsonString());
            result=await importer.ImportAsync([gltf]);Check(result.Errors.Count==1,"glTF traversal dependency rejected");
            preview.LoadFile(Path.Combine(samples,"StudioBot_Rig.glb"));
            Check(preview.Clips.Length==0&&preview.BoneCount==16&&!preview.Playing,"static rig clears previous animation state");
            var api=new WorkspaceApi(db,storage,new BlenderPipeline(RuntimePaths.Bundled("BuiltinAssets/Scripts/blender_convert.py")));
            string Clip(string name,string clip,float[] peak)
            {
                var path=Path.Combine(root,name+".glb");
                File.WriteAllBytes(path,MotionGlb.Write([new(){Name="root"}],
                    [new(0,"translation",[0,.5f,1],[[0,0,0],peak,[0,0,0]])],clip,30,zUpParent:true));
                return path;
            }
            result=await api.ImportAsync([Clip("Stationary","Stationary",[.004f,.004f,.004f]),
                Clip("Horizontal","Horizontal",[.5f,0,0]),Clip("Jump","Jump",[0,0,.5f]),
                Clip("Mixed","Mixed",[.5f,0,.5f]),Clip("Variant","Attack_InPlace",[.2f,0,0])]);
            Check(result.Added==4&&result.ExcludedInPlace==1&&result.Skipped==0&&result.Errors.Count==0,
                "import separately reports an embedded InPlace clip and does not index it");
            Check(api.Find("Stationary").Tags=="无位移"&&api.Find("Horizontal").Tags=="水平位移"
                &&api.Find("Jump").Tags=="垂直位移"&&api.Find("Mixed").Tags=="混合",
                "root motion tags classify full excursions in world axes, including a jump back to its origin");
            Directory.CreateDirectory(Path.Combine(root,"02_IP"));
            var folderVariant=Clip(Path.Combine("02_IP","FolderVariant"),"Vertical",[0,0,.4f]);
            result=await api.ImportAsync([folderVariant,Clip("Attack_IP","Attack",[.4f,0,0]),
                Clip("Pack__02_IP__Climb","Climb",[0,0,.9f]),Clip("Attack_RM","Attack",[.5f,0,0])]);
            Check(result.ExcludedInPlace==3&&result.Added==1&&api.Find("Attack_RM").Tags=="水平位移",
                "import recognizes source folder and filename IP markers and retains the RM counterpart");
            api.SetTags("Jump","用户标签 水平位移 混合");
            api.RenameAsset("Horizontal","Horizontal_InPlace");
            var organized=await api.OrganizeAnimationsAsync();
            Check(organized.Errors.Count==0&&organized.MovedToTrash==1&&api.Search("Horizontal_InPlace","Trash").Total==1
                &&api.Find("Jump").Tags=="用户标签 垂直位移",
                "existing library backfill replaces stale motion tags, preserves user tags and uses recoverable trash");
            var fixedPose = Path.Combine(root, "FixedPose.glb");
            var fixedPoseBytes = MotionGlb.Write([new() { Name = "root" }],
                [new(0, "translation", [0, 1], [[0, .25f, 0], [0, .25f, 0]]),
                 new(0, "rotation", [0, 1], [[0, 0, 0, 1], [0, 0, 0, -1]])], "FixedPose", 30, zUpParent: true);
            await File.WriteAllBytesAsync(fixedPose, fixedPoseBytes);
            result = await api.ImportAsync([fixedPose]);
            Check(result is { Added: 0, ExcludedWithoutAnimation: 1, Errors.Count: 0 },
                "a timed clip that holds a fixed pose stays outside the asset library");

            string legacyPath = "assets/legacy/FixedPose.glb";
            await storage.PutFileAsync(fixedPose, legacyPath);
            var legacy = new Animanager.Core.Models.AssetItem { Name = "FixedPose", VirtualPath = legacyPath,
                SourceFormat = ".glb", FileHash = MotionJson.Hash(fixedPoseBytes), Type = "Animation" };
            db.Insert(legacy);
            result = await api.ImportAsync([fixedPose]);
            var review = await api.FindAssetsWithoutAnimationAsync();
            Check(result is { Skipped: 0, ExcludedWithoutAnimation: 1 } && review.Errors.Length == 0 &&
                review.Assets.Any(asset => asset.Id == legacy.Id) && review.Assets.All(asset => asset.Name != "Stationary"),
                "legacy animation labels cannot hide static assets, and clips with subtle movement remain playable");
            api.SetTrash([legacy.Id], true);
            Check(api.Search("FixedPose", "Trash").Total == 1 && File.Exists(storage.Resolve(legacyPath)),
                "a confirmed static asset can be moved to trash without deleting the source");
            db.CheckIndex();
            var report=$"PASS: {checks} integration checks\nOutput: {root}\n";
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("SELF TEST FAILED: "+ex);await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }
}

