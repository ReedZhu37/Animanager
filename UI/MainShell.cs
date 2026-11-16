using Godot;
using Animanager.Core.Models;
using Animanager.Core.Storage;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;
using System.Collections.Concurrent;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;

namespace Animanager.UI;

public partial class MainShell : Control
{
    private AssetDatabase _database=null!;
    private WorkspaceApi _api=null!;
    private ConsoleCommands _commands=null!;
    private Preview3DViewport _preview=null!;
    private LineEdit _search=null!,_tags=null!,_exportPath=null!;
    private ItemList _list=null!;
    private Label _count=null!,_pageLabel=null!,_status=null!,_title=null!,_detail=null!,_time=null!,_libraryCount=null!,_empty=null!;
    private Button _play=null!,_favorite=null!,_export=null!,_cancel=null!,_previous=null!,_next=null!;
    private OptionButton _clips=null!,_speed=null!,_exportType=null!;
    private HSlider _timeline=null!;
    private FileDialog _files=null!,_folder=null!,_output=null!,_uassetFolder=null!;
    private AcceptDialog _message=null!;
    private Godot.Timer _searchTimer=null!;
    private List<AssetItem> _items=[];
    private AssetItem? _selected;
    private string _category="All";
    private int _page;
    private bool _busy,_closing;
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _exportCancellation;
    private readonly ConcurrentQueue<string> _progress=new();
    private string _lastExport="";
    private double _tick;
    private readonly Dictionary<string,Button> _categories=new();
    private string _dataRoot="";

    public override async void _Ready()
    {
        try
        {
            var startup=OpenStartupLibrary();
            InitializeDisplay();BuildUi();
            _commands=new ConsoleCommands();AddChild(_commands);
            _commands.Initialize(_api,_preview,Refresh,SyncConsoleSelection,_libraries,BeforeLibrarySwitch,ActivateLibrary);
            ConnectWorkspace();
            if(_api==null){ShowLibraryDialog();SetStatus(startup.Error);return;}
            if(OS.GetCmdlineUserArgs().Contains("--export-options-test")){await Tests.ExportOptionsTests.Run(this,_preview,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Any(a=>a is "--asset-browse-test" or "--asset-browse-render")){await Tests.AssetBrowseTests.Run(this,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--library-p0-test")){await Tests.LibraryP0Tests.Run(this,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--motion-render")){await Tests.MotionTests.Render(this,_preview,_api,_commands);return;}
            var motionTest=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--motion-test="));
            if(motionTest!=null){await Tests.MotionTests.Run(this,_preview,_api,_commands,Path.GetFullPath(motionTest[14..]));return;}
            if(OS.GetCmdlineUserArgs().Contains("--uasset-pack-render")){await Tests.UassetPackTests.Render(this,_preview,_api,_commands);return;}
            var uassetPack=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--uasset-pack-test="));
            if(uassetPack!=null){await Tests.UassetPackTests.Run(this,_preview,_api,_commands,Path.GetFullPath(uassetPack[19..]),Path.GetFullPath(OS.GetCmdlineUserArgs().Single(a=>a.StartsWith("--source-reference="))[19..]));return;}
            var standardCueTest=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--standard-uasset-test="));
            if(standardCueTest!=null){await Tests.StandardUassetTests.Run(this,_preview,_api,_commands,Path.GetFullPath(standardCueTest[23..]));return;}
            var cueTest=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--uasset-test="));
            if(cueTest!=null){await Tests.UassetTests.Run(this,_preview,_api,_commands,Path.GetFullPath(cueTest[14..]));return;}
            if(OS.GetCmdlineUserArgs().Contains("--cue-prepare")){Tests.CueComparisonTests.Prepare(this,_preview);return;}
            var cueCompare=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--cue-compare="));
            if(cueCompare!=null){await Tests.CueComparisonTests.Compare(this,_preview,Path.GetFullPath(cueCompare[14..]));return;}
            GetWindow().FilesDropped+=paths=>_ = Import(paths);
            if(OS.GetCmdlineUserArgs().Contains("--self-test"))
            {
                await Tests.PrototypeTests.Run(this,_preview);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--bone-test"))
            {
                await Tests.BoneTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--bone-ui-test"))
            {
                await Tests.BoneInterfaceTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--multi-bone-test"))
            {
                await Tests.MultiTrajectoryTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--multi-bone-ui-test"))
            {
                await Tests.MultiBoneInterfaceTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--pipeline-test"))
            {
                await Tests.PipelineTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--batch-test"))
            {
                await Tests.BatchTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--interface-test"))
            {
                await Tests.InterfaceTests.Run(this,_preview,_api,_commands);return;
            }
            if(OS.GetCmdlineUserArgs().Contains("--smoke-test"))
            {
                await Tests.BundleSmokeTests.Run(this,_preview,_api,_commands);return;
            }
            var packArgument=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--asset-pack-test="));
            if(packArgument!=null)
            {
                await Tests.AssetPackTests.Run(this,_preview,_api,_commands,Path.GetFullPath(packArgument[18..]));return;
            }
            Refresh();
            if(startup.Bootstrap)
            {
                if(Directory.Exists(RuntimePaths.Bundled("BuiltinAssets/Baselines")))await ImportBaselines();
                else await ImportSamples();
            }
            else if(_items.Count>0)SelectOnly(0);
            if(startup.Error.Length>0)SetStatus(startup.Error);
            else if(_workspaceError.Length>0)SetStatus(_workspaceError);
            else if(!startup.Bootstrap)SetStatus("本地资产库已就绪 · 拖入文件或文件夹开始导入");
            var previewAsset=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--preview-asset="));
            if(previewAsset!=null){_commands.Load(previewAsset[16..]);await _commands.PendingTask;}
            if(OS.GetCmdlineUserArgs().Contains("--console"))_commands.Open();
        }
        catch(Exception ex) { GD.PushError(ex.ToString());if(_message!=null)ShowError("启动失败",ex.Message); }
    }


    private FileDialog Dialog(FileDialog.FileModeEnum mode,string title)
    {
        var dialog=new FileDialog{FileMode=mode,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true,Title=title};AddChild(dialog);return dialog;
    }
    private void Refresh()=>RefreshAssetBrowse();
    private async void SelectAsset(int index)
    {
        if(index<0||index>=_items.Count||_items[index].Trashed)return;
        var api=_api;
        _selected=_items[index];_title.Text=_selected.Name;_tags.Text=_selected.Tags;
        var asset=_selected;
        _favorite.Text=_selected.Favorite?"已收藏":"收藏";
        _clips.Clear();SyncExportControls();SetStatus("正在加载 · "+_selected.Name);
        try
        {
            using var activity=api.BeginActivity();
            var path=await api.Storage.AcquireLocalFileAsync(asset.VirtualPath);
            if(_api!=api||_selected?.Id!=asset.Id)return;
            _preview.LoadFile(path);
            _previewAssetId=_selected.Id;_previewName.Text=_selected.Name;_exportSummary.Text="导出资产："+_selected.Name;
            _commands?.TrackSelection(asset.Id);
            foreach(var clip in _preview.Clips)_clips.AddItem(clip);
            if(_preview.Clips.Length==0)_clips.AddItem("无动画");
            _clips.Disabled=_preview.Clips.Length==0;
            _preview.SetSpeed(new[]{0.5,1,1.5,2}[_speed.Selected]);
            _detail.Text=$"{_selected.SourceFormat.TrimStart('.').ToUpperInvariant()}   ·   {FormatSize(_selected.Size)}   ·   {_preview.MeshCount} 网格   ·   {_preview.BoneCount} 骨骼   ·   {_preview.Clips.Length} 动画";
            var type=_preview.Clips.Length>0?"Animation":"Model";
            if(type!=_selected.Type){api.SetAssetType(asset.Id,type);Refresh();}
            SyncExportControls();
            SetStatus(_selected.SourceFormat==".fbx"?"FBX 实验性预览：外部纹理未打包时可能缺失；建议使用 GLB。":"预览已就绪 · 空格播放 / 暂停，F 重置视角");
        }
        catch(Exception ex){_previewAssetId="";_commands?.ForgetSelection();_preview.Clear();_detail.Text="此资产无法预览，请检查文件格式与依赖。";ShowError("加载失败",ex.Message);}
        if(_preview.Motion!=null)_detail.Text+="\n"+_preview.Motion.Summary;
        UpdateTransport();
    }
    private async Task ImportSamples() => await Import([RuntimePaths.Bundled("BuiltinAssets/Samples")]);
    private async Task ImportBaselines()=>await Import([RuntimePaths.Bundled("BuiltinAssets/Baselines")]);
    private void SyncConsoleSelection(AssetItem asset)
    {
        _assetSelection.Clear();_assetSelection.Add(asset.Id);_assetAnchor=asset.Id;
        _previewAssetId=asset.Id;_previewName.Text=asset.Name;_exportSummary.Text="导出资产："+asset.Name;
        _selected=asset;_title.Text=asset.Name;_tags.Text=asset.Tags;_favorite.Text=asset.Favorite?"已收藏":"收藏";
        _clips.Clear();foreach(var clip in _preview.Clips)_clips.AddItem(clip);
        if(_preview.Clips.Length==0)_clips.AddItem("无动画");_clips.Disabled=_preview.Clips.Length==0;
        _detail.Text=$"{asset.SourceFormat} · {_preview.MeshCount} 网格 · {_preview.BoneCount} 骨骼 · {_preview.Clips.Length} 动画";
        if(_preview.Motion!=null)_detail.Text+="\n"+_preview.Motion.Summary;
        SyncExportControls();UpdateTransport();Refresh();
    }
    private async Task Import(string[] paths,bool uasset=false)
    {
        if(_api==null){SetStatus("请先打开或新建资产库。");return;}
        if(_busy){SetStatus("已有导入任务正在运行，请等待或取消。");return;}
        _busy=true;_cancel.Visible=true;_importCancellation=new CancellationTokenSource();
        try
        {
            using var activity=_api.BeginActivity();
            var plan=uasset?_api.Planner.ImportUasset(paths):await _api.Planner.ImportAsync(paths,_importCancellation.Token);
            var id=_api.Jobs.Enqueue(plan);_cancel.Visible=false;
            var result=await _api.Jobs.WaitAsync(id);
            if(_closing)return;
            while(_progress.TryDequeue(out _)){}
            _page=0;Refresh();SetStatus(result.Kind==JobKind.ImportUasset?$".uasset · {JobStateName(result.State)} · 新增 {result.Items.Sum(i=>i.ImportedAssets)} · 重复 {result.Items.Sum(i=>i.DuplicateAssets)} · 不支持 {result.Items.Sum(i=>i.UnsupportedAssets)} · 失败 {result.Failed}":$"入库 · {JobStateName(result.State)} · 新增 {result.Succeeded} · 重复 {result.Skipped} · 失败 {result.Failed}");
            if(_selected==null&&_items.Count>0&&_category!="Trash")SelectOnly(0);
        }
        catch(OperationCanceledException){if(!_closing){Refresh();SetStatus("导入已取消，已完成的资产已保留。");}}
        catch(Exception ex){if(!_closing)ShowError("导入失败",ex.Message);}
        finally{_busy=false;_importCancellation?.Dispose();_importCancellation=null;if(!_closing)_cancel.Visible=false;}
    }
    private sealed class QueueProgress(ConcurrentQueue<string> queue):IProgress<string>{public void Report(string value)=>queue.Enqueue(value);}
    private void SaveTags()
    {
        if(_selected==null)return;
        try{_api.SetTags(_selected.Id,_tags.Text);Refresh();SetStatus("标签已保存，可立即搜索。");}
        catch(Exception ex){ShowError("保存失败",ex.Message);}
    }
    private void ToggleFavorite()
    {
        if(_selected==null)return;
        try{_api.SetFavorite(_selected.Id,!_selected.Favorite);Refresh();}
        catch(Exception ex){ShowError("保存失败",ex.Message);}
    }
    private void TogglePlayback(){_preview.TogglePlay();UpdateTransport();}
    private void UpdateTransport()
    {
        if(_preview==null)return;
        _play.Text=_preview.Playing?"暂停":"播放";_play.Disabled=_preview.Clips.Length==0;
        _timeline.Editable=_preview.Duration>0;_timeline.MaxValue=Math.Max(_preview.Duration,0.001);
        _timeline.SetValueNoSignal(Math.Min(_preview.Playhead,_preview.Duration));
        _time.Text=$"{_preview.Playhead:F2} / {_preview.Duration:F2} s";
        if(_preview.Motion is {} motion)_detail.Text=_detail.Text.Split('\n')[0]+"\n"+motion.Summary;
        _favorite.Disabled=_selected==null;
        SyncExportControls();
    }
    public override void _Process(double delta)
    {
        ProcessAssetBrowse();
        _tick+=delta;if(_tick<0.08)return;_tick=0;
        if(_workspace!=null)UpdateWorkspace();
        string? latest=null;while(_progress.TryDequeue(out var next))latest=next;if(latest!=null)SetStatus(latest);
        // Console can seek while paused; keep the shared playhead visible in the transport too.
        if(_preview!=null)UpdateTransport();
    }
    public override void _UnhandledKeyInput(InputEvent ev)
    {
        if(ev is not InputEventKey {Pressed:true,Echo:false} key)return;
        if(_consoleOpen||Animanager.UI.Workspace.DisplayScaleController.IsComposing)return;
        if(key.CtrlPressed&&key.Keycode==Key.F){_workspace.ShowPanel("assets");Callable.From(()=>_search.GrabFocus()).CallDeferred();GetViewport().SetInputAsHandled();return;}
        if(key.Keycode==Key.F6){_workspace.CycleFocus();GetViewport().SetInputAsHandled();return;}
        if(GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)return;
        if(!_workspace.IsPanelVisible("preview"))return;
        if(!_preview.HasFocus()&&!_preview.GetGlobalRect().HasPoint(GetGlobalMousePosition()))return;
        if(key.Keycode==Key.Space){TogglePlayback();GetViewport().SetInputAsHandled();}
        if(key.Keycode==Key.F){_preview.FrameModel();GetViewport().SetInputAsHandled();}
    }
    public override void _ExitTree(){_closing=true;CancelAssetBrowse();SaveWorkspace();_preview?.Bones?.Clear();if(_api!=null)_api.Jobs.Changed-=OnWorkspaceJob;_importCancellation?.Cancel();_exportCancellation?.Cancel();_libraries?.Dispose();}
    private static string FormatSize(long bytes)=>bytes>=1048576?$"{bytes/1048576.0:F1} MB":$"{bytes/1024.0:F1} KB";
    private void SetStatus(string message){_status.Text=message;_status.TooltipText=message;}
    private void ShowError(string title,string message){SetStatus(title+" · "+message);_message.Title=title;_messageBody.Text=message;PopupFit(_message,new(560,240));}
    private void ShowHelp()
    {
        _message.Title="Animanager · 使用说明";
        _messageBody.Text="1. 拖入文件或文件夹，或点击工具栏的导入。资产复制到本地库，按内容去重。\n2. 拖动面板标签，在其他面板中央合并、边缘停靠；拖动分隔线调整大小。面板菜单可隐藏、最大化；工作区菜单保存和恢复布局。\n3. 小窗口自动切到标签布局。显示菜单支持系统 DPI 与 100%–200% 缩放。\n4. 点击资产预览；空格播放，右键旋转，中键或 Shift+左键平移，滚轮缩放。\n5. 添加标签和收藏。Ctrl+F 搜索，F6 切换面板焦点，F12 打开 Limbo Console。\n6. 导出支持当前资产、跨页多选或筛选结果，选择网格、片段、时间范围及采样帧率；.res / .glb / .fbx 均可在任务面板取消或重试。\n7. 资产列表自动生成缩略图；属性筛选支持骨架、时长、源帧率和未知值。\n\n动画保留源骨架与轨道，当前不进行重定向。FBX 外部纹理未自动打包。FBX 导出需要安装 Blender；其他基础操作不需要。";
        PopupFit(_message,new(640,360));
    }
}

