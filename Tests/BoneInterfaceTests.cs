using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Pipeline;
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
            await ScrollTo(control);
            await Click(control.GetGlobalRect().GetCenter());
        }
        async Task ScrollTo(Control control)
        {
            for(var parent=control.GetParent();parent!=null;parent=parent.GetParent())
                if(parent is ScrollContainer scroll){scroll.EnsureControlVisible(control);await Frames();}
        }
        async Task ClickBone(string name)
        {
            var item=Item(name);for(var p=item.GetParent();p!=null;p=p.GetParent())p.Collapsed=false;
            await ScrollTo(tree);tree.ScrollToItem(item);await Frames();
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
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);if(DisplayServer.GetName()!="headless")owner.GetWindow().GrabFocus();host.ShowPanel("bones");await Frames(30);
            Check(host.IsPanelVisible("bones")&&host.IsPanelVisible("preview"),"animation workspace exposes the bone panel beside preview");
            Check(trail.Disabled&&Find<Label>("BoneEmpty").Text.Contains("选择一个资产"),"unloaded panel has empty guidance and disabled trajectory");
            var legacy=new WorkspaceLayout();legacy.CompactOrder.Remove("bones");legacy.Hidden.Remove("bones");legacy.Normalize();
            Check(legacy.Hidden.Contains("bones")&&legacy.CompactOrder.Contains("bones"),"older workspace layouts retain content and expose the new hidden panel");
            var weapon = preview.GetNode<Button>("Navigation/WeaponPreview");
            var marker = (Node3D)preview.View.FindChild("ReferenceWeapon", owned: false);
            var weaponPath = (ImmediateMesh)((MeshInstance3D)preview.View.FindChild("WeaponTrail", owned: false)).Mesh;
            preview.LoadFile(RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx"));
            weapon.ButtonPressed = true;
            for (int i = 0; i < 120 && weaponPath.GetSurfaceCount() == 0; i++) await Frames(1);
            var manny = bones.Skeletons.Single().Node;
            Vector3 Joint(string name) => (manny.GlobalTransform * manny.GetBoneGlobalPose(manny.FindBone(name))).Origin;
            var wrist = Joint("hand_r");
            var middle = Joint("middle_01_r");
            var thumbward = (Joint("index_01_r") - Joint("pinky_01_r")).Normalized();
            var handle = marker.GetNode<MeshInstance3D>("BladeModel/Handle");
            Check(marker.Visible && handle.GlobalPosition.DistanceTo(middle) < handle.GlobalPosition.DistanceTo(wrist) &&
                  marker.GlobalBasis.Z.Dot(thumbward) > .75f,
                "Manny grips the handle between the wrist and fingers with the blade exiting the thumb side");
            var startTip = marker.GlobalTransform * (Vector3.Back * Animanager.Core.Motion.MotionPlayback.ReferenceBladeLength);
            Check(weaponPath.GetSurfaceCount() == 1 &&
                  weaponPath.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0].IsEqualApprox(startTip),
                "the trail starts at the tip of the weapon fitted to Manny's palm");
            if (DisplayServer.GetName() != "headless")
            {
                preview.FocusPoint(middle);
                preview.Zoom(.28f);
                await Frames(4);
                await Screenshot("weapon-grip-manny.png");
            }
            weapon.ButtonPressed = false;

            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples")]);await Execute("preview load StudioBot_Walk");
            preview.FrameModel();
            preview.Seek(.31);
            await ClickControl(weapon);
            for (int i = 0; i < 120 && weaponPath.GetSurfaceCount() == 0; i++) await Frames(1);
            Check(weapon.ButtonPressed && marker.Visible && weaponPath.GetSurfaceCount() == 1 &&
                  Math.Abs(preview.Playhead - .31) < .001 && !preview.Playing && !preview.Model!.IsAncestorOf(marker),
                "weapon preview follows the right hand and samples without changing playback or the asset scene");
            var grip = marker.GlobalTransform;
            preview.Seek(.6);
            Check(!marker.GlobalTransform.IsEqualApprox(grip), "reference weapon follows the hand when scrubbing");
            await ClickControl(weapon);
            Check(!marker.Visible && weaponPath.GetSurfaceCount() == 0, "weapon toggle clears the reference weapon and its path");
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
            Check(Math.Abs(((Animanager.UI.Shell.PreviewTabs)host.Panel("preview")).ActiveStage.Timeline.Value-.31)<.001,"paused Console scrubbing also synchronizes the preview timeline");
            await Query("Foot");var selection=bones.Selected;var panelId=panel.GetInstanceId();
            host.ShowPanel("inspector");await Frames();
            Check(panel.GetInstanceId()==panelId&&search.Text=="Foot"&&bones.Selected==selection,"docking preserves search, selection, and panel instance");
            host.ShowPanel("bones");await Frames();
            Check(host.IsPanelVisible("bones")&&host.IsPanelVisible("preview"),"bone tools remain beside preview");
            host.HidePanel("bones");await Frames();await Execute("bone select LeftHand");host.ShowPanel("bones");await Frames();
            Check(Find<Label>("BoneSelection").Text.Contains("LeftHand"),"hidden panel catches up with Console selection when restored");
            var prefs=new WorkspacePreferences{AssetView="List",Scale=1.25};string path=Path.Combine(folder,"workspace.json");prefs.Save(path);
            var restored=WorkspacePreferences.Load(path,out var error);host.RestorePanels();host.ShowPanel("bones");await Frames();
            Check(error.Length==0&&restored.AssetView=="List"&&restored.Scale==1.25&&panel.GetInstanceId()==panelId,"browser settings persist and restoring panels retains the preview");
            preview.LoadFile(RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Rig.glb"));await Frames();
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
            host.ShowPanel("bones");bones.SetXray(true);preview.Seek(.3);await Frames(25);
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
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,960);host.ShowPanel("bones");await Frames(30);
            preview.Seek(.3);await Frames();Check(bones.Selected==bones.Resolve("LeftFoot"),"resize and DPI changes retain bone selection");
            var testWeapon = new Node3D();
            var testMesh = new MeshInstance3D { Name = "PracticeBlade", Mesh = new BoxMesh { Size = new(.04f, .02f, .7f) } };
            testWeapon.AddChild(testMesh); testMesh.Owner = testWeapon;
            string weaponFile = Path.Combine(folder, "PracticeBlade.glb");
            try { ExportService.WriteScene(weaponFile, testWeapon, null, false, 0); }
            finally { testWeapon.Free(); }

            var studio = bones.Skeletons.Single().Node;
            studio.AddBone("extra_weapon_socket");
            int extra = studio.FindBone("extra_weapon_socket");
            studio.SetBoneParent(extra, studio.FindBone("RightHand"));
            var player = preview.Player!;
            var clip = player.GetAnimation(preview.CurrentClip);
            int track = clip.AddTrack(Animation.TrackType.Position3D);
            clip.TrackSetPath(track, new NodePath(player.GetNode(player.RootNode).GetPathTo(studio) + ":extra_weapon_socket"));
            clip.PositionTrackInsertKey(track, 0, new(.1f, 0, 0));
            clip.PositionTrackInsertKey(track, clip.Length, new(.3f, 0, 0));
            preview.Seek(0);

            var dialog = owner.GetNode<AcceptDialog>("WeaponProfiles");
            var openProfiles = Preview3DViewport.Descendants(owner).OfType<Button>().Single(node => node.Name == "OpenWeaponProfiles");
            openProfiles.EmitSignal(BaseButton.SignalName.Pressed);
            T ProfileField<T>(string field) where T : Node => (T)dialog.FindChild(field, owned: false);
            var socket = ProfileField<OptionButton>("WeaponSocket");
            int leftPalm = Enumerable.Range(0, socket.ItemCount).Single(i => socket.GetItemText(i) == "左手掌");
            socket.Select(leftPalm);
            ProfileField<SpinBox>("WeaponTrailSeconds").Value = .3;
            owner.GetNode<FileDialog>("InstallWeaponFile").EmitSignal(FileDialog.SignalName.FileSelected, weaponFile);
            int extraOption = Enumerable.Range(0, socket.ItemCount).Single(i => socket.GetItemText(i).StartsWith("extra_weapon_socket ·"));
            socket.Select(extraOption);
            ProfileField<SpinBox>("WeaponMountPositionY").Value = .05;
            ProfileField<SpinBox>("WeaponBoxCenterZ").Value = .25;
            ProfileField<SpinBox>("WeaponBoxSizeZ").Value = .5;
            ProfileField<SpinBox>("WeaponTrailSeconds").Value = .6;
            ProfileField<CheckButton>("ShowWeaponTrailBox").ButtonPressed = true;
            ProfileField<Button>("SaveWeaponProfile").EmitSignal(BaseButton.SignalName.Pressed);
            var markers = preview.View.FindChildren("ReferenceWeapon", "Node3D", owned: false).OfType<Node3D>().ToArray();
            var leftMarker = markers[0];
            marker = markers[1];
            var leftRibbon = leftMarker.GetParent().GetNode<MeshInstance3D>("WeaponTrail");
            var rightRibbon = marker.GetParent().GetNode<MeshInstance3D>("WeaponTrail");
            weaponPath = (ImmediateMesh)rightRibbon.Mesh;
            for (int i = 0; i < 120 && weaponPath.GetSurfaceCount() == 0; i++) await Frames(1);
            Check(!dialog.Visible && weapon.ButtonPressed && marker.IsVisibleInTree() && marker.FindChild("PracticeBlade", owned: false) != null &&
                  leftMarker.IsVisibleInTree() && !leftMarker.GlobalPosition.IsEqualApprox(marker.GlobalPosition) && leftRibbon.Mesh.GetSurfaceCount() == 1,
                "one profile displays separate left-hand and extra-bone weapons and ribbons simultaneously");
            var expectedMount = (studio.GlobalTransform * studio.GetBoneGlobalPose(extra)).Orthonormalized().TranslatedLocal(Vector3.Up * .05f);
            Check(marker.GlobalTransform.IsEqualApprox(expectedMount), "the configured weapon mount follows the original extra animation bone");
            var box = marker.GetParent().GetNode<MeshInstance3D>("WeaponTrailBox");
            var trailVertices = weaponPath.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Check(box.Visible && box.GlobalPosition.IsEqualApprox(expectedMount * (Vector3.Back * .25f)) &&
                  trailVertices[0].IsEqualApprox(expectedMount * (Vector3.Back * .5f)) && trailVertices[1].IsEqualApprox(expectedMount.Origin),
                "configured box drives both edges of the swept ribbon");
            var ribbonMaterial = (ShaderMaterial)rightRibbon.MaterialOverride;
            var leftMaterial = (ShaderMaterial)leftRibbon.MaterialOverride;
            Check(Math.Abs(ribbonMaterial.GetShaderParameter("trail_seconds").AsSingle() - .6f) < .001f &&
                  Math.Abs(leftMaterial.GetShaderParameter("trail_seconds").AsSingle() - .3f) < .001f,
                "each weapon has its own trail duration");
            preview.Seek(.5);
            Check(!marker.GlobalTransform.IsEqualApprox(expectedMount), "weapon and ribbon playhead follow scrubbing on an extra bone");
            openProfiles.EmitSignal(BaseButton.SignalName.Pressed);
            var weaponItems = ProfileField<ItemList>("WeaponItems");
            Check(weaponItems.ItemCount == 2 && ProfileField<OptionButton>("WeaponSocket").Selected == leftPalm,
                "the first weapon keeps its left-hand mount after reopening the profile");
            weaponItems.Select(1);
            weaponItems.EmitSignal(ItemList.SignalName.ItemSelected, 1);
            Check(ProfileField<OptionButton>("WeaponSocket").Selected == extraOption &&
                  Math.Abs(ProfileField<SpinBox>("WeaponBoxSizeZ").Value - .5) < .001 &&
                  Math.Abs(ProfileField<SpinBox>("WeaponTrailSeconds").Value - .6) < .001,
                "reopening the profile restores the saved mount, trail box and duration");
            dialog.Hide();
            var inspector = host.Panel("inspector");
            var overviewChoice = (OptionButton)inspector.FindChild("OverviewWeaponProfile", owned: false);
            var overviewEnabled = (CheckButton)inspector.FindChild("OverviewWeaponEnabled", owned: false);
            Check(overviewChoice.GetItemText(overviewChoice.Selected).Contains("2 件") && overviewEnabled.ButtonPressed,
                "overview shows the active profile count and enabled state");
            overviewEnabled.ButtonPressed = false;
            Check(!weapon.ButtonPressed && markers.All(item => !item.IsVisibleInTree()), "the overview switch hides all weapons and synchronizes the viewport toggle");
            int dualIndex = Enumerable.Range(0, overviewChoice.ItemCount).Single(i => overviewChoice.GetItemText(i).StartsWith("通用双刀"));
            overviewChoice.Select(dualIndex);
            overviewChoice.EmitSignal(OptionButton.SignalName.ItemSelected, dualIndex);
            overviewEnabled.ButtonPressed = true;
            Check(weapon.ButtonPressed && preview.AvailableWeaponCount == 2,
                "selecting the dual-sword preset in the overview enables both palm mounts together");

            string legacyRoot = Path.Combine(folder, "legacy-weapon-profile");
            string legacyId = Guid.NewGuid().ToString("N");
            Animanager.Core.Motion.MotionJson.Atomic(Path.Combine(legacyRoot, "profiles.json"), new
            {
                selected = legacyId,
                profiles = new[] { new { id = legacyId, name = "Legacy sword", socket = WeaponSocket.LeftPalm, trailSeconds = .42 } }
            });
            var legacyWeapons = new WeaponProfileLibrary(legacyRoot);
            var migrated = legacyWeapons.Active().Weapons.Single();
            Check(migrated.Socket == WeaponSocket.LeftPalm && migrated.TrailSeconds == .42 &&
                  legacyWeapons.ModelPath(migrated) == Path.Combine(legacyRoot, legacyId + ".glb"),
                "legacy single-weapon profiles keep their model, mount and duration");
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
