using Animanager.Core;
using Animanager.Core.Console;
using Animanager.UI;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.Tests;

public static class ShellVisualTests
{
    public static async Task Run(MainShell owner, WorkspaceApi api, ConsoleCommands commands)
    {
        var output = RuntimePaths.Bundled(".runtime/shell-validation"); Directory.CreateDirectory(output);
        async Task Frames(int count = 20) { for (int i = 0; i < count; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Capture(string name)
        {
            await Frames(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = owner.GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(output, name + ".png")) != Error.Ok) throw new IOException("Screenshot failed.");
            GD.Print("CAPTURE " + name);
        }
        try
        {
            if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Visual checks require a renderer.");
            await api.ImportAsync([RuntimePaths.Bundled("BuiltinAssets/Samples")]);
            var walk = api.Find("StudioBot_Walk");
            api.RenameAsset(walk.Id, "中文名称测试_角色行走动画_需要截断但可以完整查看的长名称");
            commands.Load(walk.Id); await commands.PendingTask; await owner.PendingBrowse;
            var browser = (AssetBrowser)owner.FindChild("AssetBrowser", true, false)!;
            var stage = ((PreviewTabs)owner.Workspace.Panel("preview")).ActiveStage;
            stage.Viewport.Seek(.3);
            var instance = stage.Viewport.Model!.GetInstanceId();
            owner.GetWindow().Mode = Window.ModeEnum.Windowed;
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                owner.DisplayScale.SetScale(scale); owner.GetWindow().Size = new(1600, 1000); owner.Workspace.RestorePanels();
                owner.Workspace.ShowPanel("inspector");
                await Capture($"desktop-{scale * 100:0}");
                var inspector = (Inspector)owner.Workspace.Panel("inspector");
                if(inspector.IsVisibleInTree() && (inspector.Size.Y < 100 || !inspector.TitleLabel.IsVisibleInTree() || inspector.TitleLabel.Size.X < 100))
                    throw new Exception("Inspector content is collapsed at " + scale);
                owner.GetWindow().Size = new(1024, 700); await Frames();
                if(owner.GetWindow().Size != new Vector2I(1024,700)) throw new Exception("Window minimum prevents 1024x700 at " + scale);
                foreach (var panel in new[] { "assets", "preview", "bones" })
                {
                    owner.Workspace.ShowPanel(panel); await Frames();
                    var control = owner.Workspace.Panel(panel);
                    if (!control.IsVisibleInTree() || control.Size.X > owner.Workspace.Size.X + 1)
                        throw new Exception("Inaccessible panel " + panel + " at " + scale);
                    await Capture($"compact-{scale * 100:0}-{panel}");
                }
            }
            owner.DisplayScale.SetScale(1.25); owner.GetWindow().Size = new(1600, 1000); owner.Workspace.RestorePanels();
            owner.Workspace.ShowPanel("inspector");
            browser.SetMode(AssetBrowser.ViewMode.List); browser.Selection.AddRange(browser.Rows.Select(r => r.Id));
            await Capture("list-multiselect");
            foreach (var panel in new[] { "retarget", "export", "jobs", "filters" })
            { owner.Workspace.ShowPanel(panel); await Capture(panel); owner.Workspace.CloseDrawer(); }
            if (stage.Viewport.Model!.GetInstanceId() != instance || Math.Abs(stage.Viewport.Playhead - .3) > .01)
                throw new Exception("Layout changes replaced preview state.");
            File.WriteAllText(Path.Combine(output, "results.txt"), "PASS: rendered 100/125/150/200% layouts, 1024x700 access, drawers, long Chinese names, shared selection and preview identity.");
            GD.Print("PASS shell visual checks"); owner.GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); File.WriteAllText(Path.Combine(output, "results.txt"), ex.ToString()); owner.GetTree().Quit(1); }
    }
}
