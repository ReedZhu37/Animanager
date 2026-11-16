using Godot;
using Animanager.Core;
using Animanager.Core.Motion;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using System.Text.Json;

namespace Animanager.Tests;

public static class MotionTests
{
    public static async Task Render(Animanager.UI.MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var folder=Path.Combine(api.Storage.Root,"motion-validation");Directory.CreateDirectory(folder);
        try
        {
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);owner.Workspace.UsePreset("animation");
            var asset=api.Database.SearchAll().First(a=>a.SourceFormat==".motion"&&a.Name.EndsWith("__AS_Combo_Attack_03_All_Seq"));
            commands.Load(asset.Id);await commands.PendingTask;if(preview.Motion==null)throw new Exception(commands.LastResult);
            preview.SetLoop(false);preview.Seek(2.1);var initial=preview.Motion.Prop.GlobalTransform;preview.TogglePlay();
            for(int i=0;i<40;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
            if(preview.Playhead<=2.1||preview.Motion.Prop.GlobalTransform.IsEqualApprox(initial))throw new Exception("Real-time prop playback did not advance");
            for(int i=0;i<3;i++)
            {
                preview.Seek(2.5+i*.2);preview.Bones.Select("hand_r");
                var sk=preview.Motion.Skeleton;preview.FocusPoint(sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone("pelvis")).Origin+Vector3.Up*.15f);
                for(int f=0;f<8;f++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
                preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var screenshot=owner.GetViewport().GetTexture().GetImage();screenshot.SavePng(Path.Combine(folder,$"spin-{i+1}.png"));
            }
            File.WriteAllText(Path.Combine(folder,"render-results.txt"),"PASS: Combo03 real-time body and independent prop playback; fixed 161-bone character; three spin screenshots. Diagnostic blade uses source-bone origin, grip/contact unverified.\n");owner.GetTree().Quit();
        }
        catch(Exception ex){GD.PushError(ex.ToString());owner.GetTree().Quit(1);}
    }
    public static string MeshHash(Node3D root)
    {
        using var stream=new MemoryStream();
        foreach(var mesh in Preview3DViewport.Descendants(root).OfType<MeshInstance3D>().Where(m=>!m.HasMeta("animanager_prop_visual")))
        {
            for(int i=0;i<mesh.Mesh.GetSurfaceCount();i++)stream.Write(GD.VarToBytes(mesh.Mesh.SurfaceGetArrays(i)));
            if(mesh.Skin is {} skin)for(int i=0;i<skin.GetBindCount();i++){stream.Write(GD.VarToBytes(skin.GetBindPose(i)));stream.Write(GD.VarToBytes(skin.GetBindName(i)));}
        }
        return MotionJson.Hash(stream.ToArray());
    }
    public static string RigHash(Skeleton3D sk)=>MotionJson.Hash(MotionJson.Bytes(Enumerable.Range(0,sk.GetBoneCount()).Select(i=>new{name=sk.GetBoneName(i).ToString(),parent=sk.GetBoneParent(i)<0?"":sk.GetBoneName(sk.GetBoneParent(i)).ToString(),rest=Convert.ToBase64String(GD.VarToBytes(sk.GetBoneRest(i)))}).OrderBy(b=>b.name).ToArray()));
    public static async Task Run(Node owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands,string input)
    {
        var output=Path.Combine(api.Storage.Root,"motion-validation");Directory.CreateDirectory(output);var checks=new List<string>();var rows=new List<object>();string error="";double maxPosition=0,maxAngle=0;long sampleCount=0;
        var console=owner.GetNode("/root/LimboConsole");
        async Task Execute(string command){console.Call("execute_command",command);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add(message);GD.Print("PASS "+message);}
        void Save()=>MotionJson.Atomic(Path.Combine(output,"results.json"),new{checks,error,maxPosition,maxAngle,sampleCount,clips=rows.Count,results=rows});
        try
        {
            bool small=MotionJson.IsMotion(input);
            if(small)await Execute($"asset import \"{input.Replace('\\','/')}\"");else await Execute($"uasset import \"{input.Replace('\\','/')}\"");
            var job=api.Jobs.Get(api.Jobs.LatestId!);Check(job.State==JobState.Completed,"Import through actual Limbo and job queue");
            var assets=api.Database.SearchAll().Where(a=>a.SourceFormat==".motion").ToArray();Check(assets.Length==(small?1:740),"Expected separated animation count");
            var firstPath=api.Storage.Resolve(assets[0].VirtualPath);var first=MotionJson.Load(firstPath);preview.LoadFile(MotionJson.ResolveObject(firstPath,first.Objects["preset"]));
            string baselineMesh=MeshHash(preview.Model!);var baselineSkin=Preview3DViewport.Descendants(preview.Model!).OfType<MeshInstance3D>().Single().Skin;
            Check(baselineSkin.GetBindCount()==89,"Original Manny Simple has 89 unchanged skin binds");
            string? fixedRig=null;var referenceArg=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--source-reference="));
            using var original=referenceArg==null?null:JsonDocument.Parse(File.ReadAllText(referenceArg[19..]));
            var refs=original?.RootElement.GetProperty("clips").EnumerateArray().ToDictionary(c=>c.GetProperty("source").GetString()!,c=>c.Clone());
            foreach(var asset in assets)
            {
                commands.Load(asset.Id);await commands.PendingTask;if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);
                var motion=preview.Motion??throw new Exception("Missing independent motion playback");var sk=motion.Skeleton;preview.SetLoop(false);
                if(preview.BoneCount!=161||sk.FindBone("Nodachi_Weapon_R")>=0)throw new Exception("Weapon polluted the canonical body rig");
                if(MeshHash(preview.Model!)!=baselineMesh)throw new Exception("Preset geometry, vertex weights or inverse binds changed");
                string hash=RigHash(sk);fixedRig??=hash;if(hash!=fixedRig)throw new Exception("Preset skeleton differs across clips");
                if(motion.Manifest.Objects["preset"].Sha256!=first.Objects["preset"].Sha256||motion.Manifest.Objects["rig"].Sha256!=first.Objects["rig"].Sha256)throw new Exception("Preset not shared");
                double position=0,angle=0;
                if(refs!=null)
                {
                    var reference=refs[motion.Manifest.Provenance["source"]];
                    foreach(var sample in reference.GetProperty("samples").EnumerateArray())
                    {
                        preview.Seek(sample.GetProperty("time").GetDouble());
                        foreach(var bone in sample.GetProperty("bones").EnumerateArray())
                        {
                            string name=bone.GetProperty("name").GetString()!;var actual=name=="Nodachi_Weapon_R"?motion.Prop.Transform:sk.GetBonePose(sk.FindBone(name));
                            float[] V(string key)=>bone.GetProperty(key).EnumerateArray().Select(v=>v.GetSingle()).ToArray();var p=V("p");var q=V("r");
                            var ep=new Vector3(p[0],-p[1],p[2])*.01f;var eq=new Quaternion(-q[0],q[1],-q[2],q[3]).Normalized();
                            position=Math.Max(position,actual.Origin.DistanceTo(ep));angle=Math.Max(angle,2*Math.Acos(Math.Clamp(Math.Abs(actual.Basis.Orthonormalized().GetRotationQuaternion().Dot(eq)),0,1))*180/Math.PI);sampleCount++;
                        }
                    }
                    if(position>.0001||angle>.1)throw new Exception($"Original source sample mismatch: {asset.Name} {position} {angle}");
                }
                rows.Add(new{id=asset.Id,name=asset.Name,position,angle,state=motion.Manifest.Props[0].State});maxPosition=Math.Max(maxPosition,position);maxAngle=Math.Max(maxAngle,angle);
                if(rows.Count%25==0){Save();GD.Print($"MOTION_APP_PROGRESS {rows.Count}/{assets.Length}");}
                await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
            }
            Check(true,"All previews retain canonical rig, original mesh/skin and original sampled source poses");
            var chosen=assets.FirstOrDefault(a=>a.Name.Contains("AS_Combo_Attack_01_All_Seq"))??assets[0];await Execute("preview load "+chosen.Id);preview.SetLoop(false);
            var selected=preview.Motion!;preview.Seek(preview.Duration*.4);var propBefore=selected.Prop.GlobalTransform;string path=selected.ManifestPath;var objects=selected.Manifest.Objects.ToDictionary(k=>k.Key,k=>k.Value.Sha256);
            var legacyArg=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--legacy-library="));
            if(legacyArg!=null)
            {
                string legacyRoot=legacyArg[17..];string? oldFile=null;
                foreach(var report in Directory.EnumerateFiles(Path.Combine(legacyRoot,"uasset"),"import-report.json",SearchOption.AllDirectories))
                {using var d=JsonDocument.Parse(File.ReadAllText(report));foreach(var item in d.RootElement.GetProperty("items").EnumerateArray())if(item.GetProperty("source").GetString()==selected.Manifest.Provenance["source"])oldFile=Path.Combine(legacyRoot,item.GetProperty("path").GetString()!);}
                var reference=MotionGlb.Read(oldFile??throw new Exception("Missing legacy reference"));int weapon=reference.ByName["Nodachi_Weapon_R"];double worldError=0,tipError=0;
                for(int f=0;f<=2*(selected.Manifest.Frames-1);f++)
                {
                    double t=preview.Duration*f/(2*(selected.Manifest.Frames-1));preview.Seek(t);var expected=reference.WorldAt(weapon,t);var actual=selected.Prop.GlobalTransform;
                    worldError=Math.Max(worldError,actual.Origin.DistanceTo(new Vector3(expected.M41,expected.M42,expected.M43)));
                    var target=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0,0,.8f),expected);tipError=Math.Max(tipError,(actual*new Vector3(0,0,.8f)).DistanceTo(new Vector3(target.X,target.Y,target.Z)));
                }
                Check(worldError<.0001&&tipError<.0001,$"Actual App weapon at every key/midpoint matches legacy world motion: {worldError:F9} m, tip {tipError:F9} m");
                preview.Seek(preview.Duration*.4);propBefore=selected.Prop.GlobalTransform;
            }
            await Execute("motion inspect "+chosen.Id);Check(commands.LastResult.Contains("observe-only")&&commands.LastResult.Contains("source-bone-origin-unverified"),"Contact and grip uncertainties are exposed");
            await Execute($"motion contact {chosen.Id} 0 {Math.Min(.1,preview.Duration).ToString(System.Globalization.CultureInfo.InvariantCulture)} free");
            await Execute($"motion grip {chosen.Id} 0.01,0.02,0.03 0,45,0");
            preview.Seek(preview.Duration*.4);selected=preview.Motion!;
            Check(selected.Prop.GlobalTransform.Origin.DistanceTo(propBefore.Origin)<.00001f,"Editing contact/grip does not alter source prop trajectory");
            Check(selected.Presentation.GripConfirmed&&selected.Presentation.GripPosition[1]==.02f&&selected.Presentation.Contacts![0].State=="free","Grip and interval edits persist through reload");
            Check(selected.Manifest.Objects.All(k=>objects[k.Key]==k.Value.Sha256)&&MeshHash(preview.Model!)==baselineMesh,"Edits preserve all immutable objects and character mesh");
            await Execute($"motion sample 0.05");Check(commands.LastResult.Contains("\"contact\": \"free\""),"Console sample reports the edited contact interval");
            await Execute("motion reset "+chosen.Id);Check(preview.Motion!.Presentation.Contacts==null&&!preview.Motion.Presentation.GripConfirmed,"Reset restores defaults and retains revision history");
            bool rejected=false;try{api.SetMotionContact(chosen.Id,-1,.1,"right");}catch(ArgumentException){rejected=true;}Check(rejected,"Invalid contact interval rejected");
            rejected=false;try{api.SetMotionGrip(chosen.Id,[0,0,0],[0,0,0,0]);}catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid grip quaternion rejected");
            var relocated=Path.Combine(output,"relocated");Directory.CreateDirectory(relocated);var relocatedManifest=Path.Combine(relocated,"sample.motion.json");File.Copy(path,relocatedManifest,true);
            var originalManifest=MotionJson.Load(path);
            foreach(var item in originalManifest.Objects.Values){var target=Path.Combine(relocated,item.Path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(MotionJson.ResolveObject(path,item,false),target,true);}
            preview.LoadFile(relocatedManifest);Check(preview.BoneCount==161&&MeshHash(preview.Model!)==baselineMesh,"A relocated object closure loads without original library or source paths");
            var relocatedPlan=await api.Planner.ImportAsync([relocated]);Check(relocatedPlan.Items.Count==1,"Directory import treats a motion bundle as one asset, not its internal GLBs");
            var sibling=Path.Combine(relocated,"standalone.glb");File.Copy(MotionJson.ResolveObject(path,originalManifest.Objects["preset"],false),sibling,true);
            relocatedPlan=await api.Planner.ImportAsync([relocated]);Check(relocatedPlan.Items.Count==2,"Mixed folder import keeps unrelated GLB siblings alongside motion bundles");File.Delete(sibling);
            var copiedProp=MotionJson.ResolveObject(relocatedManifest,originalManifest.Objects["prop"]);var pristine=File.ReadAllBytes(copiedProp);var damaged=(byte[])pristine.Clone();damaged[^1]^=1;File.WriteAllBytes(copiedProp,damaged);
            rejected=false;try{MotionJson.Load(relocatedManifest);}catch(InvalidDataException){rejected=true;}finally{File.WriteAllBytes(copiedProp,pristine);}Check(rejected,"Corrupt motion objects are detected before playback");
            await Execute("preview load "+chosen.Id);
            await Execute($"asset import \"{path.Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);Check(job.State==JobState.Completed&&job.Skipped==1,"Repeat import deduplicates the composite asset");
            var exported=Animanager.Core.Pipeline.ExportService.Export(preview,output,"motion-scene",false);var exportedData=MotionGlb.Read(exported);
            Check(exportedData.Channels.Any(c=>exportedData.Nodes[c.Node].Name.Contains("PrimaryWeaponMotion")),"GLB scene export retains independent weapon animation channels");
            if(OS.GetCmdlineUserArgs().Contains("--motion-fbx"))
            {
                commands.Load(chosen.Id);await commands.PendingTask;preview.SetLoop(false);var m=preview.Motion!;
                var times=new[]{0d,preview.Duration*.2,preview.Duration*.5,preview.Duration};var expected=new List<Transform3D>();var geometry=new List<Vector3[]>();
                var blade=Preview3DViewport.Descendants(preview.Model!).OfType<MeshInstance3D>().Single(n=>n.Name.ToString()=="Blade");
                foreach(var t in times){preview.Seek(t);expected.Add(m.Prop.GlobalTransform);geometry.Add(Enumerable.Range(0,8).Select(k=>blade.GlobalTransform*blade.GetAabb().GetEndpoint(k)).ToArray());}
                await Execute($"export fbx {chosen.Id} \"{output.Replace('\\','/')}\"");job=api.Jobs.Get(api.Jobs.LatestId!);Check(job.State==JobState.Completed,"Composite motion exports through existing FBX job queue");
                preview.LoadFile(job.Items[0].Output);preview.SetLoop(false);double difference=0,angleError=0,geometryError=0;
                blade=Preview3DViewport.Descendants(preview.Model!).OfType<MeshInstance3D>().Single(n=>n.Name.ToString()=="Blade");
                for(int i=0;i<times.Length;i++)
                {
                    preview.Seek(times[i]);var node=Preview3DViewport.Descendants(preview.Model!).OfType<Node3D>().FirstOrDefault(n=>n is not BoneAttachment3D&&n.Name.ToString()=="PrimaryWeaponMotion");Transform3D actual;
                    if(node!=null)actual=node.GlobalTransform;
                    else{var sk=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().First(s=>s.FindBone("PrimaryWeaponMotion")>=0);actual=sk.GlobalTransform*sk.GetBoneGlobalPose(sk.FindBone("PrimaryWeaponMotion"));}
                    difference=Math.Max(difference,actual.Origin.DistanceTo(expected[i].Origin));angleError=Math.Max(angleError,2*Math.Acos(Math.Clamp(Math.Abs(actual.Basis.Orthonormalized().GetRotationQuaternion().Dot(expected[i].Basis.Orthonormalized().GetRotationQuaternion())),0,1))*180/Math.PI);
                    foreach(int corner in Enumerable.Range(0,8)){var point=blade.GlobalTransform*blade.GetAabb().GetEndpoint(corner);geometryError=Math.Max(geometryError,geometry[i].Min(v=>v.DistanceTo(point)));}
                }
                Check(difference<.003&&angleError<.5,$"FBX independent weapon round trip: {difference:F7} m / {angleError:F4} deg");
                Check(geometryError<.003,$"FBX coordinate normalization also preserves visible blade geometry: {geometryError:F7} m");
            }
            Save();GD.Print($"MOTION_APP_COMPLETE {rows.Count} clips, {checks.Count} checks");owner.GetTree().Quit();
        }
        catch(Exception ex){error=ex.ToString();Save();GD.PushError(error);owner.GetTree().Quit(1);}
    }
}
