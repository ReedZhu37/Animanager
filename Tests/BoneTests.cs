using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Preview;
using Animanager.Core.Pipeline;
using Animanager.UI;
using Animanager.UI.Components;

namespace Animanager.Tests;

public static class BoneTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var root=ProjectSettings.GlobalizePath("res://TestResults/bones-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        var checks=new List<string>();var console=owner.GetNode("/root/LimboConsole");
        void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add(label);GD.Print("PASS "+label);}
        async Task Frames(int n=2){for(int i=0;i<n;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Execute(string command)
        {
            console.Call("execute_command",command);await commands.PendingTask;
            if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);
            if(preview.Bones.Status.StartsWith("Failed:"))throw new Exception(preview.Bones.Status);
        }
        bool Near(Vector3 a,Vector3 b)=>a.DistanceTo(b)<.0002f;
        try
        {
            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples")]);
            await Execute("preview load StudioBot_Walk");
            await Execute("preview bones");Check(commands.LastResult.Contains("Skeleton 0:")&&commands.LastResult.Contains("LeftFoot"),"real Limbo dispatch prints skeleton hierarchy and IDs");
            await Execute("bone tree leftfoot");Check(commands.LastResult.Contains("Hips")&&commands.LastResult.Contains("LeftLowerLeg")&&!commands.LastResult.Contains("RightFoot"),"case-insensitive filtering retains ancestors only");
            await Execute("bone select LeftFoot");var bones=preview.Bones;var selected=bones.Selected!.Value;
            Check(bones.Name(selected)=="LeftFoot"&&bones.Overlay.SelectedWorldPosition.HasValue,"bone selection enables the highlight");
            await Execute("bone info");Check(commands.LastResult.Contains("Local position")&&commands.LastResult.Contains("World position")&&commands.LastResult.Contains("Parent:"),"bone info reports parent and coordinate spaces");
            await Execute("bone show true");await Execute("bone xray false");Check(bones.Overlay.ShowSkeleton&&!bones.Overlay.Xray,"overlay visibility and depth commands dispatch typed booleans");
            await Execute("bone xray true");await Execute("bone focus");
            preview.Seek(.317);double before=preview.Playhead;var poseBefore=bones.WorldPose(selected);
            await Execute("trail show true");
            Check(bones.Status=="Ready"&&bones.Trajectory!.Samples.Length==37&&bones.Trajectory.Samples[^1].Time==preview.Duration,"30 Hz trajectory includes exact endpoints without a near-duplicate float endpoint");
            Check(!preview.Playing&&preview.Playhead==before&&bones.WorldPose(selected).IsEqualApprox(poseBefore),"sampling preserves paused playhead and actual pose");
            Check(bones.Trajectory!.Samples.Select(p=>p.Position).Distinct().Count()>5,"unkeyed foot moves through animated ancestors");
            foreach(var sample in bones.Trajectory.Samples.Where((_,i)=>i%7==0))
            {preview.Seek(sample.Time);Check(Near(sample.Position,bones.WorldPose(selected).Origin),"StudioBot trajectory matches actual seek at "+sample.Time.ToString("F3"));}
            preview.Seek(.317);
            Check(Near(bones.Overlay.SelectedWorldPosition!.Value,bones.WorldPose(selected).Origin)&&Near(bones.Overlay.CurrentWorldPosition!.Value,bones.WorldPose(selected).Origin),"selection and current marker follow sub-sample seek exactly");
            Check(bones.Overlay.SegmentCount==bones.Trajectory.Samples.Length-1,"trajectory does not connect the loop endpoint back to the start");
            var savedTrajectory=bones.Trajectory;owner.Workspace.ShowPanel("preview");await Frames(20);
            owner.Workspace.FocusPanel("preview");await Frames(5);owner.Workspace.FocusPanel("preview");await Frames(5);
            Check(bones.Selected==selected&&ReferenceEquals(bones.Trajectory,savedTrajectory)&&Math.Abs(preview.Playhead-.317)<.0001,"workspace reparenting retains bone selection, trajectory, and playhead");
            int runs=bones.SamplingRuns;await Execute("trail show false");await Execute("trail show true");
            Check(bones.SamplingRuns==runs,"same selection and clip reuse cached trajectory");
            preview.TogglePlay();await Execute("trail rate 60");await Frames(3);
            Check(preview.Playing&&bones.Trajectory!.Rate==60,"60 Hz resampling leaves animation playing");
            runs=bones.SamplingRuns;preview.SetSpeed(2);preview.Zoom(.9f);await Frames();
            Check(bones.SamplingRuns==runs,"camera and playback speed changes do not resample");preview.TogglePlay();
            await Execute("trail relative Hips");Check(bones.Trajectory!.Reference==bones.Resolve("Hips"),"reference-bone mode is applied");
            preview.Seek(.41);Check(Near(bones.Overlay.CurrentWorldPosition!.Value,bones.WorldPose(selected).Origin),"relative current marker remains attached to the live bone");
            // Export while overlays and a trajectory exist; only model nodes should round-trip.
            int meshes=preview.MeshCount,count=preview.BoneCount;
            string glb=ExportService.Export(preview,root,"with-overlay",false);preview.LoadFile(glb);
            Check(preview.MeshCount==meshes&&preview.BoneCount==count&&!Preview3DViewport.Descendants(preview.Model!).Any(n=>n.Name.ToString().Contains("Overlay")),"GLB export excludes all overlay geometry");
            Check(bones.Selected==null&&bones.Trajectory==null&&!bones.TrailEnabled,"loading a different asset clears selection and trajectory");
            await Execute("preview load StudioBot_Rig");await Execute("bone select LeftFoot");
            console.Call("execute_command","trail show true");Check(commands.LastResult.StartsWith("ERROR:")&&!bones.TrailEnabled,"static rig still supports selection and rejects trajectory clearly");
            preview.AttachModel(new Node3D{Name="Empty"});Check(bones.Tree().Contains("No skeletons"),"model without a skeleton has an explicit empty state");

            preview.AttachModel(Fixture());
            console.Call("execute_command","bone select Tip");Check(commands.LastResult.Contains("Ambiguous")&&bones.Selected==null,"duplicate bone names require a unique skeleton:bone ID");
            console.Call("execute_command","bone select 9:99");Check(commands.LastResult.StartsWith("ERROR:"),"invalid bone IDs do not change selection");
            await Execute("bone select 0:1");preview.SelectClip("Move");preview.SetLoop(false);preview.Seek(.4);
            await Execute("trail rate 30");await Execute("trail world");await Execute("trail show true");
            var trajectory=bones.Trajectory!;
            Check(Math.Abs(trajectory.Samples[^1].Time-1.05)<1e-8&&trajectory.Samples.Length==33,"non-integral clip duration has an exact terminal sample");
            foreach(var sample in trajectory.Samples.Where((_,i)=>i%8==0))
            {
                preview.Seek(sample.Time);var expected=ExpectedTip(sample.Time);
                Check(Near(sample.Position,expected)&&Near(bones.WorldPose(new(0,1)).Origin,expected),"analytical world-space result includes model, node, skeleton, and parent transforms at "+sample.Time.ToString("F3"));
            }
            await Execute("trail relative 0:0");
            Check(bones.Trajectory!.Samples.All(p=>Near(p.Position,new Vector3(0,1,0))),"reference space removes root translation, rotation, and inherited scale");
            await Execute("bone select 0:0");Check(bones.Trajectory!.Samples.All(p=>p.Position.Length()<.0002f),"reference bone relative to itself has zero trajectory");
            await Execute("bone select 0:1");preview.SelectClip("Still");await bones.PendingSampling;
            Check(bones.Selected==new BoneId(0,1)&&bones.Trajectory!.Clip=="Still","clip switching preserves selection and recomputes trajectory");
            Check(bones.Skeletons[0].Node.GetBonePoseRotation(0).IsEqualApprox(Quaternion.Identity),"clip switching resets unkeyed rotation channels to imported pose");
            await Execute("trail world");Check(bones.Trajectory!.Samples.All(p=>Near(p.Position,bones.Trajectory.Samples[0].Position)),"static clip after moving clip has no stale motion");
            console.Call("execute_command","trail rate 29");Check(commands.LastResult.StartsWith("ERROR:")&&bones.SampleRate==30,"invalid sampling rate is rejected without state mutation");
            preview.SetLoop(true);preview.SelectClip("Move");await bones.PendingSampling;
            Check(preview.Player!.GetAnimation("Move").LoopMode==Animation.LoopModeEnum.Linear,"sampling does not change live clip loop mode");
            preview.Player.GetAnimation("Move").Length=100;
            preview.SelectClip("Move");var stale=bones.PendingSampling;await Frames();
            Check(!stale.IsCompleted,"long clip sampling yields to the scene tree");
            preview.Clear();await stale;await Frames();
            Check(bones.Trajectory==null&&bones.Selected==null&&!Preview3DViewport.Descendants(preview.View).Any(n=>n.Name=="TrajectorySampler"),"asset clear cancels in-flight sampling and frees its private scene");
            preview.AttachModel(Fixture());bones.Select("0:1");preview.SelectClip("Move");bones.ShowTrail(true);var obsolete=bones.PendingSampling;
            bones.Select("1:1");await obsolete;await bones.PendingSampling;
            Check(bones.Trajectory!.Bone==new BoneId(1,1),"rapid selection cannot publish an obsolete result");
            bones.ShowTrail(false);Check(bones.Trajectory==null&&bones.Overlay.CurrentWorldPosition==null,"hiding trajectory removes current marker");
            bones.ClearSelection();Check(bones.Selected==null&&bones.Overlay.SelectedWorldPosition==null,"clear selection removes highlight");

            // Official FBX skeletons and a real clip exercise the non-fixture path as well.
            foreach(var name in new[]{"UE4_Mannequin","UE5_Manny","UE5_Manny_Idle"})
            {
                preview.LoadFile(RuntimePaths.Bundled("BuiltinAssets/Baselines/"+name+".fbx"));
                Check(bones.Skeletons.Sum(s=>s.Node.GetBoneCount())==(name.StartsWith("UE4")?68:89),name+" exposes expected real hierarchy");
            }
            bones.Select("foot_l");bones.SetRate(30);bones.ShowTrail(true);await bones.PendingSampling;
            Check(bones.Status=="Ready","official Manny FBX foot trajectory samples successfully");
            foreach(var sample in bones.Trajectory!.Samples.Where((_,i)=>i%30==0).Take(5))
            {preview.Seek(sample.Time);Check(Near(sample.Position,bones.WorldPose(bones.Selected!.Value).Origin),"official FBX path agrees with preview at "+sample.Time.ToString("F3"));}
            if(DisplayServer.GetName()!="headless")
            {
                // Deterministic rendered evidence produced by the real preview viewport.
                await Execute("preview load StudioBot_Walk");await Execute("bone select LeftFoot");await Execute("bone show true");await Execute("trail show true");preview.Seek(.3);
                owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,900);owner.Workspace.FocusPanel("preview");preview.FrameModel();await Frames(10);
                preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var image=preview.View.GetTexture().GetImage();image.SavePng(Path.Combine(root,"bone-trajectory.png"));
                Check(image.GetWidth()>200,"rendered preview evidence captured");
            }
            var report=$"PASS: {checks.Count} bone / trajectory checks\n"+string.Join("\n",checks)+"\nOutput: "+root;
            await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("BONE TEST FAILED: "+ex);await File.WriteAllTextAsync(Path.Combine(root,"results.txt"),ex.ToString());owner.GetTree().Quit(1);}
    }

    private static Node3D Fixture()
    {
        var root=new Node3D{Name="Fixture",Transform=RootTransform()};
        var pivot=new Node3D{Name="Pivot",Position=new(.4f,0,0)};root.AddChild(pivot);
        var skeleton=Skeleton("Skeleton");skeleton.Transform=SkeletonTransform();pivot.AddChild(skeleton);
        var second=Skeleton("Second");second.Position=new(3,0,0);root.AddChild(second);
        var player=new AnimationPlayer{Name="Player"};root.AddChild(player);var library=new AnimationLibrary();
        var move=new Animation{Length=1.05};
        int track=move.AddTrack(Animation.TrackType.Position3D);move.TrackSetPath(track,"Pivot/Skeleton:Root");move.PositionTrackInsertKey(track,0,Vector3.Zero);move.PositionTrackInsertKey(track,1.05,new(2,0,0));
        track=move.AddTrack(Animation.TrackType.Rotation3D);move.TrackSetPath(track,"Pivot/Skeleton:Root");move.RotationTrackInsertKey(track,0,Quaternion.Identity);move.RotationTrackInsertKey(track,1.05,new Quaternion(Vector3.Forward,Mathf.Pi/2));
        track=move.AddTrack(Animation.TrackType.Position3D);move.TrackSetPath(track,"Pivot");move.PositionTrackInsertKey(track,0,new(.4f,0,0));move.PositionTrackInsertKey(track,1.05,new(.4f,0,1));
        library.AddAnimation("Move",move);
        var still=new Animation{Length=.5};track=still.AddTrack(Animation.TrackType.Position3D);still.TrackSetPath(track,"Pivot/Skeleton:Root");still.PositionTrackInsertKey(track,0,Vector3.Zero);still.PositionTrackInsertKey(track,.5,Vector3.Zero);library.AddAnimation("Still",still);
        player.AddAnimationLibrary("",library);return root;
    }
    private static Skeleton3D Skeleton(string name)
    {
        var s=new Skeleton3D{Name=name};s.AddBone("Root");s.AddBone("Tip");s.SetBoneParent(1,0);s.SetBoneRest(1,new(Basis.Identity,new Vector3(0,1,0)));s.ResetBonePoses();return s;
    }
    private static Transform3D RootTransform()=>new(new Basis(Vector3.Up,.2f).Scaled(Vector3.One*1.7f),new(3,1,-2));
    private static Transform3D SkeletonTransform()=>new(new Basis(Vector3.Back,.3f).Scaled(new Vector3(1.4f,.9f,1.2f)),new(-.2f,.3f,.4f));
    private static Vector3 ExpectedTip(double time)
    {
        float t=(float)(time/1.05);
        return RootTransform()*new Transform3D(Basis.Identity,new(.4f,0,t))*SkeletonTransform()*new Transform3D(new Basis(new Quaternion(Vector3.Forward,t*Mathf.Pi/2)),new(t*2,0,0))*new Vector3(0,1,0);
    }
}
