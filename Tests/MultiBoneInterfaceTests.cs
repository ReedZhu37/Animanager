using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Preview;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Workspace;

namespace Animanager.Tests;

public static class MultiBoneInterfaceTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        string folder=ProjectSettings.GlobalizePath("res://TestResults/multi-bone-ui-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
        var checks=new List<string>();var host=owner.Workspace;var panel=host.Panel("bones");var bones=preview.Bones;bool mouseEntered=false;
        T Find<T>(string name) where T:Node=>Preview3DViewport.Descendants(panel).OfType<T>().Single(n=>n.Name==name);
        var tree=Find<Tree>("BoneTree");var search=Find<LineEdit>("BoneSearch");var trail=Find<CheckButton>("BoneTrail");
        var reference=Find<OptionButton>("BoneReference");var rate=Find<OptionButton>("BoneRate");
        var pinButton=Find<Button>("BonePin");var rootSource=Find<OptionButton>("BoneRootSource");var rootMotion=Find<CheckButton>("BoneRootMotion");
        void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add(label);GD.Print("PASS "+label);}
        async Task Frames(int n=8){for(int i=0;i<n;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Ready(){await bones.PendingSampling;await Frames();if(bones.Status is not ("Ready" or "Off"))throw new Exception(bones.Status);}
        IEnumerable<TreeItem> Items(TreeItem? parent=null)
        {parent??=tree.GetRoot();if(parent==null)yield break;foreach(var item in parent.GetChildren()){yield return item;foreach(var nested in Items(item))yield return nested;}}
        TreeItem Item(string id)=>Items().Single(i=>i.GetMetadata(0).AsString()==id);
        bool Selected(params string[] ids)=>bones.SelectedBones.ToHashSet().SetEquals(ids.Select(bones.Resolve));
        async Task ScrollTo(Control control)
        {
            for(var parent=control.GetParent();parent!=null;parent=parent.GetParent())
                if(parent is ScrollContainer scroll){scroll.EnsureControlVisible(control);await Frames();}
        }
        async Task Click(Vector2 position,bool ctrl=false,bool shift=false)
        {
            var viewport=owner.GetViewport();if(!mouseEntered&&DisplayServer.GetName()=="headless"){viewport.NotifyMouseEntered();mouseEntered=true;}
            if(DisplayServer.GetName()!="headless"){Input.WarpMouse(position*owner.GetWindow().ContentScaleFactor);await Frames(4);Input.FlushBufferedEvents();}
            viewport.PushInput(new InputEventMouseMotion{Position=position,GlobalPosition=position,CtrlPressed=ctrl,ShiftPressed=shift},true);
            viewport.PushInput(new InputEventMouseButton{Position=position,GlobalPosition=position,ButtonIndex=MouseButton.Left,Pressed=true,CtrlPressed=ctrl,ShiftPressed=shift},true);
            viewport.PushInput(new InputEventMouseButton{Position=position,GlobalPosition=position,ButtonIndex=MouseButton.Left,Pressed=false,CtrlPressed=ctrl,ShiftPressed=shift},true);await Frames();
        }
        async Task ClickControl(Control control){await ScrollTo(control);await Click(control.GetGlobalRect().GetCenter());}
        async Task ClickBone(string name,bool ctrl=false,bool shift=false)
        {
            var item=Item(BoneInspection.IdText(bones.Resolve(name)));for(var p=item.GetParent();p!=null;p=p.GetParent())p.Collapsed=false;
            await ScrollTo(tree);tree.ScrollToItem(item);await Frames();
            var area=tree.GetItemAreaRect(item);var local=new Vector2(Math.Min(tree.Size.X-20,area.Position.X+90),area.GetCenter().Y);
            if(tree.GetItemAtPosition(local)!=item)throw new Exception("Wrong tree click target: "+name);
            await Click(tree.GetGlobalTransformWithCanvas()*local,ctrl,shift);
        }
        async Task Query(string query){search.Text=query;search.EmitSignal(LineEdit.SignalName.TextChanged,query);await Frames();}
        async Task Execute(string command)
        {owner.GetNode("/root/LimboConsole").Call("execute_command",command);await commands.PendingTask;await Ready();if(commands.LastResult.StartsWith("ERROR:"))throw new Exception(commands.LastResult);}
        async Task Choose(OptionButton button,int index)
        {
            await ScrollTo(button);if(DisplayServer.GetName()=="headless"){button.ShowPopup();await Frames();}else await ClickControl(button);
            var popup=button.GetPopup();if(!popup.Visible)throw new Exception("Dropdown did not open: "+button.Name);
            popup.SetFocusedItem(index);
            Input.ParseInputEvent(new InputEventKey{WindowId=owner.GetWindow().GetWindowId(),Keycode=Key.Enter,Pressed=true});
            Input.ParseInputEvent(new InputEventKey{WindowId=owner.GetWindow().GetWindowId(),Keycode=Key.Enter,Pressed=false});await Frames();await Ready();
        }
        async Task Screenshot(string filename)
        {
            if(DisplayServer.GetName()=="headless")return;
            preview.Redraw();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=owner.GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(folder,filename));
        }
        try
        {
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,1000);if(DisplayServer.GetName()!="headless")owner.GetWindow().GrabFocus();host.ShowPanel("bones");await Frames(30);
            Check(pinButton.Disabled&&rootSource.Disabled&&rootMotion.Disabled,"unloaded panel disables pins and Root Motion");
            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples")]);await Execute("preview load StudioBot_Walk");
            Check(rootSource.ItemCount==2&&rootSource.GetItemText(1).Contains("Hips")&&rootSource.Selected==0&&rootMotion.Disabled,"root menu lists only parentless bones and requires an explicit source");
            await Query("Foot");await ClickBone("LeftFoot");await ClickBone("RightFoot",ctrl:true);
            Check(Selected("LeftFoot","RightFoot")&&bones.Overlay.SelectedMarkerCount==2,"Ctrl click selects and highlights both bones");
            Check(Item("0:12").IsSelected(0)&&Item("0:15").IsSelected(0)&&Find<Label>("BoneSelection").Text.Contains("已选 2"),"tree and count reflect multi-selection");
            await ClickBone("LeftFoot",ctrl:true);Check(Selected("RightFoot"),"Ctrl click removes a selected bone");
            await ClickBone("LeftFoot",ctrl:true);await Query("Hand");
            Check(Selected("LeftFoot","RightFoot")&&Find<Label>("BoneSelection").Text.Contains("2 根选中骨骼不在筛选"),"search preserves all filtered-out selections");
            await ClickBone("LeftHand",ctrl:true);Check(Selected("LeftFoot","RightFoot","LeftHand"),"Ctrl click adds a search result without losing hidden selections");
            await ClickBone("RightHand");Check(Selected("RightHand"),"plain click replaces both visible and hidden selections");
            await Query("");await ClickBone("LeftLowerLeg");await ClickBone("LeftFoot",shift:true);
            Check(Selected("LeftLowerLeg","LeftFoot"),"Shift click selects a contiguous range");
            Check(bones.Selected==bones.Resolve("LeftFoot"),"Shift click makes the clicked bone primary");
            await Execute("bone select_many \"LeftFoot,RightFoot\"");
            Check(Item("0:12").IsSelected(0)&&Item("0:15").IsSelected(0),"Console multi-selection synchronizes every visible row");
            await Execute("bone remove LeftFoot");Check(!Item("0:12").IsSelected(0)&&Item("0:15").IsSelected(0),"membership changes synchronize even when the primary bone is unchanged");
            await Execute("bone add LeftFoot");await Query("Hand");await ClickControl(Find<Button>("BoneLocate"));
            Check(search.Text==""&&Selected("LeftFoot","RightFoot")&&Item("0:12").IsSelected(0)&&Item("0:15").IsSelected(0),"locate reveals all selections without reducing to a single bone");
            int runs=bones.SamplingRuns;await ClickControl(trail);await Ready();
            Check(bones.Trajectories.Count==2&&bones.SamplingRuns==runs+1,"UI trajectory toggle samples the full selection once");
            await ClickControl(pinButton);await Ready();var worldPins=bones.Pins.ToArray();
            Check(worldPins.Length==2&&Find<Label>("BonePinCount").Text.Contains("2 条")&&Find<CheckBox>("BonePinVisible_"+worldPins[0].Id).ButtonPressed,"pin action creates visible rows for the complete selection");
            ulong firstRow=Find<Control>("BonePinRow_"+worldPins[0].Id).GetInstanceId();await ClickControl(pinButton);await Ready();
            Check(bones.Pins.Count==2&&Find<Control>("BonePinRow_"+worldPins[0].Id).GetInstanceId()==firstRow,"duplicate pinning preserves rows and avoids duplicate paths");
            int hips=Enumerable.Range(0,reference.ItemCount).Single(i=>reference.GetItemText(i).Contains("Hips"));
            await Choose(reference,hips);await Choose(rate,1);await ClickControl(pinButton);await Ready();
            Check(bones.Pins.Count==4&&bones.Pins[0].Reference==null&&bones.Pins[0].Rate==30&&bones.Pins[^1].Reference!=null&&bones.Pins[^1].Rate==60,"UI pins retain independent reference spaces and sampling rates");
            Check(Find<Label>("BonePinDetail_"+bones.Pins[^1].Id).Text.Contains("Hips [0:0]")&&Find<Label>("BonePinDetail_"+worldPins[0].Id).Text.Contains("世界空间"),"pin rows show their captured settings and stable IDs");
            await ClickControl(Find<Button>("BoneClear"));await Ready();
            Check(bones.SelectedBones.Count==0&&bones.Trajectories.Count==4&&pinButton.Disabled,"clear selection keeps fixed paths and disables pinning an empty selection");
            var visible=Find<CheckBox>("BonePinVisible_"+worldPins[0].Id);await ClickControl(visible);await Ready();
            Check(!bones.Pins[0].Visible&&bones.Trajectories.Count==3&&!visible.ButtonPressed,"list checkbox hides exactly one pin");
            Check(visible.HasFocus()&&Find<Control>("BonePinRow_"+worldPins[0].Id).GetInstanceId()==firstRow,"visibility updates preserve row identity and keyboard focus");
            await Execute($"trail visible {worldPins[0].Id} true");Check(visible.ButtonPressed,"Console pin visibility synchronizes the row checkbox");
            await ClickControl(Find<Button>("BonePinRemove_"+worldPins[1].Id));await Ready();
            Check(bones.Pins.Count==3&&bones.Pins.All(p=>p.Id!=worldPins[1].Id)&&!Preview3DViewport.Descendants(panel).Any(n=>n.Name=="BonePinRow_"+worldPins[1].Id),"row remove deletes only the requested pin");
            await Execute($"trail unpin {bones.Pins[^1].Id}");Check(Find<Label>("BonePinCount").Text.Contains("2 条"),"Console removal updates list membership");
            await Choose(reference,0);await Choose(rate,0);await ClickBone(BoneInspection.IdText(worldPins[0].Bone));await ClickControl(trail);await Ready();
            await ClickControl(visible);await Ready();
            Check(!bones.Pins[0].Visible&&Find<Label>("BonePinDetail_"+worldPins[0].Id).Text.Contains("选中轨迹仍显示"),"hidden pin explains when the selected path still owns the same geometry");
            await ClickControl(trail);await Ready();Check(!Find<Label>("BonePinDetail_"+worldPins[0].Id).Text.Contains("仍显示"),"selected and pinned visibility controls remain independent");
            await Choose(rootSource,1);Check(bones.RootMotionSource==bones.Resolve("Hips")&&!rootMotion.Disabled,"root picker sets an explicit source and enables display");
            await ClickControl(rootMotion);await Ready();
            Check(bones.RootMotionEnabled&&bones.RootMotion!=null&&rootMotion.ButtonPressed&&Find<Label>("BoneRootInfo").Text.Contains("世界空间"),"Root Motion switch displays path and sampled status");
            await Execute("preview seek 0.31");Check(Find<Label>("BoneRootInfo").Text.Contains("当前位移"),"Root Motion panel reports displacement while scrubbing");
            var savedPins=bones.Pins.ToArray();var selected=bones.SelectedBones.ToArray();ulong panelId=panel.GetInstanceId();
            host.ShowPanel("inspector");await Frames(20);
            host.ShowPanel("bones");await Frames(20);
            Check(panel.GetInstanceId()==panelId&&bones.Pins.SequenceEqual(savedPins)&&bones.SelectedBones.SequenceEqual(selected)&&rootMotion.ButtonPressed,"tool switching retains pins, selection and Root Motion state");
            host.HidePanel("bones");await Frames();await Execute("bone select_many \"LeftFoot,RightFoot\"");await Execute("rootmotion show false");
            host.ShowPanel("bones");await Frames(20);
            Check(!rootMotion.ButtonPressed&&Item("0:12").IsSelected(0)&&Item("0:15").IsSelected(0),"hidden panel catches up with Console multi-selection and Root Motion");
            await ClickControl(rootMotion);await Ready();await ClickControl(Find<Button>("BoneClearPins"));await Ready();
            Check(bones.Pins.Count==0&&Find<Label>("BonePinEmpty").Visible&&Find<Button>("BoneClearPins").Disabled&&bones.RootMotionEnabled,"clear pins restores empty state without hiding Root Motion");
            await Execute("preview load StudioBot_Rig");await ClickBone("LeftFoot");
            Check(pinButton.Disabled&&rootMotion.Disabled&&rootSource.Selected==0&&bones.Pins.Count==0,"static asset resets pins and source while disabling animation-only actions");
            preview.AttachModel(new Node3D{Name="Empty"});await Frames();
            Check(rootSource.Disabled&&rootMotion.Disabled&&pinButton.Disabled&&rootSource.ItemCount==1,"skeleton-free asset has a complete disabled empty state");

            preview.AttachModel(MultipleRoots());await Frames();
            Check(rootSource.ItemCount==3&&rootSource.GetItemText(1).Contains("0:0")&&rootSource.GetItemText(2).Contains("1:0"),"same-named roots are disambiguated and child bones are excluded");
            await Choose(rootSource,2);await ClickControl(rootMotion);await Ready();
            Check(bones.RootMotionSource==new BoneId(1,0)&&bones.RootMotion!.Source==new BoneId(1,0),"root menu targets the correct skeleton identity");
            await Execute("rootmotion source 0:0");Check(rootSource.Selected==1,"Console root source synchronizes the dropdown");
            await Query("长骨骼");await ClickBone("0:1");await ClickBone("1:1",ctrl:true);await ClickControl(pinButton);await Ready();
            Check(Selected("0:1","1:1")&&bones.Pins.Count==2,"Chinese names and identical child names support multi-selection and pinning");
            savedPins=bones.Pins.ToArray();preview.SelectClip("Still");await Ready();
            Check(bones.Pins.SequenceEqual(savedPins)&&rootMotion.ButtonPressed&&bones.RootMotion!.Clip=="Still"&&bones.Trajectories.All(t=>t.Trajectory.Clip=="Still"),"clip switching recomputes data while preserving the UI configuration");

            await Execute("preview load StudioBot_Walk");await Execute("bone select_many \"LeftFoot,RightFoot,LeftHand,RightHand\"");
            await ClickControl(pinButton);await Ready();await Choose(rootSource,1);await ClickControl(rootMotion);await Ready();bones.ShowSkeleton(true);preview.Seek(.3);await Frames();
            host.ShowPanel("bones");await Frames(30);await Query("Foot");
            Check(host.IsPanelVisible("bones")&&host.IsPanelVisible("preview")&&panel.Size.X<600,"multi-bone controls share the animation workspace with the live preview");
            Check(Find<ScrollContainer>("BonePinScroll").Size.Y<=181&&bones.Pins.Count==4,"long pin lists use bounded internal scrolling");
            await ScrollTo(search);await Screenshot("selection-desktop.png");
            await ScrollTo(Find<Label>("BoneRootInfo"));await Screenshot("trajectories-desktop.png");
            int lastPin=bones.Pins[^1].Id;
            await ClickControl(Find<CheckBox>("BonePinVisible_"+lastPin));await Ready();
            Check(!bones.Pins[^1].Visible,"nested scrolling reaches and toggles the last pin row");
            owner.GetWindow().Size=new(720,400);host.ShowPanel("bones");await Frames(30);
            await ClickControl(rootMotion);await Ready();Check(host.Compact&&!bones.RootMotionEnabled,"small-window scrolling reaches Root Motion controls");
            await ClickControl(Find<CheckBox>("BonePinVisible_"+lastPin));await Ready();Check(bones.Pins[^1].Visible,"small-window scrolling reaches pin list controls");
            for(int scale=1;scale<=2;scale++)
            {
                owner.DisplayScale.SetScale(scale);owner.GetWindow().Size=new(1440,900);host.ShowPanel("bones");await Frames(30);
                Check(rootSource.Size.X<=panel.Size.X+1&&Find<ScrollContainer>("BonePinScroll").Size.X<=panel.Size.X+1&&panel.Size.X<=host.Size.X,"new controls fit panel bounds at "+scale+"x DPI");
            }
            await ClickControl(rootMotion);await Ready();Check(bones.RootMotionEnabled,"Root Motion remains operable at 200 percent scale");await Screenshot("scaled-200.png");
            await ClickControl(Find<Button>("BoneClearPins"));await Ready();Check(bones.Pins.Count==0&&bones.RootMotionEnabled,"clear pins works after resizing without clearing root display");
            preview.Clear();await Frames();Check(rootSource.Selected==0&&!rootMotion.ButtonPressed&&rootMotion.Disabled&&Find<Label>("BonePinEmpty").Visible,"asset clear removes all stale UI state");
            string report=$"PASS: {checks.Count} multi-bone UI checks\n"+string.Join("\n",checks)+"\nDisplay server: "+DisplayServer.GetName();File.WriteAllText(Path.Combine(folder,"results.txt"),report);GD.Print(report+"\n"+folder);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("MULTI BONE UI TEST FAILED: "+ex);File.WriteAllText(Path.Combine(folder,"results.txt"),$"{checks.Count} passed\n"+ex);await Screenshot("failure.png");GD.Print(folder);owner.GetTree().Quit(1);}
    }
    private static Node3D MultipleRoots()
    {
        var model=new Node3D{Name="MultiRootFixture"};
        foreach(string name in new[]{"A","B"})
        {
            var s=new Skeleton3D{Name=name};s.AddBone("Root");s.AddBone("长骨骼名称_abcdefghijklmnopqrstuvwxyz");s.SetBoneParent(1,0);
            s.SetBoneRest(1,new(Basis.Identity,Vector3.Up));s.ResetBonePoses();model.AddChild(s);
        }
        var player=new AnimationPlayer{Name="Player"};model.AddChild(player);var library=new AnimationLibrary();
        foreach(string name in new[]{"Move","Still"})
        {
            var animation=new Animation{Length=1};int track=animation.AddTrack(Animation.TrackType.Position3D);animation.TrackSetPath(track,"B:Root");
            animation.PositionTrackInsertKey(track,0,Vector3.Zero);animation.PositionTrackInsertKey(track,1,name=="Move"?Vector3.Right:Vector3.Zero);library.AddAnimation(name,animation);
        }
        player.AddAnimationLibrary("",library);return model;
    }
}
