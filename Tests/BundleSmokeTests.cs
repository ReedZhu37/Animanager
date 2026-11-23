using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.UI.Components;
using Animanager.Core.Pipeline;

namespace Animanager.Tests;

public static class BundleSmokeTests
{
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var checks=new List<string>();
        void Check(bool ok,string title){if(!ok)throw new Exception(title);checks.Add(title);GD.Print("PASS "+title);}
        try
        {
            Check(File.Exists(api.Blender.ScriptPath),"external Blender script resolves beside application");
            Check(api.Uasset.Available,"independent CUE4Parse helper is packaged beside application");
            var console=owner.GetNode("/root/LimboConsole");console.Call("execute_command","baseline import");await commands.PendingTask;
            Check(api.Search().Total==1&&api.Find("UE5_Manny_Idle").Type=="Animation","only the animated bundled FBX enters SQLite through Limbo");
            console.Call("execute_command","preview load UE5_Manny_Idle");await commands.PendingTask;
            Check(preview.BoneCount==89&&preview.Clips.Length>0,"packaged FBX loader previews the official animated skeleton");
            var output=Path.Combine(api.Storage.Root,"exports");
            var glb=ExportService.Export(preview,output,"bundle-preview",false);preview.LoadFile(glb);
            Check(preview.BoneCount==89&&preview.Clips.Length>0,"standalone GLB export round trip");
            if(api.Blender.Available)
            {
                var fbx=await api.ExportFbxAsync("UE5_Manny_Idle",output);preview.LoadFile(fbx);
                Check(preview.BoneCount==89&&preview.Clips.Length>0,"standalone Blender FBX export round trip");
            }
            else GD.Print("SKIP FBX conversion: Blender not configured on this machine.");
            foreach (string rig in Core.Retarget.RigCatalog.Ids)
                Check(Core.Retarget.RigCatalog.Hash(rig).Length == 64, "packaged retarget reference resolves: " + rig);
            console.Call("execute_command", "retarget preview UE5_Manny_Idle ue4-mannequin"); await commands.PendingTask;
            Check(preview.BoneCount == 68 && preview.AppliedRetarget?.TargetRig == "ue4-mannequin", "standalone retarget evaluator and target mesh load");
            var report=$"PASS: {checks.Count} bundle smoke checks\n"+string.Join("\n",checks)+"\nEditor binary: "+OS.HasFeature("editor");
            File.WriteAllText(Path.Combine(api.Storage.Root,"bundle-results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("BUNDLE SMOKE FAILED: "+ex);Directory.CreateDirectory(api.Storage.Root);File.WriteAllText(Path.Combine(api.Storage.Root,"bundle-results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }
}
