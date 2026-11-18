using Godot;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Workspace;
using System.Text.Json;

namespace Animanager.Tests;

public static class InterfaceTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var root=Path.Combine(api.Storage.Root,"interface-results");Directory.CreateDirectory(root);var checks=new List<string>();
        void Check(bool ok,string title){if(!ok)throw new Exception(title);checks.Add(title);GD.Print("PASS "+title);}
        async Task Frames(int count=20){for(int i=0;i<count;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        var host=owner.Workspace;var window=owner.GetWindow();
        try
        {
            var layout=new WorkspaceLayout();var assetGroup=layout.GroupFor("assets")!;
            Check(layout.Move("preview",assetGroup.Id,DockEdge.Center),"panels can move into a tab group");
            Check(layout.GroupFor("preview")==layout.GroupFor("assets")&&layout.Root.AllPanels().Distinct().Count()==5,"grouping preserves exactly one instance of every visible panel");
            Check(layout.Move("preview",assetGroup.Id,DockEdge.Bottom)&&layout.GroupFor("preview")!=layout.GroupFor("assets"),"tab can undock into a vertical split");
            layout.Hide("preview");Check(layout.GroupFor("preview")==null&&layout.Hidden.Contains("preview"),"hidden panel is removed from layout without deleting content");
            layout.Show("preview");Check(layout.GroupFor("preview")!=null&&!layout.Hidden.Contains("preview"),"hidden panel can be restored");
            foreach(var panel in layout.Root.AllPanels().ToArray())layout.Hide(panel);
            Check(layout.Root.AllPanels().Count()==1&&!layout.Hide(layout.Root.AllPanels().First()),"the last remaining panel cannot be hidden");
            layout.Root=DockNode.Split("h",float.NaN,DockNode.Group("assets","unknown"),DockNode.Group("assets","preview"));layout.Normalize();
            Check(float.IsFinite(layout.Root.Ratio)&&layout.Root.AllPanels().SequenceEqual(new[]{"assets","preview"}),"invalid ratios, duplicate and unknown panels normalize safely");
            var preferences=new WorkspacePreferences{Layout=layout,Scale=1.5,SavedLayouts=new(){{"中文布局",layout.Clone()}}};var saved=Path.Combine(root,"workspace.json");preferences.Save(saved);
            var reloaded=WorkspacePreferences.Load(saved,out var error);
            Check(error.Length==0&&reloaded.Scale==1.5&&reloaded.SavedLayouts.ContainsKey("中文布局"),"named layout and scale survive a filesystem round trip");
            preferences.AssetView="List";preferences.SortField=Animanager.Core.Browse.AssetSortField.Fps;preferences.SortDescending=false;preferences.Save(saved);
            reloaded=WorkspacePreferences.Load(saved,out error);
            Check(reloaded.AssetView=="List"&&reloaded.SortField==Animanager.Core.Browse.AssetSortField.Fps&&!reloaded.SortDescending,
                "asset view and sort direction survive restart alongside legacy layout data");
            var appPreferences=Path.Combine(root,"app-settings","workspace.json");
            var migrated=WorkspacePreferences.LoadForApp(appPreferences,saved,out error);
            Check(error.Length==0&&migrated.SavedLayouts.ContainsKey("中文布局")&&File.Exists(appPreferences),"existing library layout is imported into app preferences");
            new WorkspacePreferences{Scale=2}.Save(saved);
            Check(WorkspacePreferences.LoadForApp(appPreferences,saved,out error).Scale==1.5&&error.Length==0,"app layout takes precedence over a different library's legacy preferences");
            new WorkspacePreferences{AssetView="Unknown",SortField=(Animanager.Core.Browse.AssetSortField)99}.Save(saved);
            reloaded=WorkspacePreferences.Load(saved,out error);
            Check(reloaded.AssetView=="Grid"&&reloaded.SortField==Animanager.Core.Browse.AssetSortField.Imported,"unknown asset preferences fall back to supported controls");
            File.WriteAllText(saved,"{broken");var fallback=WorkspacePreferences.Load(saved,out error);
            Check(error.Length>0&&fallback.Layout.GroupFor("assets")!=null,"damaged configuration recovers to usable defaults");
            var clamped=DisplayScaleController.ClampRect(new(new(4000,2000),new(2000,1200)),new(new(0,0),new(1280,720)));
            Check(clamped.Position==Vector2I.Zero&&clamped.Size==new Vector2I(1280,720),"window restored from disconnected monitor fits the available display");

            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_Walk.glb")]);
            var console=owner.GetNode("/root/LimboConsole");console.Call("execute_command","preview load StudioBot_Walk");await commands.PendingTask;await Frames();
            var model=preview.Model!;var instance=model.GetInstanceId();preview.Seek(.3);
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);
            Check(!host.Compact&&host.IsPanelVisible("assets")&&host.IsPanelVisible("preview"),"desktop shell shows browsing and preview together");
            Check(host.Panel("assets").GetGlobalRect().Position.Y > host.Panel("preview").GetGlobalRect().End.Y,
                "default layout places the animation card browser beneath the central viewport");
            var buttonStyle=owner.GetThemeStylebox("normal","Button");
            var optionStyle=owner.GetThemeStylebox("normal","OptionButton");
            Check(buttonStyle.ContentMarginTop>=6&&buttonStyle.ContentMarginLeft>=12&&optionStyle.ContentMarginBottom>=6,
                "text buttons and dropdowns publish consistent content padding");
            var libraryDialog=owner.GetNode<AcceptDialog>("LibraryDialog");
            foreach(var scale in new[]{1d,2d})
            {
                owner.DisplayScale.SetScale(scale);window.Size=new(1024,700);await Frames();
                owner.FindChild("LibraryButton",true,false).EmitSignal(BaseButton.SignalName.Pressed);await Frames();
                Check(libraryDialog.Visible&&libraryDialog.Size.X<=window.Size.X&&libraryDialog.Size.Y<=window.Size.Y
                    &&libraryDialog.GetOkButton().GetGlobalRect().End.Y<=libraryDialog.Size.Y/scale,
                    $"library dialog and close button stay within the window at {scale:0}x scale");
                libraryDialog.Hide();await Frames();
            }
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);
            var dock=host.Docking;
            var inputViewport=owner.GetViewport();
            if(DisplayServer.GetName()=="headless")inputViewport.NotifyMouseEntered();
            async Task Drag(Vector2 from,Vector2 to,bool cancel=false)
            {
                if(DisplayServer.GetName()!="headless")
                {window.GrabFocus();await Frames(4);Input.WarpMouse(from*window.ContentScaleFactor);await Frames(4);Input.FlushBufferedEvents();}
                using var move=new InputEventMouseMotion{Position=from,GlobalPosition=from};Input.ParseInputEvent(move);Input.FlushBufferedEvents();
                using var press=new InputEventMouseButton{Position=from,GlobalPosition=from,ButtonIndex=MouseButton.Left,Pressed=true};Input.ParseInputEvent(press);Input.FlushBufferedEvents();
                await Frames(2);
                using var begin=new InputEventMouseMotion{Position=from+new Vector2(18,0),GlobalPosition=from+new Vector2(18,0),Relative=new(18,0),ButtonMask=MouseButtonMask.Left};Input.ParseInputEvent(begin);Input.FlushBufferedEvents();
                await Frames(2);
                if(DisplayServer.GetName()!="headless")
                {Input.WarpMouse(to*window.ContentScaleFactor);await Frames(2);Input.FlushBufferedEvents();}
                using var travel=new InputEventMouseMotion{Position=to,GlobalPosition=to,Relative=to-from-new Vector2(18,0),ButtonMask=MouseButtonMask.Left};Input.ParseInputEvent(travel);Input.FlushBufferedEvents();
                await Frames(2);
                if(cancel)
                {
                    using var escape=new InputEventKey{Keycode=Key.Escape,Pressed=true};Input.ParseInputEvent(escape);Input.FlushBufferedEvents();
                    using var escapeUp=new InputEventKey{Keycode=Key.Escape,Pressed=false};Input.ParseInputEvent(escapeUp);Input.FlushBufferedEvents();
                }
                using var release=new InputEventMouseButton{Position=to,GlobalPosition=to,ButtonIndex=MouseButton.Left,Pressed=false};Input.ParseInputEvent(release);Input.FlushBufferedEvents();
                await Frames(20);
            }
            var sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            var targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            await Drag(sourceTab.GetGlobalRect().GetCenter(),targetGroup.GetGlobalRect().GetCenter());
            Check(dock.Layout.GroupFor("assets")==dock.Layout.GroupFor("preview"),"dragging the asset tab into the viewport center joins its tab group");
            sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            var targetRect=targetGroup.GetGlobalRect();
            await Drag(sourceTab.GetGlobalRect().GetCenter(),new(targetRect.GetCenter().X,targetRect.End.Y-18));
            Check(dock.Layout.GroupFor("assets")!=dock.Layout.GroupFor("preview")&&host.IsPanelVisible("preview")&&host.IsPanelVisible("assets"),
                "dragging to a panel edge creates a separate dock region");
            Check(preview.Model!.GetInstanceId()==instance&&Math.Abs(preview.Playhead-.3)<.01,"dragging and regrouping preserve the loaded model and playhead");
            host.RestorePanels();await Frames();
            var splitter=Preview3DViewport.Descendants(dock).OfType<RatioSplit>().Single(s=>s.Data==dock.Layout.Root);
            var topPane=splitter.GetChild<Control>(0);float oldHeight=topPane.Size.Y;
            var handle=splitter.GlobalPosition+new Vector2(splitter.Size.X*.5f,topPane.Size.Y+4);
            await Drag(handle,handle-new Vector2(0,40));
            Check(topPane.Size.Y<oldHeight-20&&Math.Abs(dock.Layout.Root.Ratio-.56f)>.02,
                "dragging the horizontal splitter resizes the browser and stores the new ratio");
            var verticalSplitter=Preview3DViewport.Descendants(dock).OfType<RatioSplit>().Single(s=>s.Data==dock.Layout.Root.First);
            var leftPane=verticalSplitter.GetChild<Control>(0);float oldWidth=leftPane.Size.X;
            var verticalHandle=verticalSplitter.GlobalPosition+new Vector2(leftPane.Size.X+4,verticalSplitter.Size.Y*.5f);
            await Drag(verticalHandle,verticalHandle+new Vector2(50,0));
            Check(leftPane.Size.X>oldWidth+30,"dragging a vertical splitter changes the side panel width");
            var dockFile=Path.Combine(root,"component-docking.json");new WorkspacePreferences{DockLayout=dock.Layout.Clone()}.Save(dockFile);
            var savedDock=WorkspacePreferences.Load(dockFile,out error).DockLayout!;
            Check(error.Length==0&&JsonSerializer.Serialize(savedDock.Root)==JsonSerializer.Serialize(dock.Layout.Root),"dock positions and split ratios survive a preference round trip");
            var desktopTree=JsonSerializer.Serialize(dock.Layout.Root);
            sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            await Drag(sourceTab.GetGlobalRect().GetCenter(),targetGroup.GetGlobalRect().GetCenter(),cancel:true);
            Check(!dock.DragActive&&JsonSerializer.Serialize(dock.Layout.Root)==desktopTree,"Escape cancels docking without changing the layout");
            window.Size=new(720,400);await Frames(30);
            Check(dock.Compact&&JsonSerializer.Serialize(dock.Layout.Root)==desktopTree,"compact navigation preserves the desktop docking tree");
            window.Size=new(1440,900);host.RestorePanels();await Frames(30);
            host.ShowPanel("bones");await Frames();
            Check(host.ActiveTool=="bones"&&host.IsPanelVisible("bones"),"bone tools open in the right tab");
            host.ShowPanel("export");host.ShowPanel("retarget");await Frames();
            Check(host.ActiveDrawer==""&&host.IsPanelVisible("retarget"),"opening retarget tools closes the export drawer that would cover them");
            Check(preview.Model!.GetInstanceId()==instance&&Math.Abs(preview.Playhead-.3)<.01,"tool switching preserves model and animation position");
            host.FocusPanel("preview");await Frames();Check(host.IsPanelVisible("preview")&&!host.IsPanelVisible("assets"),"preview focus uses the workspace");
            host.RestorePanels();await Frames();Check(host.IsPanelVisible("assets"),"restore returns to browsing");
            host.HidePanel("preview");await Frames();Check(!host.IsPanelVisible("preview")&&preview.View.RenderTargetUpdateMode==SubViewport.UpdateMode.Disabled,"hidden preview stops requesting redraws");
            host.ShowPanel("preview");await Frames();Check(preview.Model!.GetInstanceId()==instance,"restoring preview reuses its scene");
            window.Size=new(720,400);await Frames(30);Check(host.Compact,"720 x 400 uses compact navigation");
            foreach(var id in new[]{"assets","preview","inspector","bones","retarget","export","jobs","filters"})
            {
                host.CloseDrawer();host.ShowPanel(id);await Frames();
                Check(host.IsPanelVisible(id)&&host.Panel(id).Size.X>100,"small window reaches panel: "+id);
            }
            host.RestorePanels();window.Size=new(1440,900);await Frames(30);Check(!host.Compact,"expanding restores desktop panels");
            foreach(var scale in new[]{1.25,1.5,1.75,2.0})
            {
                owner.DisplayScale.SetScale(scale);await Frames(30);
                Check(Math.Abs(window.ContentScaleFactor-scale)<.001&&owner.Size.X<=window.Size.X/scale+2,"UI scale is applied once at "+scale);
                Check(host.Size.X>300&&host.Panel("assets").Size.X>100,"scaled layout keeps reachable panel bounds at "+scale);
            }
            var search=(LineEdit)owner.FindChild("AssetSearch",true,false)!;
            host.ShowPanel("assets");await Frames();search.GrabFocus();commands.Open();await Frames(45);
            Check(console.Call("is_open").AsBool(),"Limbo Console remains available at 200% scale");
            console.Call("close_console");await Frames(45);Check(search.HasFocus(),"closing Console restores the original input focus");
            search.Text="中文名称 空格";search.CaretColumn=search.Text.Length;
            owner._UnhandledKeyInput(new InputEventKey{Keycode=Key.Space,Pressed=true});
            Check(!preview.Playing&&search.Text=="中文名称 空格","typing context does not trigger viewport playback");
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);

            // Real containers and input signals exercise view layout, independent scroll memory and range selection.
            var browserProbe=new Animanager.UI.Shell.AssetBrowser{Position=new(100,100),Size=new(500,300)};
            owner.AddChild(browserProbe);
            browserProbe.SetRows(Enumerable.Range(0,100).Select(i=>new Animanager.UI.Shell.AssetCardModel{Id="probe-"+i,Name="Asset "+i}));
            await Frames();browserProbe.Scroll.ScrollVertical=700;await Frames();
            browserProbe.Selection.Select("probe-3");
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();browserProbe.Scroll.ScrollVertical=1100;await Frames();
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.Grid);await Frames();
            Check(browserProbe.Scroll.ScrollVertical==700&&browserProbe.Selection.Anchor=="probe-3","returning to grid restores its scroll and selection anchor");
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();
            Check(browserProbe.Scroll.ScrollVertical==1100,"returning to list restores its independent scroll position");
            browserProbe.List.EmitSignal(Control.SignalName.GuiInput,new InputEventKey{Keycode=Key.Down,Pressed=true});
            browserProbe.List.EmitSignal(Control.SignalName.GuiInput,new InputEventKey{Keycode=Key.Down,Pressed=true,ShiftPressed=true});
            Check(browserProbe.Selection.Order().SequenceEqual(new[]{"probe-4","probe-5"}),"keyboard arrows and Shift extend the shared selection");
            browserProbe.QueueFree();await Frames();

            search.Text="";
            await api.ImportAsync([RuntimePaths.Bundled("BuiltinAssets/Samples")]);
            commands.Load("StudioBot_Walk");await commands.PendingTask;
            host.ShowPanel("assets");await Frames(30);
            var rows=(Animanager.UI.Shell.AssetBrowser)host.Panel("assets").FindChild("AssetBrowser",true,false)!;
            rows.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();
            var context=(PopupMenu)owner.FindChild("AssetContextMenu",true,false)!;
            var approval=(ConfirmationDialog)owner.FindChild("OperationApproval",true,false)!;
            var viewport=owner.GetViewport();
            if(DisplayServer.GetName()=="headless")viewport.NotifyMouseEntered();
            async Task ClickRow(int index,MouseButton button,bool ctrl=false)
            {
                var point=rows.ActiveView.GetGlobalTransformWithCanvas()*rows.RowRect(index).GetCenter();
                if(DisplayServer.GetName()!="headless")
                {
                    Input.WarpMouse(point*window.ContentScaleFactor);await Frames(4);Input.FlushBufferedEvents();
                }
                using var motion=new InputEventMouseMotion{Position=point,GlobalPosition=point,CtrlPressed=ctrl};
                using var click=new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=button,Pressed=true,CtrlPressed=ctrl};
                using var release=(InputEventMouseButton)click.Duplicate();release.Pressed=false;
                viewport.PushInput(motion,true);viewport.PushInput(click,true);viewport.PushInput(release,true);
                await Frames(8);
            }

            await ClickRow(0,MouseButton.Left);
            await ClickRow(1,MouseButton.Left,ctrl:true);
            var selected=api.Search().Items.Take(2).Select(a=>a.Id).ToHashSet();
            await ClickRow(1,MouseButton.Right);
            Check(context.Visible&&context.GetItemText(0).Contains("移入回收站")&&selected.SetEquals(owner.SelectedAssetIds),
                "right-click on a selected row shows Delete and keeps the entire selection");
            context.Hide();context.EmitSignal(PopupMenu.SignalName.IdPressed,context.GetItemId(0));await Frames(8);
            Check(approval.Visible&&api.Search(category:"Trash").Total==0,"Delete asks for confirmation before changing assets");
            approval.Hide();approval.EmitSignal(AcceptDialog.SignalName.Confirmed);await Frames(8);
            Check(api.Search(category:"Trash").Items.Select(a=>a.Id).ToHashSet().SetEquals(selected),"right-click Delete moves exactly the selected assets to Trash");

            var trash=(Button)host.Panel("navigation").FindChild("CategoryTrash",true,false)!;
            trash.EmitSignal(BaseButton.SignalName.Pressed);await Frames(8);
            await ClickRow(0,MouseButton.Right);
            Check(context.Visible&&context.GetItemText(0).Contains("恢复")&&owner.SelectedAssetIds.Length==1,"right-click on an unselected Trash row selects it and offers Restore");
            var restoredId=owner.SelectedAssetIds.Single();
            context.Hide();context.EmitSignal(PopupMenu.SignalName.IdPressed,context.GetItemId(0));
            approval.Hide();approval.EmitSignal(AcceptDialog.SignalName.Confirmed);await Frames(8);
            Check(api.Database.Find(restoredId)!=null&&api.Search(category:"Trash").Total==1,"right-click Restore applies to the selected Trash row");

            var separateRoot=Path.Combine(Path.GetDirectoryName(api.Storage.Root)!,"interface-switch-"+Guid.NewGuid().ToString("N"));
            var separate=owner.Libraries.Create(separateRoot,"Layout switch check");
            new WorkspacePreferences{Scale=2}.Save(Path.Combine(separateRoot,"workspace.json"));
            host.ShowPanel("bones");host.FocusPanel("bones");owner.DisplayScale.SetScale(1.25);await Frames();
            var activeTool=host.ActiveTool;
            var activeSize=window.Size;
            commands.LibraryOpen(separate.Id);await Frames();
            Check(owner.Libraries.Active?.Identity.Id==separate.Id&&host.ActiveTool==activeTool&&host.FocusedPanel=="bones"&&
                owner.DisplayScale.RequestedScale==1.25&&window.Size==activeSize,"switching libraries preserves active tool, focused panel, window size and scale");

            var report=$"PASS: {checks.Count} interface checks\n"+string.Join("\n",checks)+"\nDisplay server: "+DisplayServer.GetName()+"\nDPI source: "+owner.DisplayScale.Source;
            File.WriteAllText(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("INTERFACE TEST FAILED: "+ex);File.WriteAllText(Path.Combine(root,"results.txt"),$"{checks.Count} passed\n"+ex);owner.GetTree().Quit(1);}
    }
}
