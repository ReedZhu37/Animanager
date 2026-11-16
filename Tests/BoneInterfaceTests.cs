using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Preview;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Workspace;
using System.Text.Json;

namespace Animanager.Tests;

public static class BoneInterfaceTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var folder=ProjectSettings.GlobalizePath("res://TestResults/bone-ui-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
        var checks=new List<string>();var host=owner.Workspace;var panel=host.Panel("bones");var bones=preview.Bones;bool mouseNotified=false;
        T Find<T>(string name) where T:Node=>Preview3DViewport.Descendants(panel).OfType<T>().Single(c=>c.Name==name);
        var tree=Find<Tree>("BoneTree");var search=Find<LineEdit>("BoneSearch");var trail=Find<CheckButton>("BoneTrail");
        var reference=Find<OptionButton>("BoneReference");var rate=Find<OptionButton>("BoneRate");
        void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add(name);GD.Print("PASS "+name);}
        async Task Frames(int n=12){for(int i=0;i<n;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        IEnumerable<TreeItem> Items(TreeItem? parent=null)
        {
            parent??=tree.GetRoot();if(parent==null)yield break;
            foreach(var child in parent.GetChildren()){yield return child;foreach(var nested in Items(child))yield return nested;}
        }
        TreeItem Item(string name)=>Items().Single(i=>i.GetText(0)==name);
        async Task Click(Vector2 position)
        {
            var viewport=owner.GetViewport();if(!mouseNotified&&DisplayServer.GetName()=="headless"){viewport.NotifyMouseEntered();mouseNotified=true;}
            // Native windows derive button hover from the OS cursor; keep it aligned with injected input.
            if(DisplayServer.GetName()!="headless"){Input.WarpMouse(position*owner.GetWindow().ContentScaleFactor);await Frames(6);Input.FlushBufferedEvents();}
            viewport.PushInput(new InputEventMouseMotion{Position=position,GlobalPosition=position},true);
            viewport.PushInput(new InputEventMouseButton{Position=position,GlobalPosition=position,ButtonIndex=MouseButton.Left,Pressed=true},true);
            viewport.PushInput(new InputEventMouseButton{Position=position,GlobalPosition=position,ButtonIndex=MouseButton.Left,Pressed=false},true);await Frames();
        }
        async Task ClickControl(Control control)
        {
            var body=(ScrollContainer)panel.GetParent();body.EnsureControlVisible(control);await Frames();
            await Click(control.GetGlobalRect().GetCenter());
        }
        async Task ClickBone(string name)
        {
            var item=Item(name);for(var p=item.GetParent();p!=null;p=p.GetParent())p.Collapsed=false;
            ((ScrollContainer)panel.GetParent()).EnsureControlVisible(tree);tree.ScrollToItem(item);await Frames();
            var rect=tree.GetItemAreaRect(item);var local=new Vector2(Math.Min(tree.Size.X-20,rect.Position.X+90),rect.GetCenter().Y);
            Check(tree.GetItemAtPosition(local)==item,"mouse target resolves to "+name);
            await Click(tree.GetGlobalTransformWithCanvas()*local);
        }
        async Task Query(string value){search.Text=value;search.EmitSignal(LineEdit.SignalName.TextChanged,value);await Frames();}
        async Task Execute(string command)
        {owner.GetNode("/root/LimboConsole").Call("execute_command",command);await commands.PendingTask;await Frames();if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        async Task Choose(OptionButton button,int index)
        {
            // A headless display has no native popup placement; test opening independently of mouse release.
            if(DisplayServer.GetName()=="headless"){button.ShowPopup();await Frames();}else await ClickControl(button);
            var popup=button.GetPopup();Check(popup.Visible,"opens "+button.Name+" dropdown");
            void KeyPress(Key key)
            {
                Input.ParseInputEvent(new InputEventKey{WindowId=owner.GetWindow().GetWindowId(),Keycode=key,Pressed=true});
                Input.ParseInputEvent(new InputEventKey{WindowId=owner.GetWindow().GetWindowId(),Keycode=key,Pressed=false});
            }
            popup.SetFocusedItem(0);for(int i=0;i<index;i++){KeyPress(Key.Down);await Frames(1);}KeyPress(Key.Enter);
            await Frames();await bones.PendingSampling;await Frames();
        }
        try
        {
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);if(DisplayServer.GetName()!="headless")owner.GetWindow().GrabFocus();host.UsePreset("animation");await Frames(30);
            Check(host.IsPanelVisible("bones")&&host.IsPanelVisible("preview"),"animation workspace exposes the bone panel beside preview");
            Check(trail.Disabled&&Find<Label>("BoneEmpty").Text.Contains("选择一个资产"),"unloaded panel has empty guidance and disabled trajectory");
            var legacy=new WorkspaceLayout();legacy.CompactOrder.Remove("bones");legacy.Hidden.Remove("bones");legacy.Normalize();
            Check(legacy.Hidden.Contains("bones")&&legacy.CompactOrder.Contains("bones"),"older workspace layouts retain content and expose the new hidden panel");
            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples")]);await Execute("preview load StudioBot_Walk");
            Check(Items().Count(i=>i.GetMetadata(0).AsString().StartsWith("0:"))==16,"loaded asset populates the complete bone tree");
            Check(Item("LeftFoot").GetParent()==Item("LeftLowerLeg"),"tree uses real parent-child relationships");
            Item("Chest").Collapsed=true;
            await Query("lEfTfOoT");Check(Items().Count()==5&&Items().Any(i=>i.GetText(0)=="Hips")&&!Items().Any(i=>i.GetText(0)=="RightFoot"),"search ignores case and retains only matching ancestors");
            await ClickBone("LeftFoot");
            Check(bones.Selected==bones.Resolve("LeftFoot")&&bones.Overlay.SelectedWorldPosition.HasValue,"mouse selection updates shared selection and 3D highlight");
            await Query("zz_missing");Check(Find<Label>("BoneEmpty").Visible&&bones.Selected==bones.Resolve("LeftFoot")&&Find<Label>("BoneSelection").Text.Contains("筛选"),"empty search preserves selection and explains the filtered selection");
            await ClickControl(Find<Button>("BoneLocate"));Check(search.Text.Length==0&&tree.GetSelected()==Item("LeftFoot"),"locate clears the filter and reveals the selected bone");
            Check(Item("Chest").Collapsed,"search preserves unrelated collapse state");
            await ClickControl(trail);await bones.PendingSampling;await Frames();
            Check(bones.TrailEnabled&&bones.Status=="Ready"&&trail.ButtonPressed,"mouse trajectory toggle computes and displays a path");
            Check(Find<Label>("BoneStatus").Text.Contains("采样点"),"async ready state updates the panel without polling the tree");
            int hips=Enumerable.Range(0,reference.ItemCount).Single(i=>reference.GetItemText(i).Contains("Hips"));
            await Choose(reference,hips);Check(bones.Reference==bones.Resolve("Hips")&&bones.Trajectory!.Reference==bones.Reference,"reference menu switches to the explicitly chosen bone space");
            await Choose(reference,0);Check(bones.Reference==null&&bones.Trajectory!.Reference==null,"reference menu returns to world space");
            await Choose(rate,1);Check(bones.SampleRate==60&&bones.Trajectory!.Rate==60,"UI sampling rate drives the shared sampler");
            await Execute("bone select RightFoot");Check(tree.GetSelected()==Item("RightFoot"),"Console selection updates the UI tree");
            await Execute("trail relative Hips");Check(reference.Selected==hips,"Console reference changes update the UI menu");
            await Execute("trail show false");Check(!trail.ButtonPressed,"Console trajectory visibility updates the UI switch");
            await Execute("bone show true");await Execute("bone xray false");
            Check(Find<CheckButton>("BoneShow").ButtonPressed&&!Find<CheckButton>("BoneXray").ButtonPressed,"Console overlay settings synchronize with UI toggles");
            var selectedItem=Item("RightFoot");ulong itemId=selectedItem.GetInstanceId();
            preview.TogglePlay();await Frames(25);Check(Item("RightFoot").GetInstanceId()==itemId,"playback updates pose text without rebuilding tree items");preview.TogglePlay();
            await Execute("preview seek 0.31");await Frames(30);Check(Find<Label>("BonePose").Text.Contains("0.31"),"scrubbing updates selected bone time in the panel");
            Check(Math.Abs(Preview3DViewport.Descendants(host.Panel("preview")).OfType<HSlider>().Single().Value-.31)<.001,"paused Console scrubbing also synchronizes the preview timeline");
            await Query("Foot");var selection=bones.Selected;var panelId=panel.GetInstanceId();
            host.MovePanel("bones",host.Layout.GroupFor("preview")!.Id,DockEdge.Center);await Frames();
            Check(panel.GetInstanceId()==panelId&&search.Text=="Foot"&&bones.Selected==selection,"docking preserves search, selection, and panel instance");
            host.MovePanel("bones",host.Layout.GroupFor("preview")!.Id,DockEdge.Left);await Frames();
            Check(host.Layout.GroupFor("bones")!=host.Layout.GroupFor("preview"),"bone panel undocks beside preview");
            host.HidePanel("bones");await Frames();await Execute("bone select LeftHand");host.ShowPanel("bones");await Frames();
            Check(Find<Label>("BoneSelection").Text.Contains("LeftHand"),"hidden panel catches up with Console selection when restored");
            var prefs=new WorkspacePreferences{Layout=host.Layout.Clone()};string path=Path.Combine(folder,"workspace.json");prefs.Save(path);
            var restored=WorkspacePreferences.Load(path,out var error);host.Restore(restored.Layout);await Frames();
            Check(error.Length==0&&host.Layout.GroupFor("bones")!=null&&panel.GetInstanceId()==panelId,"bone docking layout persists and restores without reloading preview");
            await Execute("preview load StudioBot_Rig");
            Check(search.Text.Length==0&&tree.GetSelected()==null&&reference.Selected==0&&trail.Disabled&&Find<Label>("BoneStatus").Text.Contains("没有可用动画"),"asset switch clears stale filters and selection; static rig disables trajectory");
            await ClickBone("LeftFoot");Check(bones.Selected!=null&&trail.Disabled,"static rig remains selectable");
            preview.AttachModel(new Node3D{Name="Empty"});await Frames();
            Check(Find<Label>("BoneEmpty").Text.Contains("没有骨架")&&reference.Disabled&&Find<Button>("BoneFocus").Disabled,"skeleton-free asset clears choices and disables bone actions");
            var multi=new Node3D{Name="Multiple"};
            foreach(var name in new[]{"A","B"}){var s=new Skeleton3D{Name=name};s.AddBone("root");s.AddBone("手指_很长的骨骼名称_abcdefghijklmnopqrstuvwxyz");s.SetBoneParent(1,0);multi.AddChild(s);}
            preview.AttachModel(multi);await Frames();await Query("手指");
            var duplicate=Items().Single(i=>i.GetMetadata(0).AsString()=="1:1");duplicate.Select(0);tree.EmitSignal(Tree.SignalName.MultiSelected,duplicate,0,true);await Frames();
            Check(bones.Selected==new BoneId(1,1)&&Items().Count()==6,"Chinese search and duplicate names select the correct skeleton by identity");
            Check(reference.ItemCount==5&&reference.GetItemText(2)!=reference.GetItemText(4),"reference menu disambiguates identical names across skeletons");
            await Execute("preview load StudioBot_Walk");await Query("Foot");await ClickBone("LeftFoot");await ClickControl(trail);await bones.PendingSampling;await Frames();
            host.UsePreset("animation");bones.SetXray(true);preview.Seek(.3);await Frames(25);
            if(DisplayServer.GetName()!="headless")await Screenshot("desktop.png");
            owner.GetWindow().Size=new(720,400);host.ShowPanel("bones");await Frames(30);
            Check(host.Compact&&host.IsPanelVisible("bones")&&search.Size.X>200&&tree.Size.Y>=170,"720 x 400 compact workspace keeps tree reachable");
            await ClickControl(trail);Check(!bones.TrailEnabled,"small-window panel scroll reaches and operates trajectory switch");
            for(int scale=1;scale<=2;scale++)
            {
                owner.DisplayScale.SetScale(scale);owner.GetWindow().Size=new(1440,900);host.ShowPanel("bones");await Frames(30);
                Check(panel.Size.X<=host.Size.X&&reference.Size.X<=panel.Size.X+1,"bone controls stay within panel bounds at "+scale+"x scale");
            }
            if(DisplayServer.GetName()!="headless")await Screenshot("scaled-200.png");
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);host.UsePreset("animation");await Frames(30);
            preview.Seek(.3);await Frames();Check(bones.Selected==bones.Resolve("LeftFoot"),"resize and DPI changes retain bone selection");
            var report=$"PASS: {checks.Count} bone UI checks\n"+string.Join("\n",checks)+"\nDisplay server: "+DisplayServer.GetName();File.WriteAllText(Path.Combine(folder,"results.txt"),report);GD.Print(report+"\n"+folder);owner.GetTree().Quit(0);
        }
        catch(Exception ex)
        {
            GD.PushError("BONE UI TEST FAILED: "+ex);File.WriteAllText(Path.Combine(folder,"results.txt"),$"{checks.Count} passed\n"+ex);
            if(DisplayServer.GetName()!="headless")await Screenshot("failure.png");
            GD.Print(folder);owner.GetTree().Quit(1);
        }

        async Task Screenshot(string file)
        {
            preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=owner.GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(folder,file));
        }
    }
}
