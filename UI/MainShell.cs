using Godot;
using Animanager.UI.Shell;
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
    private LineEdit _search=null!,_assetSearch=null!,_tags=null!,_exportPath=null!;
    private AssetBrowser _browser=null!;
    private Label _count=null!,_pageLabel=null!,_status=null!,_title=null!,_detail=null!,_time=null!,_empty=null!;
    private Button _play=null!,_favorite=null!,_export=null!,_cancel=null!,_previous=null!,_next=null!,_firstPage=null!,_lastPage=null!;
    private OptionButton _exportType=null!;
    private HSlider _speed=null!;
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
    private int _previewGeneration;
    private string[]? _displayedClips;
    private string _dataRoot="";

    public override async void _Ready()
    {
        try
        {
            var startup=OpenStartupLibrary();
            InitializeDisplay();InitializeCharacterLibrary();BuildUi();
            _commands=new ConsoleCommands();AddChild(_commands);
            _commands.Initialize(_api,_preview,Refresh,SyncConsoleSelection,_libraries,BeforeLibrarySwitch,ActivateLibrary);
            _commands.PreviewLoader = PreviewAssetAsync;
            ConnectWorkspace();
            if(_api==null){ShowLibraryDialog();SetStatus(startup.Error);return;}
            if(OS.GetCmdlineUserArgs().Contains("--third-feedback-test")){await Tests.ThirdFeedbackTests.Run(this,_preview,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--second-feedback-test")){await Tests.SecondFeedbackTests.Run(this,_preview,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--feedback-test")){await Tests.FeedbackTests.Run(this,_preview,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--shell-visual-test")){await Tests.ShellVisualTests.Run(this,_api,_commands);return;}
            if(OS.GetCmdlineUserArgs().Contains("--character-test")){await Tests.CharacterLibraryTests.Run(this,_preview,_api,_commands);return;}
            var addCharacter=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--add-character="));
            if(addCharacter!=null)
            {
                var characterName=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--character-name="))?[17..];
                var entry=_characters.Import(Path.GetFullPath(addCharacter[16..]), characterName);
                _characters.Select(entry.Id);_previewCharacterId=entry.Id;RefreshCharacterChoices();
            }
            var retargetDemo=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--retarget-demo="));
            if(retargetDemo!=null){await Tests.RetargetCharacterDemo.Run(this,_preview,_api,_commands,Path.GetFullPath(retargetDemo[16..]));return;}
            var retargetProbe=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--retarget-probe="));
            if(retargetProbe!=null){await Tests.RetargetAssetProbe.Run(this,_preview,_api,_commands,Path.GetFullPath(retargetProbe[17..]));return;}
            if(OS.GetCmdlineUserArgs().Contains("--retarget-test")){await Tests.RetargetTests.Run(this,_preview,_api,_commands);return;}
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
            GetWindow().FilesDropped+=paths=>{if(_charactersDialog.Visible)_ = ImportCharacters(paths);else _ = Import(paths);};
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
            await PendingBrowse;
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
            if(previewAsset!=null){_commands.Load(previewAsset[16..]);await _commands.PendingTask;await PendingRetargetPreview;}
            if(OS.GetCmdlineUserArgs().Contains("--play")){await PendingRetargetPreview;if(!_preview.Playing)_preview.TogglePlay();}
            if(OS.GetCmdlineUserArgs().Contains("--console"))_commands.Open();
        }
        catch(Exception ex) { GD.PushError(ex.ToString());if(_message!=null)ShowError("启动失败",ex.Message); }
    }


    private FileDialog Dialog(FileDialog.FileModeEnum mode,string title)
    {
        var dialog=new FileDialog{FileMode=mode,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true,Title=title};AddChild(dialog);return dialog;
    }
    private void Refresh()=>RefreshAssetBrowse();
    internal Task PendingAssetPreview { get; private set; } = Task.CompletedTask;
    private void SelectAsset(int index)
    {
        if(index<0||index>=_items.Count||_items[index].Trashed)return;
        PreviewAssetAsync(_items[index]);
    }
    internal Task PreviewAssetAsync(AssetItem asset, CancellationToken ct = default)
    {
        PendingAssetPreview = LoadSelectedAssetAsync(asset, ct);
        PendingRetargetPreview = PendingAssetPreview;
        return PendingAssetPreview;
    }
    private async Task LoadSelectedAssetAsync(AssetItem asset, CancellationToken externalCancellation)
    {
        _retargetCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _retargetCancellation = cancellation;
        var api = _api; int generation = ++_previewGeneration;
        bool resumePlayback = _preview.Playing;
        _selected = asset; _title.Text = asset.Name; _tags.Text = asset.Tags;
        if (!_assetSelection.Contains(asset.Id)) { _assetSelection.Clear(); _assetSelection.Add(asset.Id); _assetAnchor = asset.Id; }
        _stage.SetLoadStatus("正在准备 " + asset.Name + "…");
        SyncExportControls();
        bool Current() => !_closing && _api == api && generation == _previewGeneration && _selected?.Id == asset.Id && !cancellation.IsCancellationRequested;
        try
        {
            using var activity = api.BeginActivity();
            var path = await api.Storage.AcquireLocalFileAsync(asset.VirtualPath, cancellation.Token);
            if (!Current()) return;
            using var source = new PreviewSource(path);
            LoadAssetRetargetConfiguration();
            if (_previewCharacterId.Length > 0 && source.HasAnimation)
            {
                SetRetargetCharacter(_previewCharacterId);
                _retargetTargetHash = ""; _retargetSourceHash = ""; _retargetSourceRig = "";
                var options = api.InspectRetarget(asset.Id, ReadRetargetOptions());
                _characterStatus.Text = "正在应用角色 · " + RetargetCharacter.Name;
                var result = await _characterCache.Get(path, options, () => api.PreviewRetargetAsync(asset.Id, options, cancellation.Token), cancellation.Token);
                if (!Current()) { result.Root.Free(); return; }
                try { _preview.AttachRetarget(result, options, path, 1, source, newSource: true); }
                catch { if (result.Root.GetParent() == null) result.Root.Free(); throw; }
                SetRetargetOptions(options); _retargetView.Select(1); SyncRetargetSummary();
                _characterStatus.Text = "Ready · " + RetargetCharacter.Name;
            }
            else
            {
                if (!Current()) return;
                _preview.AttachSource(source, notify: false);
            }
            _previewAssetId = asset.Id; _previewName.Text = asset.Name; _exportSummary.Text = "导出资产：" + asset.Name;
            _commands?.TrackSelection(asset.Id);
            _stage.SetClips(_preview.Clips, _preview.CurrentClip); _preview.SetSpeed(_speed.Value);
            _detail.Text = $"{asset.SourceFormat} · {_preview.MeshCount} 网格 · {_preview.BoneCount} 骨骼 · {_preview.Clips.Length} 动画";
            var type = _preview.Clips.Length > 0 ? "Animation" : "Model";
            if (type != asset.Type) { api.SetAssetType(asset.Id, type); asset.Type = type; }
            if (resumePlayback && !_preview.Playing) _preview.TogglePlay();
            _stage.SetLoadStatus(""); SetStatus("预览已就绪 · " + asset.Name);
            UpdateTransport(); Refresh();
        }
        catch (OperationCanceledException)
        {
            if (_retargetCancellation == cancellation && generation == _previewGeneration)
                _stage.SetLoadStatus("切换已取消，保留当前预览。");
        }
        catch (Exception e)
        {
            if (!Current()) return;
            _retargetEnabled.ButtonPressed = false;
            _retargetInfo.Text = e.Message; _characterStatus.Text = "待修复 · " + e.Message;
            _stage.SetLoadStatus("切换失败，保留当前预览。" + e.Message);
            _commands?.ForgetSelection(); SetStatus("无法预览 " + asset.Name + " · " + e.Message);
            SyncExportControls();
        }
        finally { if (_retargetCancellation == cancellation) _retargetCancellation = null; }
    }
    private async Task ImportSamples() => await Import([RuntimePaths.Bundled("BuiltinAssets/Samples")], autoCharacters:false);
    private async Task ImportBaselines()=>await Import([RuntimePaths.Bundled("BuiltinAssets/Baselines")], autoCharacters:false);
    private void SyncConsoleSelection(AssetItem asset)
    {
        _previewGeneration++;
        _assetSelection.Clear();_assetSelection.Add(asset.Id);_assetAnchor=asset.Id;
        _previewAssetId=asset.Id;_previewName.Text=asset.Name;_exportSummary.Text="导出资产："+asset.Name;
        _selected=asset;_title.Text=asset.Name;_tags.Text=asset.Tags;
        LoadAssetRetargetConfiguration();
        if (_preview.AppliedRetarget is { } appliedRetarget)
        { SetRetargetOptions(appliedRetarget); _retargetView.Select(_preview.RetargetViewMode); SyncRetargetSummary(); }
        _stage.SetClips(_preview.Clips,_preview.CurrentClip);
        _detail.Text=$"{asset.SourceFormat} · {_preview.MeshCount} 网格 · {_preview.BoneCount} 骨骼 · {_preview.Clips.Length} 动画";
        if(_preview.Motion!=null)_detail.Text+="\n"+_preview.Motion.Summary;
        SyncExportControls();UpdateTransport();Refresh();
        if(_previewCharacterId.Length>0&&_preview.AppliedRetarget==null)PendingRetargetPreview=ApplyCharacterForSource();
    }
    internal async Task Import(string[] paths,bool uasset=false, bool autoCharacters=true)
    {
        if(_api==null){SetStatus("请先打开或新建资产库。");return;}
        if(_busy){SetStatus("已有导入任务正在运行，请等待或取消。");return;}
        _busy=true;_cancel.Visible=true;_importCancellation=new CancellationTokenSource();
        try
        {
            using var activity=_api.BeginActivity();
            SetStatus("正在扫描导入文件…");
            var plan=uasset?_api.Planner.ImportUasset(paths):await _api.Planner.ImportAsync(paths,_importCancellation.Token);
            var id=_api.Jobs.Enqueue(plan);_cancel.Visible=false;
            _workspace.ShowPanel("jobs");RefreshJobs();
            int jobIndex=_jobIds.IndexOf(id);if(jobIndex>=0){_jobsList.Select(jobIndex);ShowJobDetails();}
            SetStatus("导入已开始 · "+plan.Items.Count+" 个任务；可在任务面板查看进度或取消。");
            var result=await _api.Jobs.WaitAsync(id);
            if(_closing)return;
            while(_progress.TryDequeue(out _)){}
            _page=0;Refresh();ShowImportResult(result);
            await PendingBrowse;
            if(_closing)return;
            if(_selected==null&&_items.Count>0&&_category!="Trash")SelectOnly(0);
            if (autoCharacters && !uasset)
            {
                var models = new List<string>();
                foreach (var item in result.Items.Where(i => i.State is ItemState.Succeeded or ItemState.Skipped))
                {
                    string input = item.Source.Input, ext = Path.GetExtension(input).ToLowerInvariant();
                    if (ext is not (".fbx" or ".glb" or ".gltf")) continue;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (_closing) return;
                    try
                    {
                        if (Core.Browse.AssetAttributeReader.Read(input, ext, _importCancellation.Token).Duration > 0) continue;
                        var scene = SceneAsset.Load(input, out _);
                        try
                        {
                            var nodes = Preview3DViewport.Descendants(scene).ToArray();
                            if (nodes.OfType<MeshInstance3D>().Any(m => m.Mesh != null) && !nodes.OfType<AnimationPlayer>().Any(p => p.GetAnimationList().Any(c => c != "RESET"))) models.Add(input);
                        }
                        finally { scene.Free(); }
                    }
                    catch (Exception e) { GD.PushWarning("角色检查：" + e.Message); }
                }
                if (models.Count > 0) await ImportCharacters(models.ToArray());
            }
        }
        catch(OperationCanceledException){if(!_closing){Refresh();SetStatus("导入已取消，已完成的资产已保留。");}}
        catch(Exception ex){if(!_closing)ShowError("导入失败",ex.Message);}
        finally{_busy=false;_importCancellation?.Dispose();_importCancellation=null;if(!_closing)_cancel.Visible=false;}
    }
    private sealed class QueueProgress(ConcurrentQueue<string> queue):IProgress<string>{public void Report(string value)=>queue.Enqueue(value);}
    private void SaveTags()
    {
        if(_selected==null)return;
        try{_api.SetTags(_selected.Id,_tags.Text);Refresh();SetStatus("标签已保存，可立即搜索。");_workspace.Toast("标签已保存");}
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
        if(!ReferenceEquals(_displayedClips,_preview.Clips)){_displayedClips=_preview.Clips;_stage.SetClips(_preview.Clips,_preview.CurrentClip);}
        _stage.SetCurrentClip(_preview.CurrentClip);
        if (_previewAssetId == _selected?.Id && _preview.SourceReadiness is { } readiness)
        {
            _stage.SetReadiness(readiness.Ready, readiness.Summary);
            _inspector.Readiness.Text = readiness.Summary;
            _inspector.Readiness.TooltipText = readiness.Summary;
        }
        else { _stage.SetReadiness(false, "未检查"); _inspector.Readiness.Text = "未检查"; }
        _stage.SetPlaying(_preview.Playing);_play.Disabled=_preview.Clips.Length==0;
        _timeline.Editable=_preview.Duration>0;_timeline.MaxValue=Math.Max(_preview.Duration,0.001);
        _timeline.SetValueNoSignal(Math.Min(_preview.Playhead,_preview.Duration));
        _stage.SetTime(_preview.Playhead, _preview.Duration, _preview.DisplayFps);
        if(_preview.Motion is {} motion)_detail.Text=_detail.Text.Split('\n')[0]+"\n"+motion.Summary;
        _favorite.Disabled=_selected==null;
        SyncInspector();SyncExportControls();
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
        if(key.Keycode==Key.Escape&&_workspace.Docking.DragActive){_workspace.Docking.CancelDrag();GetViewport().SetInputAsHandled();return;}
        if(key.Keycode==Key.Escape&&_workspace.ActiveDrawer.Length>0){_workspace.CloseDrawer();GetViewport().SetInputAsHandled();return;}
        if(key.CtrlPressed&&key.Keycode==Key.F){_workspace.ShowPanel("assets");Callable.From(()=>{_assetSearch.GrabFocus();_assetSearch.SelectAll();}).CallDeferred();GetViewport().SetInputAsHandled();return;}
        if(key.Keycode==Key.F6){_workspace.CycleFocus();GetViewport().SetInputAsHandled();return;}
        if(GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)return;
        if(!_workspace.IsPanelVisible("preview"))return;
        if(!_preview.HasFocus()&&!_preview.GetGlobalRect().HasPoint(GetGlobalMousePosition()))return;
        if(key.Keycode==Key.Space){TogglePlayback();GetViewport().SetInputAsHandled();}
        if(key.Keycode==Key.F){_preview.FrameModel();GetViewport().SetInputAsHandled();}
    }
    public override void _ExitTree(){_closing=true;CancelAssetBrowse();SaveWorkspace();_preview?.Bones?.Clear();_characterCache.Dispose();if(_api!=null)_api.Jobs.Changed-=OnWorkspaceJob;_importCancellation?.Cancel();_exportCancellation?.Cancel();_libraries?.Dispose();}
    private static string FormatSize(long bytes)=>bytes>=1048576?$"{bytes/1048576.0:F1} MB":$"{bytes/1024.0:F1} KB";
    private void SetStatus(string message){_workspace.Status.Report(message);}
    private void ShowError(string title,string message){SetStatus(title+" · "+message);_message.Title=title;_messageBody.Text=message;_workspace.Status.Report(title+" · "+message,Tokens.Danger);PopupFit(_message,new(560,240));}
    private void ShowHelp()
    {
        _message.Title="Animanager · 使用说明";
        _messageBody.Text="1. 拖入文件或文件夹，或点击工具栏的导入。资产复制到本地库，按内容去重。\n2. 动画卡片默认在底部，viewport 在中央；拖动面板标签可合并或分区，拖动分隔条调整大小，双击标签最大化／恢复。\n3. 小窗口通过标签或快捷导航切换内容；布局自动保存，更多菜单可恢复默认布局和设置 100%–200% 缩放。\n4. 点击资产预览；空格播放，中键/右键旋转，Shift+中键或 Shift+左键平移，滚轮缩放。\n5. 添加标签和收藏。Ctrl+F 搜索，F6 切换面板焦点，F12 打开 Limbo Console。\n6. 导出支持当前资产、跨页多选或筛选结果，选择网格、片段、时间范围及采样帧率；.res / .glb / .fbx 均可在任务面板取消或重试。\n7. 资产列表自动生成缩略图；属性筛选支持骨架、时长、源帧率和未知值。\n\n可通过角色库和重定向工具预览并导出目标角色动画。FBX 外部纹理未自动打包。FBX 导出需要安装 Blender；其他基础操作不需要。";
        PopupFit(_message,new(640,360));
    }
}

