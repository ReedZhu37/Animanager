using Godot;
using Animanager.UI.Shell;
using Animanager.Core.Browse;
using Animanager.UI.Workspace;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using Animanager.UI.Panels;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    public ShellLayout Workspace=>_workspace;
    public DisplayScaleController DisplayScale=>_display;
    private ShellLayout _workspace=null!;
    private DisplayScaleController _display=null!;
    private WorkspacePreferences _preferences=new();
    private string _preferencesPath="",_workspaceError="";
    private Godot.Timer _saveLayout=null!;
    private Label _scaleHint=null!,_previewName=null!;
    private ItemList _jobsList=null!;
    private RichTextLabel _jobDetails=null!;
    private List<string> _jobIds=[];
    private readonly System.Collections.Concurrent.ConcurrentQueue<JobUpdate> _jobUpdates=new();

    private ConfirmationDialog _quitDialog=null!;
    private RichTextLabel _messageBody=null!;
    private AppHeader _header=null!;
    private SideBar _sideBar=null!;
    private PreviewStage _stage=null!;
    private Inspector _inspector=null!;
    private SelectionBar _selectionBar=null!;
    private FilterBar _filterBar=null!;
    private AssetSort _sort=AssetSort.Default;
    private OptionButton _sortControl=null!;
    private PopupMenu _morePopup=null!,_importPopup=null!;
    private bool _loop=true;

    private bool _consoleOpen;
    private string _previewAssetId="";
    private Label _exportSummary=null!;

    private void InitializeDisplay()
    {
        var legacyPath=_dataRoot.Length==0?null:Path.Combine(_dataRoot,"workspace.json");
        _preferences=WorkspacePreferences.LoadForApp(_preferencesPath,legacyPath,out _workspaceError);
        _display=new DisplayScaleController();AddChild(_display);_display.SetScale(_preferences.Scale);
        Theme=ThemeFactory.Create();GetWindow().Title="Animanager";
        if(DisplayServer.GetName()!="headless"&&!OS.GetCmdlineUserArgs().Contains("--interface-test"))
        {
            GetWindow().Size=_preferences.HasPosition?new(_preferences.Width,_preferences.Height):new((int)(1280*_display.EffectiveScale),(int)(800*_display.EffectiveScale));
            if(_preferences.HasPosition)GetWindow().Position=new(_preferences.X,_preferences.Y);
            DisplayScaleController.ClampWindow(GetWindow(),!_preferences.HasPosition);
            if(_preferences.Maximized)GetWindow().Mode=Window.ModeEnum.Maximized;
        }
    }
    private void BuildUi()
    {
        _workspace=new ShellLayout{Name="Workspace"};AddChild(_workspace);_workspace.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _header=_workspace.Header; _status=_workspace.Status.MessageLabel; _scaleHint=_workspace.Status.ScaleLabel;
        _search=_header.SearchInput;_search.Name="AssetSearch";
        _searchTimer=new Godot.Timer{OneShot=true,WaitTime=.2};AddChild(_searchTimer);
        _search.TextChanged+=text=>{if(_assetSearch!=null&&_assetSearch.Text!=text)_assetSearch.Text=text;_searchTimer.Start();};
        _search.TextSubmitted+=_=>ApplyAssetSearch();
        _searchTimer.Timeout+=()=>{if(DisplayScaleController.IsComposing){_searchTimer.Start();return;}ApplyAssetSearch();};
        _workspace.Register("navigation","导航",BuildNavigation());
        _workspace.Register("assets","动画资产",BuildAssets());
        _workspace.Register("preview","预览",BuildPreview());
        _workspace.Register("inspector","属性",BuildInspector());
        _workspace.Register("bones","骨骼",new BoneTreePanel{Preview=_preview});
        _workspace.Register("retarget","重定向",BuildRetarget());
        _workspace.Register("export","导出",BuildExport());
        _workspace.Register("jobs","任务",BuildJobs());
        _workspace.Initialize(_preferences.DockLayout);
        _workspace.LayoutChanged+=SaveSoon;
        BuildHeaderMenus();
        _header.LibraryPressed+=ShowLibraryDialog;
        _header.ImportPressed+=()=>PopupMenuAtPointer(_importPopup);
        _header.ExportPressed+=()=>_workspace.ShowPanel("export");
        _header.JobsPressed+=()=>_workspace.ShowPanel("jobs");
        _header.MorePressed+=()=>PopupMenuAtPointer(_morePopup);
        _cancel=Button("取消扫描",()=>_importCancellation?.Cancel());_cancel.Visible=false;
        ((Container)_workspace.Status.GetChild(0)).AddChild(_cancel);
        _files=Dialog(FileDialog.FileModeEnum.OpenFiles,"导入资产（.uasset 自动识别标准骨架）");_files.Filters=["*.glb, *.gltf, *.fbx ; 3D 资产","*.motion.json ; 独立人体与道具动画","*.uasset, *.zip ; UE4.23 / 4.27 / UE5.0 / UE5.6 源动画"];_files.FilesSelected+=paths=>_ = Import(paths);
        _uassetFolder=Dialog(FileDialog.FileModeEnum.OpenDir,"导入 .uasset · UE4 / UE5 标准骨架");_uassetFolder.DirSelected+=path=>_ = Import([path],true);
        _folder=Dialog(FileDialog.FileModeEnum.OpenDir,"选择要扫描的资产文件夹");_folder.DirSelected+=path=>_ = Import([path]);
        _output=Dialog(FileDialog.FileModeEnum.OpenDir,"选择导出目录");_output.DirSelected+=path=>_exportPath.Text=path;
        _message=new AcceptDialog{Title="Animanager",OkButtonText="关闭"};AddChild(_message);
        _messageBody=new RichTextLabel{CustomMinimumSize=new(160,70),SelectionEnabled=true,BbcodeEnabled=false,FitContent=false};_message.AddChild(_messageBody);
        BuildLibraryDialogs();
        BuildCharacterLibraryDialog();
        _quitDialog=new ConfirmationDialog{Title="仍有任务正在执行",DialogText="取消未完成任务并退出？已完成的资产与输出会保留。",DialogAutowrap=true,OkButtonText="取消任务并退出",CancelButtonText="继续运行"};AddChild(_quitDialog);
        _quitDialog.Confirmed+=()=>_ = QuitSafely();GetTree().AutoAcceptQuit=false;
        GetWindow().CloseRequested+=()=>{if(_closing)return;if(_busy||_commands?.JobRunning==true)PopupFit(_quitDialog,new(440,160));else _ = QuitSafely();};
        _saveLayout=new Godot.Timer{OneShot=true,WaitTime=.5};AddChild(_saveLayout);_saveLayout.Timeout+=SaveWorkspace;
        _display.Changed+=()=>{_preferences.Scale=_display.RequestedScale;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);SaveSoon();};
        GetWindow().SizeChanged+=SaveSoon;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);UpdateTransport();
    }

    private Control BuildNavigation()
    {
        _sideBar=new SideBar();
        _sideBar.CategorySelected+=ShowAssetCategory;_sideBar.CollectionSelected+=ShowAssetCategory;
        _sideBar.OpenLibraryFolder+=OpenLibraryFolder;
        _sideBar.CreateCollection+=()=>LibraryAction(()=>AssetAction(10));
        _sideBar.TagSelected+=tag=>{_search.Text=tag;ApplyAssetSearch();};
        return _sideBar;
    }
    private Control BuildAssets()
    {
        var col=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};
        _assetSearch=Input("搜索当前库：资产名称或标签…   Ctrl+F");_assetSearch.Name="AssetBrowserSearch";
        _assetSearch.ClearButtonEnabled=true;_assetSearch.SizeFlagsHorizontal=SizeFlags.ExpandFill;col.AddChild(_assetSearch);
        _assetSearch.TextChanged+=text=>{if(_search.Text!=text)_search.Text=text;_searchTimer.Start();};
        _assetSearch.TextSubmitted+=text=>{_search.Text=text;ApplyAssetSearch();};
        var toolbar=new HFlowContainer();col.AddChild(toolbar);
        _browser=new AssetBrowser{Name="AssetBrowser"};
        var gridView=Button("网格",()=>_browser.SetMode(AssetBrowser.ViewMode.Grid));toolbar.AddChild(gridView);
        var listView=Button("列表",()=>_browser.SetMode(AssetBrowser.ViewMode.List));toolbar.AddChild(listView);
        _attributeToggle=Button("筛选",()=>{_workspace.ShowPanel("filters");LoadSkeletonOptions();});_attributeToggle.Name="AssetAttributeFilters";toolbar.AddChild(_attributeToggle);
        _sortControl=new OptionButton{Name="AssetSort",FitToLongestItem=false,CustomMinimumSize=new(100,28)};
        foreach(var title in new[]{"最近导入","名称","大小","收藏优先","时长","帧率"})_sortControl.AddItem(title);
        _sortControl.ItemSelected+=i=>SetAssetSort(new((AssetSortField)i,i is 0 or 2 or 3));toolbar.AddChild(_sortControl);
        BuildAssetActions(toolbar);
        _count=Label("0 个结果",true,11);toolbar.AddChild(_count);
        _filterBar=new FilterBar();col.AddChild(_filterBar);
        _filterBar.Cleared+=()=>ResetAssetAttributeFilter(true);
        _filterBar.Removed+=key=>{if(key=="skeleton")_skeletonFilter.Select(0);else if(key=="duration")_durationFilter.Text="";else _fpsFilter.Text="";ApplyAssetAttributeFilter();};
        _selectionBar=new SelectionBar();col.AddChild(_selectionBar);
        _selectionBar.ActionRequested+=id=>LibraryAction(()=>{switch(id){case "export":_workspace.ShowPanel("export");break;case "tags":AssetAction(5);break;case "favorite":AssetAction(7);break;case "trash":AssetAction(TrashAssetsAction);break;case "restore":AssetAction(RestoreAssetsAction);break;case "purge":AssetAction(PurgeAssetsAction);break;default:FillAssetMenu();PopupMenuAtPointer(_assetActions.GetPopup());break;}});
        col.AddChild(_browser);
        _browser.Selection.Changed+=()=>{if(_selectionCount!=null)UpdateAssetSelectionCount();};
        _browser.Activated+=e=>{if(_assetSelection.Contains(e.Card.Id)&&!_items[e.Index].Trashed)SelectAsset(e.Index);else if(_selected?.Id==e.Card.Id)ClearAssetPreview();};
        _browser.ContextMenuRequested+=ShowAssetContextMenu;
        _browser.DeleteRequested+=()=>{if(_assetSelection.Count>0)LibraryAction(()=>AssetAction(_category=="Trash"?PurgeAssetsAction:TrashAssetsAction));};
        _browser.SortRequested+=SetAssetSort;
        _browser.ViewModeChanged+=mode=>{_preferences.AssetView=mode.ToString();SaveSoon();};
        _browser.SetMode(_preferences.AssetView=="List"?AssetBrowser.ViewMode.List:AssetBrowser.ViewMode.Grid);
        _sort=new(_preferences.SortField,_preferences.SortDescending);_browser.SetSort(_sort);_sortControl.Select((int)_sort.Field);
        _empty=Wrapped("导入文件以建立资产库。");col.AddChild(_empty);
        var pagination=new HBoxContainer();toolbar.AddChild(pagination);
        _firstPage=Button("首页",()=>GoToAssetPage(0));_firstPage.Name="AssetPageFirst";pagination.AddChild(_firstPage);
        _previous=Button("上一页",()=>GoToAssetPage(_page-1));_previous.Name="AssetPagePrevious";pagination.AddChild(_previous);
        _pageLabel=Label("1 / 1",true,11);_pageLabel.SizeFlagsHorizontal=SizeFlags.ExpandFill;_pageLabel.HorizontalAlignment=HorizontalAlignment.Center;pagination.AddChild(_pageLabel);
        _next=Button("下一页",()=>GoToAssetPage(_page+1));_next.Name="AssetPageNext";pagination.AddChild(_next);
        _lastPage=Button("尾页",()=>GoToAssetPage(Math.Max(0,(FilteredAssetCount-1)/AssetPageSize)));_lastPage.Name="AssetPageLast";pagination.AddChild(_lastPage);
        _firstPage.TooltipText="首页";_previous.TooltipText="上一页";_next.TooltipText="下一页";_lastPage.TooltipText="尾页";
        void AdaptToolbar()
        {
            bool compact=col.Size.X<620;
            _count.Visible=_selectionCount.Visible=!compact;
            gridView.Visible=!compact||_browser.Mode!=AssetBrowser.ViewMode.Grid;
            listView.Visible=!compact||_browser.Mode!=AssetBrowser.ViewMode.List;
            _firstPage.Text=compact?"«":"首页";_previous.Text=compact?"‹":"上一页";
            _next.Text=compact?"›":"下一页";_lastPage.Text=compact?"»":"尾页";
        }
        col.Resized+=AdaptToolbar;_browser.ViewModeChanged+=_=>AdaptToolbar();
        var filters=new VBoxContainer();BuildAssetBrowseControls(filters);_workspace.Register("filters","属性筛选",filters);
        return col;
    }
    private void SetAssetSort(AssetSort sort)
    {
        _sort=sort;_browser.SetSort(sort);_sortControl.Select((int)sort.Field);
        _sortControl.TooltipText=sort.Descending?"降序；点击列表表头可切换方向":"升序；点击列表表头可切换方向";
        _preferences.SortField=sort.Field;_preferences.SortDescending=sort.Descending;SaveSoon();_page=0;Refresh();
    }
    private Control BuildPreview()
    {
        _stage=new PreviewStage();_preview=_stage.Viewport;_previewName=_stage.TitleLabel;
        _play=_stage.PlayButton;_speed=_stage.SpeedControl;_timeline=_stage.Timeline;_time=_stage.TimeLabel;
        BuildCharacterSelector(_stage.CharacterSlot);
        _stage.SettingsPressed += () => PopupFit(_stage.SettingsDialog, new(520, 540));
        _stage.PlayPressed+=TogglePlayback;_stage.SeekRequested+=time=>{_preview.Seek(time);UpdateTransport();};
        _stage.StepRequested+=direction=>{_preview.Seek(Math.Clamp(_preview.Playhead+direction*_preview.FrameStep,0,_preview.Duration));UpdateTransport();};
        _stage.SpeedChanged+=speed=>_preview.SetSpeed(speed);
        _stage.LoopToggled+=()=>{_loop=!_loop;_preview.SetLoop(_loop);_stage.SetLoop(_loop);};_stage.SetLoop(true);
        bool clay=true;_stage.ClayToggled+=()=>{clay=!clay;_preview.SetClay(clay);};
        _stage.FramePressed+=()=>_preview.FrameModel();_stage.BonesPressed+=()=>_workspace.ShowPanel("bones");
        _stage.ClipSelected+=clip=>{bool playing=_preview.Playing;_preview.SelectClip(clip);if(playing)_preview.TogglePlay();_stage.SetCurrentClip(clip);UpdateTransport();};
        return _stage;
    }
    private Control BuildInspector()
    {
        _inspector=new Inspector();_title=_inspector.TitleLabel;_detail=_inspector.Summary;_tags=_inspector.TagsInput;_favorite=_inspector.FavoriteButton;
        _inspector.FavoriteToggled+=ToggleFavorite;_inspector.TagsSaved+=SaveTags;
        _inspector.TagAddRequested+=()=>_tags.GrabFocus();
        _inspector.TagSelected+=tag=>{_search.Text=tag;ApplyAssetSearch();};
        _inspector.ClipSelected+=clip=>{_preview.SelectClip(clip);_preview.TogglePlay();_stage.SetCurrentClip(clip);UpdateTransport();};
        _inspector.MoreRequested+=()=>{FillAssetMenu();PopupMenuAtPointer(_assetActions.GetPopup());};
        return _inspector;
    }
    private Control BuildExport()=>BuildConfiguredExport();
    private void PopupMenuAtPointer(PopupMenu menu)
    {
        menu.ResetSize();menu.PopupOnParent(new Rect2I((Vector2I)(GetWindow().GetFinalTransform()*GetGlobalMousePosition()),Vector2I.One));
    }
    private void BuildHeaderMenus()
    {
        _importPopup=new PopupMenu();AddChild(_importPopup);
        foreach(var title in new[]{"导入文件…","导入文件夹…","导入 .uasset 文件夹…","导入基准资产"})_importPopup.AddItem(title);
        _importPopup.IdPressed+=id=>{switch(id){case 0:_files.PopupCenteredRatio(.7f);break;case 1:_folder.PopupCenteredRatio(.7f);break;case 2:_uassetFolder.PopupCenteredRatio(.7f);break;case 3:_ = ImportBaselines();break;}};
        _morePopup=new PopupMenu();AddChild(_morePopup);
        foreach(var title in new[]{"资产库管理…","导入…","导出…","任务…","角色库…","骨骼检查","重定向","Console · F12","使用说明","显示导航"})_morePopup.AddItem(title);
        _morePopup.AddSeparator();
        _morePopup.AddItem("恢复默认布局",10);
        _morePopup.AddItem("显示动画资产",11);
        _morePopup.AddItem("显示预览",12);
        _morePopup.AddItem("显示属性",13);
        _morePopup.AddSeparator();_morePopup.AddItem("缩放：跟随系统",20);
        for(int i=0;i<5;i++)_morePopup.AddItem($"缩放：{100+i*25}%",21+i);
        _morePopup.IdPressed+=id=>{switch(id){case 0:ShowLibraryDialog();break;case 1:PopupMenuAtPointer(_importPopup);break;case 2:_workspace.ShowPanel("export");break;case 3:_workspace.ShowPanel("jobs");break;case 4:ShowCharacters();break;case 5:_workspace.ShowPanel("bones");break;case 6:_workspace.ShowPanel("retarget");break;case 7:_commands?.Open();break;case 8:ShowHelp();break;case 9:_workspace.ShowPanel("navigation");break;case 10:_workspace.RestorePanels();break;case 11:_workspace.ShowPanel("assets");break;case 12:_workspace.ShowPanel("preview");break;case 13:_workspace.ShowPanel("inspector");break;default:_display.SetScale(id==20?0:1+(id-21)*.25);break;}};
    }
    private Control BuildJobs()
    {
        var col=new VBoxContainer();var row=new HFlowContainer();col.AddChild(row);row.AddChild(Button("刷新",RefreshJobs));row.AddChild(Button("取消所选",()=>JobAction(false)));row.AddChild(Button("重试失败项",()=>JobAction(true)));
        _jobsList=new ItemList{CustomMinimumSize=new(0,90),SizeFlagsVertical=SizeFlags.ExpandFill,TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis};col.AddChild(_jobsList);_jobsList.ItemSelected+=_=>ShowJobDetails();
        _jobDetails=new RichTextLabel{CustomMinimumSize=new(0,70),FitContent=false,SizeFlagsVertical=SizeFlags.ExpandFill,SelectionEnabled=true,BbcodeEnabled=false};col.AddChild(_jobDetails);return col;
    }
    private void SaveSoon(){if(_saveLayout!=null&&!_closing)_saveLayout.Start();}
    private void SaveWorkspace()
    {
        if(_workspace==null||_preferencesPath.Length==0)return;
        // Preserve the old settings field; the component workspace has its own persisted layout.
        _preferences.DockLayout=_workspace.Layout.Clone();
        if(GetWindow().Mode==Window.ModeEnum.Windowed){_preferences.Width=GetWindow().Size.X;_preferences.Height=GetWindow().Size.Y;_preferences.X=GetWindow().Position.X;_preferences.Y=GetWindow().Position.Y;_preferences.HasPosition=true;}
        _preferences.Maximized=GetWindow().Mode==Window.ModeEnum.Maximized;
        try{_preferences.Save(_preferencesPath);}catch(Exception ex){if(!_closing)SetStatus("布局无法保存："+ex.Message);}
    }
    private void ConnectWorkspace()
    {
        if(_api!=null)_api.Jobs.Changed+=OnWorkspaceJob;
        RefreshJobs();
        // Limbo already saves and restores the GUI focus; only gate application shortcuts here.
        GetNode("/root/LimboConsole").Connect("toggled",Callable.From<bool>(visible=>_consoleOpen=visible));
        if(_workspaceError.Length>0)SetStatus(_workspaceError);
    }
    private void OnWorkspaceJob(JobUpdate update)=>_jobUpdates.Enqueue(update);
    private void UpdateWorkspace()
    {
        _scaleHint.Text=$"{_display.EffectiveScale*100:0}%";_scaleHint.TooltipText=_display.Source;

        bool dirty=false;while(_jobUpdates.TryDequeue(out var update)){dirty=true;SetStatus($"任务 {update.Id[..8]} · {JobStateName(update.State)} · 已处理 {update.Finished}/{update.Total}");}
        if(dirty)RefreshJobs();
    }
    private static string JobStateName(JobState state)=>state switch{JobState.Queued=>"排队",JobState.Running=>"运行中",JobState.Completed=>"已完成",JobState.CompletedWithErrors=>"部分失败",JobState.Cancelled=>"已取消",JobState.Interrupted=>"已中断",_=>"失败"};
    private static string JobKindName(JobKind kind)=>kind switch{JobKind.Import=>"导入",JobKind.ImportUasset=>".uasset 导入",JobKind.ExportGlb=>"GLB 导出",JobKind.ExportRes=>"RES 导出",JobKind.ExportFbx=>"FBX 导出",_=>kind.ToString()};
    private void RefreshJobs()
    {
        if(_api==null||_jobsList==null)return;var old=_jobsList.GetSelectedItems().FirstOrDefault(-1);var selected=old>=0&&old<_jobIds.Count?_jobIds[old]:"";
        var jobs=_api.Jobs.Recent();_jobsList.Clear();_jobIds=jobs.Select(j=>j.Id).ToList();
        _header.SetJobs(jobs.Count(j=>j.State==JobState.Running),jobs.Count(j=>j.State is JobState.Running or JobState.Queued));
        foreach(var job in jobs)_jobsList.AddItem($"{JobStateName(job.State)}  {job.Finished}/{job.Total}  ·  {JobKindName(job.Kind)}  ·  {job.Id[..8]}");
        if(jobs.Length>0)_jobsList.Select(Math.Max(0,_jobIds.IndexOf(selected)));ShowJobDetails();
    }
    private void ShowJobDetails()
    {
        var selected=_jobsList.GetSelectedItems();if(selected.Length==0){_jobDetails.Text="暂无任务。导入和 FBX / GLB / RES 导出的记录会显示在这里。";return;}
        var job=_api.Jobs.Get(_jobIds[selected[0]]);_jobDetails.Text=$"成功 {job.Succeeded} · 跳过 {job.Skipped} · 失败 {job.Failed} · 取消 {job.Cancelled}\n{job.Message}\n{job.ReportPath}\n"+string.Join("\n",job.Items.Where(i=>i.Error.Length>0).Take(8).Select(i=>i.Source.Name+": "+i.Error));
        if(job.ExportOptions is {} options)_jobDetails.Text+="\n"+options.Describe();
        if(job.Kind is JobKind.Import or JobKind.ImportUasset)_jobDetails.Text+=$"\n资产：新增 {job.Items.Sum(i=>i.ImportedAssets)} · 重复 {job.Items.Sum(i=>i.DuplicateAssets)} · 不支持 {job.Items.Sum(i=>i.UnsupportedAssets)}\n"+string.Join("\n",job.Items.SelectMany(i=>i.Warnings).Take(12))+"\n"+string.Join("\n",job.Items.Where(i=>i.Output.Length>0).Select(i=>i.Output));
    }
    private void ShowImportResult(JobRecord job)
    {
        string summary=$"新增 {job.Items.Sum(i=>i.ImportedAssets)} · 重复 {job.Items.Sum(i=>i.DuplicateAssets)} · 不支持 {job.Items.Sum(i=>i.UnsupportedAssets)} · 失败任务 {job.Failed}";
        SetStatus("导入 · "+JobStateName(job.State)+" · "+summary);
        _message.Title="导入结果 · "+JobStateName(job.State);
        _messageBody.Text=summary+"\n"+job.Error+"\n"+string.Join("\n",job.Items.Where(i=>i.Error.Length>0).Take(8).Select(i=>i.Source.Name+"："+i.Error))
            +"\n"+string.Join("\n",job.Items.SelectMany(i=>i.Warnings).Take(12));
        if(job.Kind==JobKind.ImportUasset||job.Items.Any(i=>i.Source.IsUasset))
            _messageBody.Text+="\n仅将独立动画序列加入动画库；Skeleton 用于解析依赖，Montage、蓝图和材质等资源不作为动画导入。";
        if(job.Items.Sum(i=>i.ImportedAssets)==0&&job.Items.Sum(i=>i.DuplicateAssets)>0)
            _messageBody.Text+="\n这些资产已存在于库中，本次没有重复添加。";
        if(_search.Text.Length>0||_category!="All"||_attributeFilter.IsActive)
            _messageBody.Text+="\n当前搜索或筛选仍然生效；新资产可能不在当前结果中。";
        _messageBody.Text+="\n完整报告："+job.ReportPath+"\n可在任务面板查看各项详情、日志和转换报告。";
        PopupFit(_message,new(640,360));
    }
    private void JobAction(bool retry)
    {
        var selected=_jobsList.GetSelectedItems();if(selected.Length==0)return;try{if(retry)_api.Jobs.Retry(_jobIds[selected[0]]);else _api.Jobs.Cancel(_jobIds[selected[0]]);RefreshJobs();}catch(Exception ex){ShowError("任务操作失败",ex.Message);}
    }
    private void PopupFit(Window popup,Vector2I desired)
    {
        popup.ContentScaleFactor=_display.EffectiveScale;var available=GetWindow().Size;
        popup.PopupCentered(new Vector2I(Math.Min((int)(desired.X*_display.EffectiveScale),Math.Max(160,available.X-40)),Math.Min((int)(desired.Y*_display.EffectiveScale),Math.Max(100,available.Y-60))));
    }
    private async Task QuitSafely()
    {
        if(_closing)return;SaveWorkspace();_closing=true;CancelAssetBrowse();SetStatus("正在保存并停止任务…");
        _importCancellation?.Cancel();_exportCancellation?.Cancel();
        _retargetCancellation?.Cancel();await PendingRetargetPreview;
        _characterCache.Dispose();
        if(_commands!=null)await _commands.ShutdownAsync();
        await _libraryUiTask;
        GetTree().Quit();
    }
}
