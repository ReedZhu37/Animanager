using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Preview;
using Animanager.Core.Pipeline;
using Animanager.UI;
using Animanager.UI.Components;

namespace Animanager.Tests;

public static class MultiTrajectoryTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        string folder=ProjectSettings.GlobalizePath("res://TestResults/multi-bones-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
        var checks=new List<string>();var console=owner.GetNode("/root/LimboConsole");var bones=preview.Bones;
        void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);GD.Print("PASS "+label);}
        async Task Frames(int count=2){for(int i=0;i<count;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Ready(){await bones.PendingSampling;if(bones.Status!="Ready"&&bones.Status!="Off")throw new Exception(bones.Status);}
        async Task Execute(string command)
        {console.Call("execute_command",command);await commands.PendingTask;await Ready();if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        void Reject(string command,string label){console.Call("execute_command",command);Check(commands.LastResult.StartsWith("ERROR:"),label);}
        bool Near(Vector3 a,Vector3 b)=>a.DistanceTo(b)<.0003f;
        bool SameRotation(Quaternion a,Quaternion b)=>Mathf.Abs(a.Dot(b))>.99999f;
        try
        {
            preview.AttachModel(Fixture());preview.SelectClip("Move");preview.SetLoop(false);
            Reject("rootmotion show true","Root Motion requires an explicit source");
            Reject("bone select Root","ambiguous names across skeletons are rejected");
            await Execute("bone select_many \"0:1, 0:2,0:1\"");
            Check(bones.SelectedBones.SequenceEqual(new[]{new BoneId(0,1),new BoneId(0,2)})&&bones.Selected==new BoneId(0,2),"multi-selection is ordered, unique, and exposes a primary bone");
            Check(bones.Overlay.SelectedMarkerCount==2,"all selected bones have live highlights");
            Reject("bone select_many \"0:0,missing\"","multi-selection validates the entire input");
            Check(bones.SelectedBones.Count==2&&bones.Selected==new BoneId(0,2),"invalid batch selection is atomic");
            await Execute("bone selection");Check(commands.LastResult.Contains("+ 0:1")&&commands.LastResult.Contains("> 0:2"),"Console reports all selections and primary marker");
            await Execute("bone add 1:1");await Execute("bone add 1:1");
            Check(bones.SelectedBones.Count==3,"add selection is idempotent across multiple rigs");
            await Execute("bone remove 1:1");Check(bones.SelectedBones.Count==2,"remove selection preserves other bones");
            preview.Seek(.317);var before=bones.WorldPose(new(0,1));int runs=bones.SamplingRuns;
            await Execute("trail show true");
            Check(bones.SamplingRuns==runs+1&&bones.Trajectories.Count==2,"one isolated sampler computes both selected trajectories");
            Check(bones.Trajectories.Select(t=>t.Color).Distinct().Count()==2&&bones.Overlay.TrailCount==2,"trajectories have distinct stable bone colors and geometry");
            Check(preview.Playhead==.317&&!preview.Playing&&bones.WorldPose(new(0,1)).IsEqualApprox(before),"batch sampling preserves live time and pose");
            foreach(var trail in bones.Trajectories)
            {
                Check(trail.Trajectory.Samples.Length==33&&trail.Trajectory.Samples[^1].Time==1.05,"both trajectories include the exact non-integral endpoint: "+trail.Id);
                bool correct=true;
                foreach(var sample in trail.Trajectory.Samples.Where((_,i)=>i%8==0))
                {preview.Seek(sample.Time);correct&=Near(sample.Position,bones.WorldPose(trail.Trajectory.Bone).Origin);}
                Check(correct,"sampled path agrees with the live multi-bone pose: "+trail.Id);
            }
            await Execute("trail pin");var ids=bones.Pins.Select(p=>p.Id).ToArray();runs=bones.SamplingRuns;
            Check(ids.Length==2&&bones.Trajectories.Count==2,"pinning selection deduplicates identical displayed paths");
            await Execute("trail pin");Check(bones.Pins.Count==2&&bones.SamplingRuns==runs,"pinning the same configuration is idempotent and reuses poses");
            await Execute("bone select 1:1");
            Check(bones.Trajectories.Count==3&&bones.SelectedBones.Count==1,"single-select API preserves pinned paths while replacing the selection");
            await Execute("trail show false");Check(bones.Trajectories.Count==2,"hiding selected trajectories retains pins");
            await Execute("bone clear");Check(bones.SelectedBones.Count==0&&bones.Trajectories.Count==2&&bones.Overlay.SelectedMarkerCount==0,"clearing selection retains pinned geometry without stale highlights");
            await Execute($"trail visible {ids[0]} false");Check(bones.Trajectories.Count==1&&bones.Pins.Count==2,"hidden pin retains its descriptor");
            await Execute($"trail visible {ids[0]} true");Check(bones.Trajectories.Count==2&&bones.SamplingRuns==runs+1,"showing a cached pin does not resample");
            Reject("trail unpin 999999","invalid pin IDs fail without removing other pins");
            Check(bones.Pins.Count==2,"invalid pin operation is atomic");
            await Execute("bone select 0:1");await Execute("trail relative 0:0");await Execute("trail rate 60");await Execute("trail pin");
            int relativePin=bones.Pins[^1].Id;
            Check(bones.Pins.Count==3&&bones.Pins[0].Reference==null&&bones.Pins[0].Rate==30,"new pins capture space and rate without modifying older pins");
            Check(bones.Trajectories.Single(t=>t.PinId==relativePin).Trajectory.Samples.All(s=>Near(s.Position,new(0,1,.5f))),"relative pin cancels ancestor translation, rotation and scale");
            await Execute("trail world");await Execute("trail rate 30");preview.Seek(.317);
            Check(bones.Trajectories.Single(t=>t.PinId==relativePin).Trajectory is {Reference:not null,Rate:60},"fixed reference and rate survive later global settings");
            var relativeRoot=(Node3D)Preview3DViewport.Descendants(bones.Overlay).OfType<BoneLabelAnchor>().Single(l=>l.Text.StartsWith("PIN "+relativePin+" ·")).GetParent();
            Check(relativeRoot.GlobalTransform.IsEqualApprox(bones.WorldPose(new(0,0))),"relative pin geometry follows its captured reference, not the global setting");
            Check(bones.Trajectories.All(t=>Near(bones.Overlay.TrailCurrentWorldPosition(t.Id)!.Value,bones.WorldPose(t.Trajectory.Bone).Origin)),"all world and relative pin markers track exact sub-sample poses");
            await Execute("trail pins");Check(commands.LastResult.Contains("relative to 0:0")&&commands.LastResult.Contains("60/s"),"Console lists independent pin settings");
            await Execute($"trail unpin {ids[1]}");Check(bones.Pins.Count==2&&bones.Trajectories.Count==2,"unpin removes only its own trajectory");
            await Execute("rootmotion source 0:0");Reject("rootmotion source 0:1","non-root bones cannot be Root Motion sources");
            Check(bones.RootMotionSource==new BoneId(0,0),"invalid Root Motion source preserves the previous source");
            var player=preview.Player!;var liveTrack=player.RootMotionTrack;preview.SetSpeed(1.5);preview.SetLoop(true);preview.Seek(.317);
            before=bones.WorldPose(new(0,0));await Execute("rootmotion show true");var motion=bones.RootMotion!;
            Check(motion.Samples.Count==33&&motion.Samples[^1].Time==1.05,"Root Motion samples a single clip with exact endpoints");
            Check(Near(motion.Samples[0].Displacement,Vector3.Zero)&&SameRotation(motion.Samples[0].RotationDelta,Quaternion.Identity),"Root Motion begins at zero displacement and identity rotation delta");
            // Model and skeleton are rotated and uniformly scaled; the animated node contributes world Z motion.
            Vector3 expectedDelta=new Vector3(3.6f,0,0)+new Quaternion(Vector3.Up,-.3f)*new Vector3(0,0,1.5f);
            Check(Near(motion.Samples[^1].Displacement,expectedDelta),"Root Motion displacement includes ancestor motion and world distances in the initial root orientation");
            Check(SameRotation(motion.Samples[^1].RotationDelta,new Quaternion(Vector3.Up,1.2f)),"Root Motion reports the full relative rotation quaternion");
            Check(Near(motion.Samples[^1].WorldPosition,ExpectedRoot(1.05).Origin),"Root Motion world path includes model and skeleton transforms");
            var mid=bones.RootMotionAt(.517);
            Check(Near(mid.WorldPosition,ExpectedRoot(.517).Origin)&&SameRotation(mid.RotationDelta,new Quaternion(Vector3.Up,1.2f*(float)(.517/1.05))),"arbitrary-time query interpolates translation and rotation");
            Check(player.RootMotionTrack==liveTrack&&player.SpeedScale==1.5f&&preview.Playhead==.317&&!preview.Playing&&bones.WorldPose(new(0,0)).IsEqualApprox(before)&&player.GetAnimation("Move").LoopMode==Animation.LoopModeEnum.Linear,"Root Motion sampling preserves playback, pose, loop, speed and engine extraction settings");
            Check(bones.Overlay.RootMotionHeadingCount>1&&Near(bones.Overlay.RootMotionWorldPosition!.Value,bones.WorldPose(new(0,0)).Origin),"Root Motion overlay includes sampled headings and an exact current marker");
            await Execute("rootmotion sample 0.517");Check(commands.LastResult.Contains("Rotation delta")&&commands.LastResult.Contains("Displacement"),"Console exposes Root Motion sample values");
            Reject("rootmotion sample -1","negative Root Motion time is rejected");Reject("rootmotion sample 2","Root Motion time beyond the clip is rejected");
            bool invalidTime=false;try{bones.RootMotionAt(double.NaN);}catch(ArgumentOutOfRangeException){invalidTime=true;}Check(invalidTime,"non-finite Root Motion time is rejected");
            await Execute("bone select 0:0");await Execute("trail relative 0:0");await Execute("trail show true");
            Check(bones.Trajectory!.Samples.All(s=>Near(s.Position,Vector3.Zero))&&bones.RootMotion!.Samples[^1].Displacement.Length()>1,"Root Motion remains world-space when ordinary root trajectory is relative to itself");
            await Execute("bone clear");Check(bones.RootMotionEnabled&&bones.RootMotion!=null&&bones.Trajectories.Count==2,"Root Motion and pins are independent of selection");
            var pins=bones.Pins.ToArray();preview.SelectClip("Spin");await Ready();
            Check(bones.Pins.SequenceEqual(pins)&&bones.RootMotion!.Clip=="Spin"&&bones.Trajectories.All(t=>t.Trajectory.Clip=="Spin"),"clip change retains pin configuration and recomputes every path");
            Check(bones.RootMotion!.Samples.All(s=>Near(s.Displacement,Vector3.Zero))&&bones.Overlay.RootMotionHeadingCount>1&&SameRotation(bones.RootMotion.Samples[^1].RotationDelta,new Quaternion(Vector3.Up,1.2f)),"rotation-only Root Motion has a stationary path and visible orientation arrows");
            preview.SelectClip("Still");await Ready();
            Check(bones.RootMotion!.Samples.All(s=>Near(s.Displacement,Vector3.Zero)&&SameRotation(s.RotationDelta,Quaternion.Identity)),"clip switch clears unkeyed motion from the previous clip");
            await Execute("rootmotion show false");Check(bones.RootMotion==null&&bones.Overlay.RootMotionWorldPosition==null&&bones.Trajectories.Count==2,"hiding Root Motion retains pinned bone paths");
            await Execute("trail clear_pins");Check(bones.Pins.Count==0&&bones.Trajectories.Count==0&&bones.Status=="Off","clear pins removes all fixed paths");

            preview.SelectClip("Move");player.GetAnimation("Move").Length=100;preview.SelectClip("Move");
            bones.SetSelection(["0:1","0:2"]);bones.ShowTrail(true);var obsolete=bones.PendingSampling;await Frames(1);
            Check(!obsolete.IsCompleted,"large multi-bone sampling yields to the scene tree");
            bones.PinSelected();var obsoletePins=bones.PendingSampling;bones.ShowRootMotion(true);var obsoleteMotion=bones.PendingSampling;
            preview.Clear();await Task.WhenAll(obsolete,obsoletePins,obsoleteMotion);await Frames();
            Check(bones.SelectedBones.Count==0&&bones.Pins.Count==0&&bones.RootMotionSource==null&&bones.RootMotion==null&&bones.Trajectories.Count==0,"asset clear cancels all work and removes selection, pins and Root Motion");
            Check(!Preview3DViewport.Descendants(preview.View).Any(n=>n.Name=="TrajectorySampler"),"cancelled generations free every private sampling scene");
            preview.AttachModel(Fixture());preview.SelectClip("Move");bones.SetSelection(["0:1","0:2"]);bones.ShowTrail(true);obsolete=bones.PendingSampling;
            bones.SetSelection(["1:1","1:2"]);await obsolete;await Ready();
            Check(bones.Trajectories.Count==2&&bones.Trajectories.All(t=>t.Trajectory.Bone.Skeleton==1),"rapid multi-selection never publishes obsolete trajectories");
            bones.PinSelected();await Ready();Check(bones.Pins.All(p=>p.Id>relativePin),"pin IDs are not recycled after an asset change");

            // Allocation limits reject oversized work before cloning a sampler.
            bones.ClearPins();bones.ShowTrail(false);bones.SetSelection(["0:0","0:1","0:2","1:0","1:1","1:2"]);
            preview.Player!.GetAnimation("Move").Length=4001;preview.SelectClip("Move");
            Reject("trail show true","per-track sampling limit rejects oversized clips");
            preview.Player.GetAnimation("Move").Length=4000;preview.SelectClip("Move");
            for(int i=0;i<4;i++)preview.Bones.Skeletons[1].Node.AddBone("Extra"+i);
            bones.AddSelection("1:3");bones.AddSelection("1:4");bones.AddSelection("1:5");bones.AddSelection("1:6");runs=bones.SamplingRuns;
            bones.ShowTrail(true);
            Check(bones.Status.StartsWith("Unavailable:")&&bones.SamplingRuns==runs&&bones.Trajectories.Count==0,"aggregate pose-sample budget prevents excessive allocation");
            bones.ShowTrail(false);bones.SetSelection(["0:0","0:1","0:2","1:0","1:1","1:2"]);
            preview.Player.GetAnimation("Move").Length=2000;preview.SelectClip("Move");
            foreach(string reference in new[]{"0:0","0:1","0:2"}){bones.SetReference(reference);bones.PinSelected();}
            Check(bones.Status.Contains("displayed-sample budget")&&bones.SamplingRuns==runs,"multiple reference configurations also respect the displayed-sample budget");
            var overBudget=bones.PendingSampling;preview.Clear();await overBudget;

            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples")]);await Execute("preview load StudioBot_Walk");
            await Execute("bone select_many \"LeftFoot,RightFoot\"");await Execute("trail show true");await Execute("trail pin");await Execute("rootmotion source Hips");await Execute("rootmotion show true");
            Check(bones.Trajectories.Count==2&&bones.RootMotion!=null,"multi-path and Root Motion work on a real imported animation");
            bones.ShowSkeleton(true);preview.Seek(.3);preview.SetSpeed(1);
            if(DisplayServer.GetName()!="headless")
            {
                owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,900);owner.Workspace.FocusPanel("preview");preview.FrameModel();await Frames(10);
                preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var image=preview.View.GetTexture().GetImage();image.SavePng(Path.Combine(folder,"multi-trajectories.png"));
                Check(image.GetWidth()>200,"rendered multi-path and Root Motion evidence captured");
            }
            int meshes=preview.MeshCount,boneCount=preview.BoneCount;
            string output=ExportService.Export(preview,folder,"multi-overlay",false);preview.LoadFile(output);
            Check(preview.MeshCount==meshes&&preview.BoneCount==boneCount&&!Preview3DViewport.Descendants(preview.Model!).Any(n=>n.Name.ToString().Contains("Trajectory")||n.Name=="RootMotion"),"GLB export excludes all multi-path and Root Motion geometry");
            string report=$"PASS: {checks.Count} multi-bone / pinned trajectory / Root Motion checks\n"+string.Join("\n",checks)+"\nOutput: "+folder;
            await File.WriteAllTextAsync(Path.Combine(folder,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("MULTI BONE TEST FAILED: "+ex);await File.WriteAllTextAsync(Path.Combine(folder,"results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }

    private static Transform3D ModelTransform()=>new(new Basis(Vector3.Up,.25f).Scaled(Vector3.One*1.5f),new(3,1,-2));
    private static Transform3D SkeletonTransform()=>new(new Basis(Vector3.Up,.3f).Scaled(Vector3.One*1.2f),new(-.2f,.3f,.4f));
    private static Transform3D ExpectedRoot(double time)
    {float t=(float)(time/1.05);return ModelTransform()*new Transform3D(Basis.Identity,new(.4f,0,t))*SkeletonTransform()*new Transform3D(new Basis(Vector3.Up,1.2f*t),new(2*t,0,0));}
    private static Node3D Fixture()
    {
        var model=new Node3D{Name="Fixture",Transform=ModelTransform()};var pivot=new Node3D{Name="Pivot",Position=new(.4f,0,0)};model.AddChild(pivot);
        var skeleton=Skeleton("Skeleton");skeleton.Transform=SkeletonTransform();pivot.AddChild(skeleton);
        var second=Skeleton("Second");second.Position=new(4,0,0);model.AddChild(second);
        var player=new AnimationPlayer{Name="Player"};model.AddChild(player);var library=new AnimationLibrary();
        foreach(string name in new[]{"Move","Spin","Still"})
        {
            var animation=new Animation{Length=1.05};int track;
            if(name=="Move")
            {
                track=animation.AddTrack(Animation.TrackType.Position3D);animation.TrackSetPath(track,"Pivot/Skeleton:Root");animation.PositionTrackInsertKey(track,0,Vector3.Zero);animation.PositionTrackInsertKey(track,1.05,new(2,0,0));
                track=animation.AddTrack(Animation.TrackType.Position3D);animation.TrackSetPath(track,"Pivot");animation.PositionTrackInsertKey(track,0,new(.4f,0,0));animation.PositionTrackInsertKey(track,1.05,new(.4f,0,1));
            }
            track=animation.AddTrack(Animation.TrackType.Rotation3D);animation.TrackSetPath(track,"Pivot/Skeleton:Root");animation.RotationTrackInsertKey(track,0,Quaternion.Identity);animation.RotationTrackInsertKey(track,1.05,name=="Still"?Quaternion.Identity:new Quaternion(Vector3.Up,1.2f));
            library.AddAnimation(name,animation);
        }
        player.AddAnimationLibrary("",library);return model;
    }
    private static Skeleton3D Skeleton(string name)
    {
        var skeleton=new Skeleton3D{Name=name};skeleton.AddBone("Root");skeleton.AddBone("Left");skeleton.AddBone("Right");
        for(int i=1;i<=2;i++){skeleton.SetBoneParent(i,0);skeleton.SetBoneRest(i,new(Basis.Identity,new Vector3(0,1,i==1?.5f:-.5f)));}
        skeleton.ResetBonePoses();return skeleton;
    }
}
