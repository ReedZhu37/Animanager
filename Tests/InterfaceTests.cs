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
            File.WriteAllText(saved,"{broken");var fallback=WorkspacePreferences.Load(saved,out error);
            Check(error.Length>0&&fallback.Layout.GroupFor("assets")!=null,"damaged configuration recovers to usable defaults");
            var clamped=DisplayScaleController.ClampRect(new(new(4000,2000),new(2000,1200)),new(new(0,0),new(1280,720)));
            Check(clamped.Position==Vector2I.Zero&&clamped.Size==new Vector2I(1280,720),"window restored from disconnected monitor fits the available display");

            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_Walk.glb")]);
            var console=owner.GetNode("/root/LimboConsole");console.Call("execute_command","preview load StudioBot_Walk");await commands.PendingTask;await Frames();
            var model=preview.Model!;var instance=model.GetInstanceId();preview.Seek(.3);
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.UsePreset("browse");await Frames(30);
            Check(!host.Compact&&host.Groups.Count>=3,"desktop size creates a multi-panel workspace");
            var target=host.Layout.GroupFor("assets")!;host.MovePanel("preview",target.Id,DockEdge.Center);await Frames();
            Check(host.Layout.GroupFor("assets")==host.Layout.GroupFor("preview"),"runtime docking updates the canonical group");
            Check(preview.Model!.GetInstanceId()==instance&&Math.Abs(preview.Playhead-.3)<.01,"docking preserves the loaded model and animation position");
            host.Maximize("preview");await Frames();Check(host.Groups.Count==1&&host.IsPanelVisible("preview"),"panel maximize shows a single content group");
            host.Maximize("preview");await Frames();Check(host.Groups.Count>1,"restore returns to the previous split layout");
            host.HidePanel("preview");await Frames();Check(!host.IsPanelVisible("preview")&&preview.View.RenderTargetUpdateMode==SubViewport.UpdateMode.Disabled,"hidden preview stops requesting redraws");
            host.ShowPanel("preview");await Frames();Check(preview.Model!.GetInstanceId()==instance,"restoring the preview reuses its existing scene");
            var before=JsonSerializer.Serialize(host.Layout.Root);
            window.Size=new(720,400);await Frames(30);
            Check(host.Compact&&host.Groups.Count==1,"720 x 400 uses a single tabbed workspace");
            Check(JsonSerializer.Serialize(host.Layout.Root)==before,"small-window adaptation leaves the saved desktop layout intact");
            foreach(var id in WorkspaceLayout.PanelIds)
            {
                host.ShowPanel(id);await Frames();
                Check(host.IsPanelVisible(id)&&host.Panel(id).Size.X>100,"small window can reach panel: "+id);
            }
            window.Size=new(1440,900);await Frames(30);Check(!host.Compact,"expanding the window restores desktop panels");
            foreach(var scale in new[]{1.25,1.5,1.75,2.0})
            {
                owner.DisplayScale.SetScale(scale);await Frames(30);
                Check(Math.Abs(window.ContentScaleFactor-scale)<.001&&owner.Size.X<=window.Size.X/scale+2,"UI scale is applied once at "+scale);
                Check(host.Size.X>300&&host.Groups.All(g=>g.Size.X>100&&g.Size.Y>60),"scaled layout keeps reachable panel bounds at "+scale);
            }
            var search=Preview3DViewport.Descendants(host.Panel("assets")).OfType<LineEdit>().First();
            host.ShowPanel("assets");await Frames();search.GrabFocus();commands.Open();await Frames(45);
            Check(console.Call("is_open").AsBool(),"Limbo Console remains available at 200% scale");
            console.Call("close_console");await Frames(45);Check(search.HasFocus(),"closing Console restores the original input focus");
            search.Text="中文名称 空格";search.CaretColumn=search.Text.Length;
            owner._UnhandledKeyInput(new InputEventKey{Keycode=Key.Space,Pressed=true});
            Check(!preview.Playing&&search.Text=="中文名称 空格","typing context does not trigger viewport playback");
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.UsePreset("browse");await Frames(30);
            var report=$"PASS: {checks.Count} interface checks\n"+string.Join("\n",checks)+"\nDisplay server: "+DisplayServer.GetName()+"\nDPI source: "+owner.DisplayScale.Source;
            File.WriteAllText(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("INTERFACE TEST FAILED: "+ex);File.WriteAllText(Path.Combine(root,"results.txt"),$"{checks.Count} passed\n"+ex);owner.GetTree().Quit(1);}
    }
}
