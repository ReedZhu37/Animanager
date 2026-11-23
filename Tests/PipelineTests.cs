using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.UI.Components;
using System.Text.Json;

namespace Animanager.Tests;

public static class PipelineTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var root=ProjectSettings.GlobalizePath("res://TestResults/pipeline-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        int checks=0;var records=new List<object>();
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;GD.Print("PASS "+message);}
        var console=owner.GetNode("/root/LimboConsole");
        async Task Execute(string command)
        {
            console.Call("execute_command",command);await commands.PendingTask;
            if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);
        }
        float Height()=>Preview3DViewport.CalculateWorldBounds(preview.Model!)!.Value.Size.Y;
        try
        {
            await Execute("app status");Check(commands.LastResult.Contains("fbxExport"),"Limbo registers and dispatches typed C# command");
            await Execute("baseline import");
            var import=api.Jobs.Get(api.Jobs.LatestId!);
            Check(api.Search("UE").Total==1&&import.Failed==0&&import.Skipped==4,"FBX import skips four static reference files with empty takes");
            Check(api.Find("UE5_Manny_Idle").Tags=="无位移","FBX import tags genuine skeletal animation with no root displacement");
            foreach(var name in new[]{"UE4_Mannequin","UE5_Manny","UE5_Quinn","UE5_Manny_Idle"})
            {
                preview.LoadFile(RuntimePaths.Bundled("BuiltinAssets/Baselines/"+name+".fbx"));
                Check(preview.MeshCount>0&&preview.BoneCount>50,name+" native FBX meshes and skeleton");
                records.Add(new{name,bones=preview.BoneCount,meshes=preview.MeshCount,clips=preview.Clips,height=Height()});
            }
            await Execute("preview load UE5_Manny_Idle");
            Check(preview.Clips.Length>0&&preview.Duration>0,"Epic FBX animation imported");
            var duration=preview.Duration;var height=Height();
            var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
            var boneNames=Enumerable.Range(0,skeleton.GetBoneCount()).Select(i=>skeleton.GetBoneName(i).ToString()).ToArray();
            await Execute("preview seek 0");var pose=Enumerable.Range(0,skeleton.GetBoneCount()).Select(skeleton.GetBonePose).ToArray();
            await Execute($"preview seek {(duration*.4).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Check(Enumerable.Range(0,skeleton.GetBoneCount()).Any(i=>!skeleton.GetBonePose(i).IsEqualApprox(pose[i])),"official animation drives bone pose");
            await Execute("asset tags UE5_Manny_Idle \"baseline official\"");Check(api.Search("official").Total==1,"console quoted string arguments");
            await Execute("asset favorite UE5_Manny_Idle true");Check(api.Find("UE5_Manny_Idle").Favorite,"console boolean argument");
            await Execute("preview play");Check(preview.Playing,"console starts animation");
            await Execute("preview pause");Check(!preview.Playing,"console pauses animation");
            await Execute($"export fbx UE5_Manny_Idle \"{root.Replace('\\','/')}\"");
            var exported=Path.Combine(root,"UE5_Manny_Idle.fbx");
            Check(File.Exists(exported)&&new FileInfo(exported).Length>1000,"Blender FBX export completes through Limbo");
            preview.LoadFile(exported);
            var reloaded=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First();
            var missing=boneNames.Where(b=>reloaded.FindBone(b)<0).ToArray();
            GD.Print("Bone comparison: source="+boneNames.Length+" destination="+reloaded.GetBoneCount()+" missing="+string.Join(",",missing));
            Check(missing.Length==0,"FBX roundtrip preserves all source bone names including IK");
            Check(preview.Clips.Length>0&&Math.Abs(preview.Duration-duration)<0.1,"FBX roundtrip retains animation duration");
            GD.Print($"Height comparison: source={height} destination={Height()}");
            Check(Math.Abs(Height()/height-1)<.05,"FBX roundtrip retains model scale and vertical axis");
            var before=Enumerable.Range(0,reloaded.GetBoneCount()).Select(reloaded.GetBonePose).ToArray();
            preview.Seek(preview.Duration*.4);
            Check(Enumerable.Range(0,reloaded.GetBoneCount()).Any(i=>!reloaded.GetBonePose(i).IsEqualApprox(before[i])),"exported FBX animation moves the skeleton");
            // glTF -> FBX is a separate input path, exercised on a small deterministic fixture.
            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_Walk.glb")]);
            var converted=await api.ExportFbxAsync("StudioBot_Walk",root);preview.LoadFile(converted);
            Check(preview.BoneCount>=16&&preview.Clips.Length>0,"GLB to FBX skeletal animation export");
            var report=$"PASS: {checks} FBX / Limbo checks\n"+JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true});
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("PIPELINE TEST FAILED: "+ex);await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }
}
