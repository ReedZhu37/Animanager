using Godot;
using Animanager.UI.Workspace;
using Animanager.Core.Jobs;
using Animanager.UI.Components;
using Animanager.UI.Panels;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    public DockWorkspace Workspace=>_workspace;
    public DisplayScaleController DisplayScale=>_display;
    private DockWorkspace _workspace=null!;
    private DisplayScaleController _display=null!;
    private WorkspacePreferences _preferences=new();
    private string _preferencesPath="",_workspaceError="";
    private Godot.Timer _saveLayout=null!;
    private Label _scaleHint=null!,_modeHint=null!,_previewName=null!;
    private ItemList _jobsList=null!;
    private RichTextLabel _jobDetails=null!;
    private List<string> _jobIds=[];
    private readonly System.Collections.Concurrent.ConcurrentQueue<JobUpdate> _jobUpdates=new();
    private MenuButton _viewMenu=null!,_layoutMenu=null!,_scaleMenu=null!;
    private ConfirmationDialog _saveLayoutDialog=null!;
    private ConfirmationDialog _quitDialog=null!;
    private RichTextLabel _messageBody=null!;
    private LineEdit _layoutName=null!;

    private bool _consoleOpen;
    private string _previewAssetId="";
    private Label _exportSummary=null!;

    private void InitializeDisplay()
    {
        var legacyPath=_dataRoot.Length==0?null:Path.Combine(_dataRoot,"workspace.json");
        _preferences=WorkspacePreferences.LoadForApp(_preferencesPath,legacyPath,out _workspaceError);
        _display=new DisplayScaleController();AddChild(_display);_display.SetScale(_preferences.Scale);
        Theme=StudioTheme.Create(_preferences.Comfortable);GetWindow().Title="Animanager";
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
        var background=new ColorRect{Color=StudioTheme.Background,MouseFilter=MouseFilterEnum.Ignore};AddChild(background);background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margins=new MarginContainer();AddChild(margins);margins.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach(var side in new[]{"left","right","top","bottom"})margins.AddThemeConstantOverride("margin_"+side,8);
        var root=Column(margins);root.AddThemeConstantOverride("separation",6);
        var toolbar=new HFlowContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};root.AddChild(toolbar);
        var brand=Label("Animanager",false,16);brand.CustomMinimumSize=new(120,32);toolbar.AddChild(brand);
        var libraryButton=Button("Library",ShowLibraryDialog);libraryButton.Name="LibraryButton";toolbar.AddChild(libraryButton);
        var fileMenu=new MenuButton{Text="文件",FocusMode=FocusModeEnum.All};toolbar.AddChild(fileMenu);
        var filePopup=fileMenu.GetPopup();filePopup.AddItem("导入文件…",0);filePopup.AddItem("导入文件夹…",1);filePopup.AddItem("导入基准资产",2);filePopup.AddSeparator();filePopup.AddItem("打开资产库目录",3);filePopup.AddItem("使用说明",4);
        filePopup.AddItem("导入 .uasset 文件夹 · UE4 / UE5…",5);
        filePopup.IdPressed+=id=>{switch(id){case 0:_files.PopupCenteredRatio(.7f);break;case 1:_folder.PopupCenteredRatio(.7f);break;case 2:_ = ImportBaselines();break;case 3:OpenLibraryFolder();break;case 4:ShowHelp();break;case 5:_uassetFolder.PopupCenteredRatio(.7f);break;}};
        _viewMenu=new MenuButton{Text="面板",FocusMode=FocusModeEnum.All};toolbar.AddChild(_viewMenu);_viewMenu.AboutToPopup+=FillViewMenu;
        _viewMenu.GetPopup().IdPressed+=id=>_workspace.ShowPanel(WorkspaceLayout.PanelIds[(int)id]);
        _layoutMenu=new MenuButton{Text="工作区",FocusMode=FocusModeEnum.All};toolbar.AddChild(_layoutMenu);_layoutMenu.AboutToPopup+=FillLayoutMenu;_layoutMenu.GetPopup().IdPressed+=LayoutMenuAction;
        toolbar.AddChild(Button("导入",()=>_files.PopupCenteredRatio(.7f)));
        toolbar.AddChild(Button("导出所选",()=>_workspace.ShowPanel("export"),true));
        toolbar.AddChild(Button("任务",()=>_workspace.ShowPanel("jobs")));
        toolbar.AddChild(Button("Console · F12",()=>_commands?.Open()));
        _scaleMenu=new MenuButton{Text="显示",TooltipText="界面缩放与密度",FocusMode=FocusModeEnum.All};toolbar.AddChild(_scaleMenu);_scaleMenu.AboutToPopup+=FillScaleMenu;
        _scaleMenu.GetPopup().IdPressed+=id=>{if(id==10){_preferences.Comfortable=!_preferences.Comfortable;Theme=StudioTheme.Create(_preferences.Comfortable);SaveSoon();}else _display.SetScale(id==0?0:1+(id-1)*.25);};
        _workspace=new DockWorkspace{Name="Workspace",SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};root.AddChild(_workspace);
        _workspace.Register("navigation","导航",BuildNavigation());_workspace.Register("assets","资产",BuildAssets());
        _workspace.Register("preview","预览",BuildPreview());_workspace.Register("inspector","属性",BuildInspector());
        _workspace.Register("export","导出",BuildExport());_workspace.Register("jobs","任务",BuildJobs());
        _workspace.Register("bones","骨骼",new BoneTreePanel{Preview=_preview});
        _workspace.LayoutChanged+=SaveSoon;_workspace.Notice+=SetStatus;_workspace.Initialize(_preferences.Layout);
        var footer=Row(root);_status=Label("正在初始化…",true,12);_status.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;_status.SizeFlagsHorizontal=SizeFlags.ExpandFill;footer.AddChild(_status);
        _cancel=Button("取消扫描",()=>_importCancellation?.Cancel());_cancel.Visible=false;footer.AddChild(_cancel);
        _modeHint=Label("",true,12);footer.AddChild(_modeHint);_scaleHint=Label("",true,12);footer.AddChild(_scaleHint);
        _files=Dialog(FileDialog.FileModeEnum.OpenFiles,"导入资产（.uasset 自动识别标准骨架）");_files.Filters=["*.glb, *.gltf, *.fbx ; 3D 资产","*.motion.json ; 独立人体与道具动画","*.uasset, *.zip ; UE4.23 / UE5.0 / UE5.6 源动画"];_files.FilesSelected+=paths=>_ = Import(paths);
        _uassetFolder=Dialog(FileDialog.FileModeEnum.OpenDir,"导入 .uasset · UE4 / UE5 标准骨架");_uassetFolder.DirSelected+=path=>_ = Import([path],true);
        _folder=Dialog(FileDialog.FileModeEnum.OpenDir,"选择要扫描的资产文件夹");_folder.DirSelected+=path=>_ = Import([path]);
        _output=Dialog(FileDialog.FileModeEnum.OpenDir,"选择导出目录");_output.DirSelected+=path=>_exportPath.Text=path;
        _message=new AcceptDialog{Title="Animanager",OkButtonText="关闭"};AddChild(_message);
        _messageBody=new RichTextLabel{CustomMinimumSize=new(160,70),SelectionEnabled=true,BbcodeEnabled=false,FitContent=false};_message.AddChild(_messageBody);
        BuildLibraryDialogs();
        _saveLayoutDialog=new ConfirmationDialog{Title="保存工作区布局",OkButtonText="保存",CancelButtonText="取消"};AddChild(_saveLayoutDialog);
        var layoutForm=Column(_saveLayoutDialog);layoutForm.AddChild(Label("布局名称"));
        _layoutName=Input("例如：我的双栏布局");_layoutName.MaxLength=60;layoutForm.AddChild(_layoutName);
        _saveLayoutDialog.Confirmed+=()=>{var name=_layoutName.Text.Trim();if(name.Length==0)return;_preferences.SavedLayouts[name]=_workspace.Layout.Clone();SaveSoon();SetStatus("已保存布局 · "+name);};
        _quitDialog=new ConfirmationDialog{Title="仍有任务正在执行",DialogText="取消未完成任务并退出？已完成的资产与输出会保留。",DialogAutowrap=true,OkButtonText="取消任务并退出",CancelButtonText="继续运行"};AddChild(_quitDialog);
        _quitDialog.Confirmed+=()=>_ = QuitSafely();GetTree().AutoAcceptQuit=false;
        GetWindow().CloseRequested+=()=>{if(_closing)return;if(_busy||_commands?.JobRunning==true)PopupFit(_quitDialog,new(440,160));else _ = QuitSafely();};
        _saveLayout=new Godot.Timer{OneShot=true,WaitTime=.5};AddChild(_saveLayout);_saveLayout.Timeout+=SaveWorkspace;
        _display.Changed+=()=>{_preferences.Scale=_display.RequestedScale;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);SaveSoon();};
        GetWindow().SizeChanged+=SaveSoon;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);UpdateTransport();
    }
    private Control BuildNavigation()
    {
        var col=new VBoxContainer();col.AddChild(Label("资产库",true,12));_libraryCount=Wrapped("0 个资产");col.AddChild(_libraryCount);
        foreach(var (key,title) in new[]{("All","全部资产"),("Animation","动画片段"),("Model","模型与骨架"),("Favorites","我的收藏"),("Trash","回收站")})
        {
            var button=Button(title,()=>ShowAssetCategory(key));button.ToggleMode=true;button.Alignment=HorizontalAlignment.Left;col.AddChild(button);_categories[key]=button;
        }
        col.AddChild(new HSeparator());
        _collectionFilter=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(90,32)};col.AddChild(_collectionFilter);
        _collectionFilter.ItemSelected+=index=>{if(index>0&&index<=_collections.Length)ShowAssetCategory(_collections[index-1].Id);};
        var space=new Control{SizeFlagsVertical=SizeFlags.ExpandFill};col.AddChild(space);
        col.AddChild(Button("打开库目录",OpenLibraryFolder));return col;
    }
    private Control BuildAssets()
    {
        var col=new VBoxContainer();_search=Input("搜索名称或标签…  Ctrl+F");_search.Name="AssetSearch";col.AddChild(_search);
        _searchTimer=new Godot.Timer{OneShot=true,WaitTime=.2};AddChild(_searchTimer);_search.TextChanged+=_=>_searchTimer.Start();
        _searchTimer.Timeout+=()=>{if(DisplayScaleController.IsComposing){_searchTimer.Start();return;}_page=0;Refresh();};
        var header=new HFlowContainer();col.AddChild(header);_count=Label("0 个结果",true,12);header.AddChild(_count);
        BuildAssetActions(header);
        _list=new ItemList{Name="AssetRows",SelectMode=ItemList.SelectModeEnum.Multi,SizeFlagsVertical=SizeFlags.ExpandFill,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(0,80),MaxColumns=1,AllowReselect=true,TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis};col.AddChild(_list);
        _list.GuiInput+=HandleAssetInput;_list.MultiSelected+=(_,_)=>QueueNativeAssetSelection();
        _empty=Wrapped("导入文件以建立资产库。");col.AddChild(_empty);
        var pagination=Row(col);_previous=Button("‹",()=>{_page--;Refresh();});_previous.TooltipText="上一页";pagination.AddChild(_previous);
        _pageLabel=Label("1 / 1",true,12);_pageLabel.SizeFlagsHorizontal=SizeFlags.ExpandFill;_pageLabel.HorizontalAlignment=HorizontalAlignment.Center;pagination.AddChild(_pageLabel);
        _next=Button("›",()=>{_page++;Refresh();});_next.Name="AssetPageNext";_next.TooltipText="下一页";pagination.AddChild(_next);return col;
    }
    private Control BuildPreview()
    {
        var col=new VBoxContainer();var header=Row(col);_previewName=Label("选择一个资产",true,12);_previewName.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;_previewName.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(_previewName);
        header.AddChild(Button("适配",()=>_preview.FrameModel()));
        header.AddChild(Button("骨骼",()=>_workspace.ShowPanel("bones")));
        _preview=new Preview3DViewport();col.AddChild(_preview);
        var transport=new HFlowContainer();col.AddChild(transport);_play=Button("播放",TogglePlayback);transport.AddChild(_play);
        transport.AddChild(Button("− 帧",()=>{_preview.Seek(Math.Max(0,_preview.Playhead-_preview.FrameStep));UpdateTransport();}));transport.AddChild(Button("＋ 帧",()=>{_preview.Seek(Math.Min(_preview.Duration,_preview.Playhead+_preview.FrameStep));UpdateTransport();}));
        _speed=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(70,32)};foreach(var speed in new[]{"0.5×","1×","1.5×","2×"})_speed.AddItem(speed);_speed.Select(1);transport.AddChild(_speed);_speed.ItemSelected+=i=>_preview.SetSpeed(new[]{.5,1,1.5,2}[(int)i]);
        _clips=new OptionButton{FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(100,32)};col.AddChild(_clips);_clips.ItemSelected+=i=>{_preview.SelectClip(_clips.GetItemText((int)i));UpdateTransport();};
        var timeline=Row(col);_timeline=new HSlider{MinValue=0,MaxValue=1,Step=.001,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(40,24)};timeline.AddChild(_timeline);_timeline.ValueChanged+=v=>{_preview.Seek(v);UpdateTransport();};
        _time=Label("0.00 / 0.00 s",true,12);timeline.AddChild(_time);
        var options=new HFlowContainer();col.AddChild(options);var loop=new CheckButton{Text="循环",ButtonPressed=true};options.AddChild(loop);loop.Toggled+=v=>_preview.SetLoop(v);
        options.AddChild(Button("缩小",()=>_preview.Zoom(1.14f)));options.AddChild(Button("放大",()=>_preview.Zoom(.88f)));
        return col;
    }
    private Control BuildInspector()
    {
        var col=new VBoxContainer();_title=Wrapped("选择一个资产",false);col.AddChild(_title);_detail=Wrapped("格式、骨架与动画信息显示在这里。");col.AddChild(_detail);
        _favorite=Button("收藏",ToggleFavorite);col.AddChild(_favorite);col.AddChild(Label("标签",true,12));_tags=Input("例如：行走 战斗 Humanoid");col.AddChild(_tags);_tags.TextSubmitted+=_=>SaveTags();col.AddChild(Button("保存标签",SaveTags));return col;
    }
    private Control BuildExport()
    {
        var col=new VBoxContainer();_exportSummary=Wrapped("导出当前选中的资产。");col.AddChild(_exportSummary);
        col.AddChild(Label("输出格式",true,12));_exportType=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(140,32)};
        _exportType.AddItem("Godot 动画库 · .res");_exportType.AddItem("通用场景 · .glb");_exportType.AddItem("FBX · Blender");_exportType.Select(2);col.AddChild(_exportType);
        col.AddChild(Label("目标目录",true,12));_exportPath=Input("绝对路径");_exportPath.Text=ProjectSettings.GlobalizePath("user://Exports");col.AddChild(_exportPath);
        var row=Row(col);row.AddChild(Button("浏览…",()=>_output.PopupCenteredRatio(.7f)));_export=Button("导出所选",()=>Export(_exportPath.Text),true);row.AddChild(_export);
        col.AddChild(Wrapped("同名文件自动编号。FBX 导出进入任务队列；片段与源骨架保持不变。"));
        col.AddChild(Button("打开最近输出目录",()=>{if(_lastExport.Length>0)OS.ShellOpen(Path.GetDirectoryName(_lastExport)!);else SetStatus("还没有导出结果。");}));return col;
    }
    private Control BuildJobs()
    {
        var col=new VBoxContainer();var row=new HFlowContainer();col.AddChild(row);row.AddChild(Button("刷新",RefreshJobs));row.AddChild(Button("取消所选",()=>JobAction(false)));row.AddChild(Button("重试失败项",()=>JobAction(true)));
        _jobsList=new ItemList{CustomMinimumSize=new(0,90),SizeFlagsVertical=SizeFlags.ExpandFill,TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis};col.AddChild(_jobsList);_jobsList.ItemSelected+=_=>ShowJobDetails();
        _jobDetails=new RichTextLabel{CustomMinimumSize=new(0,70),FitContent=false,SizeFlagsVertical=SizeFlags.ExpandFill,SelectionEnabled=true,BbcodeEnabled=false};col.AddChild(_jobDetails);return col;
    }
    private void FillViewMenu()
    {
        var menu=_viewMenu.GetPopup();menu.Clear();for(int i=0;i<WorkspaceLayout.PanelIds.Length;i++){var id=WorkspaceLayout.PanelIds[i];menu.AddCheckItem(_workspace.Title(id),i);menu.SetItemChecked(i,_workspace.Layout.GroupFor(id)!=null);}
    }
    private string[] _savedNames=[];
    private void FillLayoutMenu()
    {
        var menu=_layoutMenu.GetPopup();menu.Clear();menu.AddItem("浏览布局",0);menu.AddItem("预览布局",1);menu.AddItem("批处理布局",2);menu.AddItem("动画检查",5);menu.AddSeparator();menu.AddItem("保存当前布局…",3);menu.AddItem("恢复默认布局",4);
        _savedNames=_preferences.SavedLayouts.Keys.Order().ToArray();if(_savedNames.Length>0)menu.AddSeparator();for(int i=0;i<_savedNames.Length;i++)menu.AddItem(_savedNames[i],100+i);
    }
    private void LayoutMenuAction(long id)
    {
        if(id>=100){_workspace.Restore(_preferences.SavedLayouts[_savedNames[(int)id-100]]);return;}
        if(id==3){_layoutName.Text="";PopupFit(_saveLayoutDialog,new(420,140));_layoutName.GrabFocus();return;}
        _workspace.UsePreset(id==5?"animation":id==1?"preview":id==2?"batch":"browse");
    }
    private void FillScaleMenu()
    {
        var menu=_scaleMenu.GetPopup();menu.Clear();menu.AddRadioCheckItem("跟随系统",0);menu.SetItemChecked(0,_display.RequestedScale==0);
        for(int i=0;i<5;i++){menu.AddRadioCheckItem($"{100+i*25}%",i+1);menu.SetItemChecked(i+1,Math.Abs(_display.RequestedScale-(1+i*.25))<.01);}
        menu.AddSeparator();menu.AddCheckItem("宽松控件间距",10);menu.SetItemChecked(menu.GetItemIndex(10),_preferences.Comfortable);
    }
    private void SaveSoon(){if(_saveLayout!=null&&!_closing)_saveLayout.Start();}
    private void SaveWorkspace()
    {
        if(_workspace==null||_preferencesPath.Length==0)return;
        _preferences.Layout=_workspace.Layout;
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
        _modeHint.Text=_workspace.Compact?"标签布局":"可拖拽工作区";
        bool dirty=false;while(_jobUpdates.TryDequeue(out var update)){dirty=true;SetStatus($"任务 {update.Id[..8]} · {JobStateName(update.State)} · 已处理 {update.Finished}/{update.Total}");}
        if(dirty)RefreshJobs();
    }
    private static string JobStateName(JobState state)=>state switch{JobState.Queued=>"排队",JobState.Running=>"运行中",JobState.Completed=>"已完成",JobState.CompletedWithErrors=>"部分失败",JobState.Cancelled=>"已取消",JobState.Interrupted=>"已中断",_=>"失败"};
    private void RefreshJobs()
    {
        if(_api==null||_jobsList==null)return;var old=_jobsList.GetSelectedItems().FirstOrDefault(-1);var selected=old>=0&&old<_jobIds.Count?_jobIds[old]:"";
        var jobs=_api.Jobs.Recent();_jobsList.Clear();_jobIds=jobs.Select(j=>j.Id).ToList();
        foreach(var job in jobs)_jobsList.AddItem($"{JobStateName(job.State)}  {job.Finished}/{job.Total}  ·  {(job.Kind==JobKind.Import?"导入":job.Kind==JobKind.ImportUasset?".uasset 导入":"FBX 导出")}  ·  {job.Id[..8]}");
        if(jobs.Length>0)_jobsList.Select(Math.Max(0,_jobIds.IndexOf(selected)));ShowJobDetails();
    }
    private void ShowJobDetails()
    {
        var selected=_jobsList.GetSelectedItems();if(selected.Length==0){_jobDetails.Text="暂无任务。导入和 FBX 导出的记录会显示在这里。";return;}
        var job=_api.Jobs.Get(_jobIds[selected[0]]);_jobDetails.Text=$"成功 {job.Succeeded} · 跳过 {job.Skipped} · 失败 {job.Failed} · 取消 {job.Cancelled}\n{job.Message}\n{job.ReportPath}\n"+string.Join("\n",job.Items.Where(i=>i.Error.Length>0).Take(8).Select(i=>i.Source.Name+": "+i.Error));
        if(job.Kind==JobKind.ImportUasset)_jobDetails.Text+=$"\n动画：新增 {job.Items.Sum(i=>i.ImportedAssets)} · 重复 {job.Items.Sum(i=>i.DuplicateAssets)} · 不支持 {job.Items.Sum(i=>i.UnsupportedAssets)}\n骨架：自动识别 UE4 / UE5，依赖与替代信息见报告；仅源姿势，不含重定向 / Montage / 曲线 / 事件\n"+string.Join("\n",job.Items.Where(i=>i.Output.Length>0).Select(i=>i.Output));
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
        if(_closing)return;SaveWorkspace();_closing=true;SetStatus("正在保存并停止任务…");
        _importCancellation?.Cancel();_exportCancellation?.Cancel();
        if(_commands!=null)await _commands.ShutdownAsync();
        await _libraryUiTask;
        GetTree().Quit();
    }
}
