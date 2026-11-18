using System.Security.Cryptography;
using System.Text.Json;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Pipeline;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.Tests;

public static class ThirdFeedbackTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        string folder=Path.Combine(api.Storage.Root,"third-feedback");Directory.CreateDirectory(folder);
        var checks=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);GD.Print("PASS "+text);}
        async Task Frames(int n=5){for(int i=0;i<n;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        void HideDialogs(){foreach(var d in Preview3DViewport.Descendants(owner).OfType<AcceptDialog>())d.Hide();}
        async Task Capture(string name,Viewport? view=null)
        {
            if(DisplayServer.GetName()=="headless")return;
            await Frames();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=(view??owner.GetViewport()).GetTexture().GetImage();image.SavePng(Path.Combine(folder,name+".png"));
        }
        try
        {
            string invalid=Path.Combine(folder,"invalid-folder");Directory.CreateDirectory(Path.Combine(invalid,"nested"));
            File.WriteAllText(Path.Combine(invalid,"nested","broken.uasset"),"broken package");
            File.Copy(RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Walk.glb"),Path.Combine(invalid,"Walk.glb"));
            var mixed=await api.Planner.ImportAsync([invalid,Path.Combine(invalid,"nested")]);
            Check(mixed.Items.Count==2&&mixed.Items.Count(i=>i.IsUasset)==1&&mixed.Items.Single(i=>i.IsUasset).Input==invalid,"folder scan preserves UE dependency root and deduplicates overlapping directories");
            var mixedResult=await api.Jobs.WaitAsync(api.Jobs.Enqueue(mixed));
            Check(mixedResult.Succeeded==1&&mixedResult.Failed==1&&mixedResult.Items.Any(i=>i.Source.IsUasset&&i.Error.Length>0),"mixed directory imports GLB and reports broken uasset without suppressing successes");
            var loaded=api.Find("Walk");
            string empty=Path.Combine(folder,"empty");Directory.CreateDirectory(empty);
            await owner.Import([empty]);
            Check(Preview3DViewport.Descendants(owner).OfType<AcceptDialog>().Any(d=>d.Visible&&d.Title=="导入失败"),"empty directory has an explicit failure dialog");HideDialogs();

            string? source=OS.GetCmdlineUserArgs().FirstOrDefault(s=>s.StartsWith("--uasset-feedback-source="))?.Split('=',2)[1];
            if(source!=null)
            {
                var hashes=Directory.EnumerateFiles(source,"*.uasset",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
                await owner.Import([source]);
                var result=api.Jobs.Get(api.Jobs.LatestId!);
                Check(result.Kind==JobKind.Import&&result.Items.Count==1&&result.Items[0].Source.IsUasset,"ordinary folder import automatically selects the uasset reader");
                Check(result.Items.Sum(i=>i.ImportedAssets)==58&&result.Failed==0,"Dodge UE4.27 imports all 58 AnimSequence assets");
                Check(result.Items.Sum(i=>i.UnsupportedAssets)==77&&result.Items[0].Warnings.Count>0,"77 non-sequence resources have explicit unsupported reasons");
                var dialog=Preview3DViewport.Descendants(owner).OfType<AcceptDialog>().Single(d=>d.Visible&&d.Title.StartsWith("导入结果"));
                Check(Preview3DViewport.Descendants(dialog).OfType<RichTextLabel>().Any(l=>l.Text.Contains("新增 58")&&l.Text.Contains("不支持 77")),"visible import summary reports counts and detailed report location");
                await Capture("dodge-import",dialog);HideDialogs();owner.Workspace.CloseDrawer();
                var referenceArg=OS.GetCmdlineUserArgs().FirstOrDefault(s=>s.StartsWith("--uasset-feedback-reference="));
                if(referenceArg!=null)
                {
                    using var doc=JsonDocument.Parse(File.ReadAllText(referenceArg.Split('=',2)[1]));
                    double maxP=0,maxAngle=0;int sampled=0;
                    foreach(var clip in doc.RootElement.GetProperty("clips").EnumerateArray())
                    {
                        var asset=api.Find(clip.GetProperty("name").GetString()!);
                        preview.LoadFile(api.Storage.Resolve(asset.VirtualPath));preview.SetLoop(false);
                        var skeleton=Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
                        foreach(var sample in clip.GetProperty("samples").EnumerateArray())
                        {
                            preview.Seek(sample.GetProperty("time").GetDouble());
                            foreach(var bone in sample.GetProperty("bones").EnumerateArray())
                            {
                                float[] V(string key)=>bone.GetProperty(key).EnumerateArray().Select(v=>v.GetSingle()).ToArray();
                                var p=V("p");var q=V("r");var s=V("s");var actual=skeleton.GetBonePose(skeleton.FindBone(bone.GetProperty("name").GetString()!));
                                maxP=Math.Max(maxP,actual.Origin.DistanceTo(new Vector3(p[0],-p[1],p[2])*.01f));
                                maxAngle=Math.Max(maxAngle,2*Math.Acos(Math.Clamp(Math.Abs(actual.Basis.Orthonormalized().GetRotationQuaternion().Dot(new Quaternion(-q[0],q[1],-q[2],q[3]).Normalized())),0,1))*180/Math.PI);
                                if(!actual.Basis.Scale.IsEqualApprox(new(s[0],s[1],s[2])))throw new Exception("Source scale differs");
                                sampled++;
                            }
                        }
                        await Frames(1);
                    }
                    GD.Print($"DODGE_SOURCE_SAMPLES {sampled} position={maxP} angle={maxAngle}");
                    Check(sampled>10000&&maxP<.0001&&maxAngle<.1,"58 converted clips preserve independent UE4.27 source position, rotation and scale samples");
                }
                await owner.Import([source]);result=api.Jobs.Get(api.Jobs.LatestId!);
                Check(result.Items.Sum(i=>i.ImportedAssets)==0&&result.Items.Sum(i=>i.DuplicateAssets)==58,"reimport reports all 58 duplicates without adding new assets");HideDialogs();owner.Workspace.CloseDrawer();
                Check(hashes.All(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))==p.Value),"all original Dodge packages remain byte-for-byte unchanged");
                await owner.SelectPreviewCharacterAsync("ue5-mannequin");
                var dodge=api.Search().Items.First(a=>a.Name.Contains("A_DiveRoll_135"));await owner.PreviewAssetAsync(dodge);
                Check(preview.AppliedRetarget?.TargetRig=="ue5-mannequin"&&preview.Clips.Length>0,"imported Dodge animation previews on the configured UE5 character");
                preview.Seek(.4);preview.Bones.Select("hand_l");preview.Bones.ShowTrail(true);await preview.Bones.PendingSampling;
            }
            for(int i=0;i<125;i++)api.Database.Insert(loaded with{Id=Guid.NewGuid().ToString("N"),Name=$"GridRecord_{i:000}",FileHash=Guid.NewGuid().ToString("N"),Tags=i==117?"检索标签":""});
            owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,1000);HideDialogs();owner.Workspace.CloseDrawer();owner.Workspace.ShowPanel("assets");await Frames(10);
            var assets=owner.Workspace.Panel("assets");var browser=(AssetBrowser)assets.FindChild("AssetBrowser",true,false)!;
            var search=(LineEdit)assets.FindChild("AssetBrowserSearch",true,false)!;var global=(LineEdit)owner.FindChild("AssetSearch",true,false)!;
            Button Button(string name)=>(Button)assets.FindChild(name,true,false)!;
            async Task Search(string text){search.Text=text;search.EmitSignal(LineEdit.SignalName.TextSubmitted,text);await owner.PendingBrowse;await Frames();}
            await Search("GridRecord");browser.SetMode(AssetBrowser.ViewMode.List);await Frames();
            Check(browser.Count==60&&!Button("AssetPageLast").Disabled&&Button("AssetPageFirst").Disabled,"search covers the full library and starts at page one");
            Button("AssetPageLast").EmitSignal(BaseButton.SignalName.Pressed);await owner.PendingBrowse;await Frames();
            Check(browser.Count==5&&Button("AssetPageLast").Disabled&&!Button("AssetPageFirst").Disabled,"last-page button reaches the final five of 125 search results");
            Button("AssetPageFirst").EmitSignal(BaseButton.SignalName.Pressed);await owner.PendingBrowse;await Frames();
            Check(browser.Count==60&&Button("AssetPageFirst").Disabled&&browser.Scroll.ScrollVertical==0,"first-page button resets the page and scroll position");
            browser.Scroll.ScrollVertical=310;await Frames();
            var header=browser.List.HeaderRect;
            Check(Math.Abs(browser.List.GlobalPosition.Y+header.Position.Y-browser.Scroll.GlobalPosition.Y)<1,"list header stays pinned to the top after scrolling");
            await Capture("sticky-header");
            if(DisplayServer.GetName()=="headless")owner.GetViewport().NotifyMouseEntered();
            var point=browser.List.GetGlobalTransformWithCanvas()*new Vector2(45,header.Position.Y+16);
            using(var down=new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left,Pressed=true})owner.GetViewport().PushInput(down,true);
            using(var up=new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left})owner.GetViewport().PushInput(up,true);
            await owner.PendingBrowse;await Frames();
            Check(browser.List.SortKey=="name","pinned header accepts sort clicks at its visible position");
            browser.ScrollToIndex(35);await Frames();
            Check(browser.List.GlobalPosition.Y+browser.RowRect(35).Position.Y>=browser.Scroll.GlobalPosition.Y+AssetList.HeaderHeight-1,"keyboard scroll leaves the selected row below the pinned header");
            await Search("GridRecord_124");Check(browser.Count==1&&browser.Rows[0].Name=="GridRecord_124"&&global.Text==search.Text,"asset search matches names across pages and synchronizes the global field");
            search.Text="GridRecord_123";search.EmitSignal(LineEdit.SignalName.TextChanged,search.Text);
            await owner.ToSignal(owner.GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);await owner.PendingBrowse;
            Check(browser.Count==1&&browser.Rows[0].Name=="GridRecord_123","typing in the asset search applies the debounced query without Enter");
            await Search("检索标签");Check(browser.Count==1&&browser.Rows[0].Name=="GridRecord_117","asset search supports Chinese tags");
            await Search("no-such-asset");Check(browser.Count==0&&Button("AssetPageFirst").Disabled&&Button("AssetPageLast").Disabled,"empty search disables boundary navigation");
            global.Text="GridRecord";global.EmitSignal(LineEdit.SignalName.TextSubmitted,global.Text);await owner.PendingBrowse;await Frames();
            Check(search.Text==global.Text&&browser.Count==60,"global search synchronizes the asset panel and clearing restores results");
            owner.DisplayScale.SetScale(2);owner.GetWindow().Size=new(1024,700);owner.Workspace.ShowPanel("assets");await Frames(15);
            Check(owner.GetViewport().GetVisibleRect().Encloses(search.GetGlobalRect())&&owner.GetViewport().GetVisibleRect().Encloses(Button("AssetPageLast").GetGlobalRect()),"search and page boundaries remain visible at 200 percent");
            Check(browser.Scroll.Size.Y>=AssetList.HeaderHeight+AssetList.RowHeight,"compact toolbar leaves room for asset rows below the fixed header");
            await Capture("list-200");
            if(DisplayServer.GetName()!="headless")
            {
                owner.DisplayScale.SetScale(1);owner.GetWindow().Size=new(1440,1000);owner.Workspace.FocusPanel("preview");await Frames(15);
                var root=new Node3D();var mesh=new ImmediateMesh();mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);ScreenSpaceOverlay.Segment(mesh,new(-.4f,0,0),new(.4f,0,0),Colors.Red);mesh.SurfaceEnd();
                var line=new MeshInstance3D{Mesh=mesh};root.AddChild(line);
                var marker=new MeshInstance3D{Mesh=new QuadMesh{Size=new(2,2)},Position=new(0,.2f,0)};root.AddChild(marker);
                preview.SetClay(false);preview.AttachModel(root);
                var material=ScreenSpaceOverlay.Lines(true);line.MaterialOverride=material;marker.MaterialOverride=ScreenSpaceOverlay.Marker(Colors.Blue,1,true);
                preview.FocusPoint(Vector3.Zero);preview.SnapAxis(2);preview.ToggleProjection();
                async Task<(int Line,int Marker)> Measure(string name)
                {
                    preview.Redraw();await Frames();await owner.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                    using var image=preview.View.GetTexture().GetImage();image.SavePng(Path.Combine(folder,name+".png"));
                    int x=image.GetWidth()/2,red=0,minX=int.MaxValue,maxX=-1;
                    for(int y=0;y<image.GetHeight();y++){var c=image.GetPixel(x,y);if(c.R>.65&&c.G<.25&&c.B<.25)red++;}
                    for(int y=0;y<image.GetHeight();y++)for(int xx=x-12;xx<=x+12;xx++){var c=image.GetPixel(xx,y);if(c.B>.65&&c.R<.25&&c.G<.25){minX=Math.Min(minX,xx);maxX=Math.Max(maxX,xx);}}
                    return(red,maxX<0?0:maxX-minX+1);
                }
                var far=await Measure("line-far");preview.Zoom(.5f);var near=await Measure("line-near");
                GD.Print($"OVERLAY_PIXEL_WIDTH far={far} near={near}");
                Check(far.Line is >=1 and <=3&&Math.Abs(far.Line-near.Line)<=1&&far.Marker is >=5 and <=9&&Math.Abs(far.Marker-near.Marker)<=1,"rendered line thickness and endpoint size stay constant when zooming");
                preview.ToggleProjection();var ortho=await Measure("line-ortho");
                Check(Math.Abs(ortho.Line-far.Line)<=1&&Math.Abs(ortho.Marker-far.Marker)<=1,"orthographic projection retains line and point pixel size");
                owner.GetWindow().Size=new(1100,800);await Frames(12);var resized=await Measure("line-resized");
                Check(Math.Abs(resized.Line-far.Line)<=1&&Math.Abs(resized.Marker-far.Marker)<=1,"resizing the viewport retains line and point pixel size");
                var cover=new MeshInstance3D{Mesh=new BoxMesh{Size=new(.5f,.15f,.1f)},Position=new(0,0,.3f)};root.AddChild(cover);
                var through=await Measure("line-xray");ScreenSpaceOverlay.SetXray(material,false);var hidden=await Measure("line-depth");
                Check(through.Line>0&&hidden.Line==0,"fixed-width line respects X-ray and normal depth occlusion");
            }
            File.WriteAllLines(Path.Combine(folder,"results.txt"),checks.Select(s=>"PASS "+s));GD.Print("THIRD_FEEDBACK_TESTS_OK "+checks.Count);owner.GetTree().Quit();
        }
        catch(Exception e){File.WriteAllText(Path.Combine(folder,"results.txt"),string.Join("\n",checks)+"\nFAIL "+e);GD.PushError(e.ToString());owner.GetTree().Quit(1);}
    }
}
