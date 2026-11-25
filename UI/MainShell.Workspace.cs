using Godot;
using Animanager.UI.Shell;
using Animanager.Core.Browse;
using Animanager.UI.Workspace;
using Animanager.Core.Jobs;
using Animanager.Core;
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
    private Label _scaleHint=null!;
    private ItemList _jobsList=null!;
    private RichTextLabel _jobDetails=null!;
    private List<(WorkspaceApi Api, string Id)> _jobIds=[];
    private readonly HashSet<WorkspaceApi> _jobLibraries = [];
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
        _bonePanel = new BoneTreePanel { Preview = _preview };
        _workspace.Register("bones","骨骼",_bonePanel);
        _workspace.Register("retarget","重定向",BuildRetarget());
        _workspace.Register("export","导出",BuildExport());
        _workspace.Register("jobs","任务",BuildJobs());
        _workspace.Initialize(_preferences.DockLayout);
        _workspace.LayoutChanged+=SaveSoon;
        BuildHeaderMenus();
        BuildPluginSettings();
        _header.LibraryPressed+=ShowLibraryDialog;
        _header.ImportPressed+=()=>PopupMenuAtPointer(_importPopup);
        _header.ExportPressed+=()=>_workspace.ShowPanel("export");
        _header.JobsPressed+=()=>_workspace.ShowPanel("jobs");
        _header.MorePressed+=()=>PopupMenuAtPointer(_morePopup);
        _cancel=Button("取消扫描",()=>_importCancellation?.Cancel());_cancel.Visible=false;
        ((Container)_workspace.Status.GetChild(0)).AddChild(_cancel);
        _files=Dialog(FileDialog.FileModeEnum.OpenFiles,"导入资产（.uasset 自动识别标准骨架）");_files.Filters=["*.glb, *.gltf, *.fbx ; 3D 资产","*.motion.json ; 独立人体与道具动画","*.uasset, *.zip ; UE4.23 / 4.27 / UE5.0 / UE5.6 源动画"];_files.FilesSelected+=paths=>ImportIntoLibrary(paths);
        _uassetFolder=Dialog(FileDialog.FileModeEnum.OpenDir,"导入 .uasset · UE4 / UE5 标准骨架");_uassetFolder.DirSelected+=path=>ImportIntoLibrary([path],true);
        _folder=Dialog(FileDialog.FileModeEnum.OpenDir,"选择要扫描的资产文件夹");_folder.DirSelected+=path=>ImportIntoLibrary([path]);
        _output=Dialog(FileDialog.FileModeEnum.OpenDir,"选择导出目录");_output.DirSelected+=path=>_exportPath.Text=path;
        _message=new AcceptDialog{Title="Animanager",OkButtonText="关闭"};AddChild(_message);
        _messageBody=new RichTextLabel{CustomMinimumSize=new(160,70),SelectionEnabled=true,BbcodeEnabled=false,FitContent=false};_message.AddChild(_messageBody);
        BuildLibraryDialogs();
        BuildAssetTagDialog();
        BuildTagGroupDialog();
        BuildCharacterLibraryDialog();
        _quitDialog=new ConfirmationDialog{Title="仍有任务正在执行",DialogText="取消未完成任务并退出？已完成的资产与输出会保留。",DialogAutowrap=true,OkButtonText="取消任务并退出",CancelButtonText="继续运行"};AddChild(_quitDialog);
        _quitDialog.Confirmed+=()=>_ = QuitSafely();GetTree().AutoAcceptQuit=false;
        GetWindow().CloseRequested+=()=>{if(_closing)return;if(_busy||_exportCancellation!=null||!_pluginTask.IsCompleted||_commands?.JobRunning==true||_jobLibraries.Any(api=>api.Jobs.Busy))PopupFit(_quitDialog,new(440,160));else _ = QuitSafely();};
        _saveLayout=new Godot.Timer{OneShot=true,WaitTime=.5};AddChild(_saveLayout);_saveLayout.Timeout+=SaveWorkspace;
        _display.Changed+=()=>{_preferences.Scale=_display.RequestedScale;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);SaveSoon();};
        GetWindow().SizeChanged+=SaveSoon;DisplayScaleController.ApplyChildren(this,_display.EffectiveScale);UpdateTransport();
    }

    private Control BuildNavigation()
    {
        _sideBar=new SideBar();
        _sideBar.CategorySelected += ShowAssetCategory;
        _sideBar.CollectionSelected += ToggleCollection;
        _sideBar.OpenLibraryFolder+=OpenLibraryFolder;
        _sideBar.OpenLibrarySelected+=BrowseLibrary;
        _sideBar.CloseLibraryRequested+=RequestCloseLibrary;
        _sideBar.CreateCollection += () => ShowCollectionEditor();
        _sideBar.CollectionActionRequested += (id, action) => LibraryAction(() => RunCollectionAction(id, action));
        _sideBar.CanMoveCollection = CanMoveCollection;
        _sideBar.CollectionMoveRequested += (id, targetId, placement) => LibraryAction(() => MoveCollection(id, targetId, placement));
        _sideBar.CanDropAssetsOnCollection = CanDropAssetsOnCollection;
        _sideBar.AssetsDroppedOnCollection += (id, keys) => LibraryAction(() => DropAssetsOnCollection(id, keys));
        _sideBar.CanDropEditableAssets = keys => keys.Any(key => DragSourceLibrary(key) != null);
        _sideBar.AssetsDroppedOnFavorites += keys => LibraryAction(() => DropAssetsOnFavorites(keys));
        _sideBar.AssetsDroppedOnTag += (tag, keys) => LibraryAction(() => DropAssetsOnTag(tag, keys));
        _sideBar.DropHintChanged += _workspace.Status.SetDropHint;
        _sideBar.TagSelected+=BrowseByTag;
        _sideBar.TagGroupSelected += BrowseByTagGroup;
        _sideBar.CreateTag += ShowAddAppTags;
        _sideBar.CreateTagGroup+=()=>ShowTagGroupEditor(null);
        _sideBar.EditTagGroup+=ShowTagGroupEditor;
        return _sideBar;
    }
    private Control BuildAssets()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 0);
        var header = new VBoxContainer();
        header.AddThemeConstantOverride("separation", Tokens.Space.Xs);
        col.AddChild(header);
        var searchRow = Row(header);
        searchRow.AddThemeConstantOverride("separation", Tokens.Space.Xs);
        var searchField = new SearchField("搜索名称或标签")
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(98, Tokens.Size.Control)
        };
        _assetSearch = searchField.Input;
        _assetSearch.Name = "AssetBrowserSearch";
        _assetSearch.ClearButtonEnabled = true;
        _assetSearch.TooltipText = "搜索资产名称或标签 · Ctrl+F";
        _assetSearch.Resized += () => _assetSearch.PlaceholderText = _assetSearch.Size.X < 90 ? "搜索资产" : "搜索名称或标签";
        var searchSurface = Surfaces.Fill(Tokens.Sunken, border: Tokens.LineStrong);
        var searchFocus = Surfaces.Fill(Tokens.Sunken, border: Tokens.Accent);
        searchField.AddThemeStyleboxOverride("panel", searchSurface);
        _assetSearch.FocusEntered += () => searchField.AddThemeStyleboxOverride("panel", searchFocus);
        _assetSearch.FocusExited += () => searchField.AddThemeStyleboxOverride("panel", searchSurface);
        _assetSearch.TextChanged += text =>
        {
            if (_search.Text != text) _search.Text = text;
            _searchTimer.Start();
        };
        _assetSearch.TextSubmitted += text => { _search.Text = text; ApplyAssetSearch(); };
        searchRow.AddChild(searchField);
        var scope = BuildAssetLibraryScope();
        StyleAssetToolbarButton(scope);
        searchRow.AddChild(scope);
        var libraries = BuildOpenLibrariesMenu();
        StyleAssetToolbarButton(libraries, quiet: true);
        searchRow.AddChild(libraries);

        var toolbar = new HFlowContainer();
        toolbar.AddThemeConstantOverride("h_separation", Tokens.Space.Sm);
        toolbar.AddThemeConstantOverride("v_separation", Tokens.Space.Xs);
        header.AddChild(toolbar);
        var browseTools = Row(toolbar);
        browseTools.AddThemeConstantOverride("separation", Tokens.Space.Xs);
        _attributeToggle = Button("筛选", () => { _workspace.ShowPanel("filters"); LoadSkeletonOptions(); });
        _attributeToggle.Name = "AssetAttributeFilters";
        _attributeToggle.CustomMinimumSize = new(50, Tokens.Size.Control);
        _attributeToggle.TooltipText = "按标签、动作相似度、骨架、时长或帧率筛选";
        StyleAssetToolbarButton(_attributeToggle);
        browseTools.AddChild(_attributeToggle);
        _sortControl = new OptionButton { Name = "AssetSort", FitToLongestItem = false, CustomMinimumSize = new(88, Tokens.Size.Control) };
        foreach (var title in new[] { "导入时间", "名称", "大小", "收藏优先", "时长", "帧率", "默认顺序" })
            _sortControl.AddItem(title);
        _sortControl.ItemSelected += i => SetAssetSort(new((AssetSortField)i, i is 0 or 2 or 3 or (int)AssetSortField.LibraryMix));
        StyleAssetToolbarButton(_sortControl);
        browseTools.AddChild(_sortControl);

        _browser = new AssetBrowser { Name = "AssetBrowser" };
        browseTools.AddChild(BuildAssetViewSwitch());
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", Tokens.Space.Xs);
        toolbar.AddChild(actions);
        var organize = BuildAssetActions();
        StyleAssetToolbarButton(organize, quiet: true);
        actions.AddChild(organize);
        _locateAsset = Button("定位", () =>
        {
            if (_assetSelection.Count == 1) LibraryAction(() => LocateAssetInLibrary(_assetSelection.Single()));
        });
        _locateAsset.Name = "LocateAssetInLibrary";
        _locateAsset.TooltipText = "在来源库的完整列表中定位选中的资产";
        _locateAsset.Disabled = true;
        StyleAssetToolbarButton(_locateAsset, quiet: true);
        actions.AddChild(_locateAsset);

        _filterBar = new FilterBar();
        header.AddChild(_filterBar);
        // In short docks the filter button retains the active count and opens the full controls.
        col.Resized += () => _filterBar.SetExpanded(col.Size.Y >= 200);
        _filterBar.Cleared+=()=>ResetAssetFilters(true);
        _filterBar.Removed+=RemoveAssetFilter;
        _selectionBar=new SelectionBar();header.AddChild(_selectionBar);
        _selectionBar.ActionRequested+=id=>LibraryAction(()=>{switch(id){case "export":_workspace.ShowPanel("export");break;case "tags":AssetAction(AddAssetTagsAction);break;case "favorite":AssetAction(7);break;case "trash":AssetAction(TrashAssetsAction);break;case "restore":AssetAction(RestoreAssetsAction);break;case "purge":AssetAction(PurgeAssetsAction);break;default:FillAssetMenu();PopupMenuAtPointer(_assetActions.GetPopup());break;}});
        col.AddChild(Hairline.Horizontal());
        var results = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(results);
        results.AddChild(_browser);
        _browser.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _empty = Wrapped("导入文件以建立资产库。");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        results.AddChild(_empty);
        _empty.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _hiddenPreviewDropHint = BuildComparisonDropHint(results, "HiddenPreviewComparisonDropHint", "拖到这里，在预览中对比");
        _hiddenPreviewDropHint.SetAnchorsPreset(LayoutPreset.BottomWide);
        _hiddenPreviewDropHint.OffsetLeft = 12;
        _hiddenPreviewDropHint.OffsetRight = -12;
        _hiddenPreviewDropHint.OffsetTop = -52;
        _hiddenPreviewDropHint.OffsetBottom = -12;
        _browser.Selection.Changed += UpdateAssetSelectionCount;
        _browser.Activated+=e=>{if(e.OpenRequested) OpenAsset(e.Index);};
        _browser.ContextMenuRequested+=ShowAssetContextMenu;
        _browser.OpenSourceRequested += card => LibraryAction(() =>
        {
            if (card.ClosedLibraryId is { } libraryId) OpenCollectionSourceLibrary(libraryId, card.LocateClosedLibrary);
        });
        _browser.DeleteRequested+=()=>{if(CanEditAssetSelection())LibraryAction(()=>AssetAction(_category=="Trash"?PurgeAssetsAction:TrashAssetsAction));};
        _browser.SortRequested+=SetAssetSort;
        _browser.ViewModeChanged+=mode=>{_preferences.AssetView=mode.ToString();SaveSoon();};
        _browser.SetMode(_preferences.AssetView=="List"?AssetBrowser.ViewMode.List:AssetBrowser.ViewMode.Grid);
        _sort=new(_preferences.SortField,_preferences.SortDescending);_browser.SetSort(_sort);_sortControl.Select((int)_sort.Field);

        col.AddChild(Hairline.Horizontal());
        col.AddChild(BuildAssetResultsFooter());
        var filters=new VBoxContainer();BuildAssetBrowseControls(filters);_workspace.Register("filters","资产筛选",filters);
        return col;
    }

    private static void StyleAssetToolbarButton(Button button, bool quiet = false)
    {
        button.CustomMinimumSize = new(button.CustomMinimumSize.X, Tokens.Size.Control);
        button.AddThemeFontSizeOverride("font_size", Shell.Type.Row);
        int padding = quiet ? Tokens.Space.Xs + 2 : Tokens.Space.Sm;
        button.AddThemeStyleboxOverride("normal", Surfaces.Fill(quiet ? Colors.Transparent : Tokens.Raised,
            padX: padding, border: quiet ? null : Tokens.LineStrong));
        button.AddThemeStyleboxOverride("hover", Surfaces.Fill(Tokens.Hover, padX: padding));
        button.AddThemeStyleboxOverride("pressed", Surfaces.Fill(Tokens.Active, padX: padding));
        button.AddThemeStyleboxOverride("disabled", Surfaces.Fill(Colors.Transparent, padX: padding));
        if (quiet) button.AddThemeColorOverride("font_color", Tokens.Text2);
    }

    private Control BuildAssetViewSwitch()
    {
        var track = new PanelContainer();
        track.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Sunken, padX: 1, padY: 1, border: Tokens.LineStrong));
        var row = Row(track);
        row.AddThemeConstantOverride("separation", 0);
        var group = new ButtonGroup();
        var grid = ViewButton(Icons.Grid, "网格视图", AssetBrowser.ViewMode.Grid);
        var list = ViewButton(Icons.List, "列表视图", AssetBrowser.ViewMode.List);
        row.AddChild(grid);
        row.AddChild(list);
        void SyncMode(AssetBrowser.ViewMode mode)
        {
            bool isGrid = mode == AssetBrowser.ViewMode.Grid;
            grid.SetPressedNoSignal(isGrid);
            list.SetPressedNoSignal(!isGrid);
        }
        _browser.ViewModeChanged += SyncMode;
        SyncMode(_browser.Mode);
        return track;

        IconButton ViewButton(string icon, string tooltip, AssetBrowser.ViewMode mode)
        {
            var button = new IconButton(icon, 16, tooltip)
            {
                CustomMinimumSize = new(28, 28), ToggleMode = true, ButtonGroup = group
            };
            var active = Surfaces.Fill(Tokens.AccentWash, border: Tokens.Accent.Alpha(.4f));
            button.AddThemeStyleboxOverride("pressed", active);
            button.AddThemeStyleboxOverride("hover_pressed", active);
            button.Pressed += () => _browser.SetMode(mode);
            return button;
        }
    }

    private Control BuildAssetResultsFooter()
    {
        var footer = new HBoxContainer();
        _count = Shell.Type.CaptionLabel("0 个结果", Tokens.Text2);
        _count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _count.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        footer.AddChild(_count);
        var pagination = Row(footer);
        pagination.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        pagination.AddThemeConstantOverride("separation", Tokens.Space.Xs);
        _firstPage = PageButton("AssetPageFirst", "«", "首页", () => GoToAssetPage(0));
        _previous = PageButton("AssetPagePrevious", "‹", "上一页", () => GoToAssetPage(_page - 1));
        _pageLabel = Shell.Type.MonoLabel("1 / 1", Tokens.Text2, Shell.Type.Caption);
        _pageLabel.CustomMinimumSize = new(42, 0);
        _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        pagination.AddChild(_pageLabel);
        _next = PageButton("AssetPageNext", "›", "下一页", () => GoToAssetPage(_page + 1));
        _lastPage = PageButton("AssetPageLast", "»", "尾页", () => GoToAssetPage(Math.Max(0, (FilteredAssetCount - 1) / AssetPageSize)));
        return footer;

        Button PageButton(string name, string text, string tooltip, Action action)
        {
            var button = Button(text, action);
            button.Name = name;
            button.TooltipText = tooltip;
            button.CustomMinimumSize = new(26, 26);
            button.ThemeTypeVariation = "GhostButton";
            button.AddThemeFontSizeOverride("font_size", Shell.Type.SectionTitle);
            pagination.AddChild(button);
            return button;
        }
    }
    private void SetAssetSort(AssetSort sort)
    {
        _sort=sort;_browser.SetSort(sort);_sortControl.Select((int)sort.Field);
        _browser.Scroll.ScrollVertical = 0;
        _preferences.SortField=sort.Field;_preferences.SortDescending=sort.Descending;_preferences.AssetSortChosen=true;SaveSoon();_page=0;Refresh();
    }
    private Control BuildInspector()
    {
        _inspector=new Inspector();_title=_inspector.TitleLabel;_detail=_inspector.Summary;_tags=_inspector.TagsInput;_favorite=_inspector.FavoriteButton;
        BuildWeaponProfiles(_inspector.WeaponOverview, _previewWeaponControls);
        _inspector.FavoriteToggled+=ToggleFavorite;_inspector.TagsSaved+=SaveTags;
        _inspector.TagAddRequested += () =>
        {
            if (!_libraryLoading && _selected is { Trashed: false } asset && _selectedLibrary is { } library)
                ShowAssetTagChange([(library, [asset.Id])], asset.Name, add: true);
        };
        _inspector.TagSelected+=BrowseByTag;
        _inspector.ClipSelected+=clip=>{EndAnimationComparison();_preview.SelectClip(clip);_preview.TogglePlay();_stage.SetCurrentClip(clip);UpdateTransport();};
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
        _importPopup.IdPressed+=id=>{switch(id){case 0:_files.PopupCenteredRatio(.7f);break;case 1:_folder.PopupCenteredRatio(.7f);break;case 2:_uassetFolder.PopupCenteredRatio(.7f);break;case 3:ImportIntoLibrary([RuntimePaths.Bundled("BuiltinAssets/Baselines")], autoCharacters:false);break;}};
        _morePopup=new PopupMenu();AddChild(_morePopup);
        foreach(var title in new[]{"资产库管理…","导入…","导出…","任务…","角色库…","骨骼检查","重定向","Console · F12","使用说明","显示导航"})_morePopup.AddItem(title);
        _morePopup.AddItem("标签管理…",14);
        _morePopup.AddItem("插件…",15);
        _morePopup.AddSeparator();
        _morePopup.AddItem("恢复默认布局",10);
        _morePopup.AddItem("显示动画资产",11);
        _morePopup.AddItem("显示预览",12);
        _morePopup.AddItem("显示属性",13);
        _morePopup.AddSeparator();_morePopup.AddItem("缩放：跟随系统",20);
        for(int i=0;i<5;i++)_morePopup.AddItem($"缩放：{100+i*25}%",21+i);
        _morePopup.IdPressed+=id=>{switch(id){case 0:ShowLibraryDialog();break;case 1:PopupMenuAtPointer(_importPopup);break;case 2:_workspace.ShowPanel("export");break;case 3:_workspace.ShowPanel("jobs");break;case 4:ShowCharacters();break;case 5:_workspace.ShowPanel("bones");break;case 6:_workspace.ShowPanel("retarget");break;case 7:_commands?.Open();break;case 8:ShowHelp();break;case 9:_workspace.ShowPanel("navigation");break;case 10:_workspace.RestorePanels();break;case 11:_workspace.ShowPanel("assets");break;case 12:_workspace.ShowPanel("preview");break;case 13:_workspace.ShowPanel("inspector");break;case 14:ShowAppTagMenu();break;case 15:ShowPluginSettings();break;default:_display.SetScale(id==20?0:1+(id-21)*.25);break;}};
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
        SyncLibraryJobs();
        // Limbo already saves and restores the GUI focus; only gate application shortcuts here.
        GetNode("/root/LimboConsole").Connect("toggled",Callable.From<bool>(visible=>_consoleOpen=visible));
        if(_workspaceError.Length>0)SetStatus(_workspaceError);
    }
    private void SyncLibraryJobs()
    {
        var open = LoadedLibraries().Select(library => library.Api).ToHashSet();
        foreach (var api in _jobLibraries.Except(open).ToArray())
        {
            api.Jobs.Changed -= OnWorkspaceJob;
            _jobLibraries.Remove(api);
        }
        foreach (var api in open)
            if (_jobLibraries.Add(api)) api.Jobs.Changed += OnWorkspaceJob;
        RefreshJobs();
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
        if (_jobsList == null) return;
        int old = _jobsList.GetSelectedItems().FirstOrDefault(-1);
        var selected = old >= 0 && old < _jobIds.Count ? _jobIds[old] : default;
        var libraries = LoadedLibraries();
        var jobs = libraries.SelectMany(library => library.Api.Jobs.Recent().Select(job => (Library: library, Job: job)))
            .OrderByDescending(entry => entry.Job.CreatedUtc).ToArray();
        _jobsList.Clear();
        _jobIds = jobs.Select(entry => (entry.Library.Api, entry.Job.Id)).ToList();
        _header.SetJobs(jobs.Count(entry => entry.Job.State == JobState.Running),
            jobs.Count(entry => entry.Job.State is JobState.Running or JobState.Queued));
        foreach (var (library, job) in jobs)
        {
            string source = libraries.Length > 1 ? $"  ·  {library.Identity.Name}" : "";
            _jobsList.AddItem($"{JobStateName(job.State)}  {job.Finished}/{job.Total}  ·  {JobKindName(job.Kind)}{source}  ·  {job.Id[..8]}");
        }
        if (jobs.Length > 0) _jobsList.Select(Math.Max(0, _jobIds.IndexOf(selected)));
        ShowJobDetails();
    }
    private void ShowJobDetails()
    {
        var selected=_jobsList.GetSelectedItems();if(selected.Length==0){_jobDetails.Text="暂无任务。导入和 FBX / GLB / RES 导出的记录会显示在这里。";return;}
        var (api, id) = _jobIds[selected[0]];
        var job=api.Jobs.Get(id);_jobDetails.Text=$"成功 {job.Succeeded} · 跳过 {job.Skipped} · 失败 {job.Failed} · 取消 {job.Cancelled}\n{job.Message}\n{job.ReportPath}\n"+string.Join("\n",job.Items.Where(i=>i.Error.Length>0).Take(8).Select(i=>i.Source.Name+": "+i.Error));
        if(job.ExportOptions is {} options)_jobDetails.Text+="\n"+options.Describe();
        if(job.Kind is JobKind.Import or JobKind.ImportUasset)_jobDetails.Text+="\n资产 "+ImportSummary(job)+"\n"+string.Join("\n",job.Items.SelectMany(i=>i.Warnings).Take(12))+"\n"+string.Join("\n",job.Items.Where(i=>i.Output.Length>0).Select(i=>i.Output));
    }
    private static string ImportSummary(JobRecord job)
    {
        var summary=$"新增 {job.Items.Sum(i=>i.ImportedAssets)} · 重复 {job.Items.Sum(i=>i.DuplicateAssets)} · 不支持 {job.Items.Sum(i=>i.UnsupportedAssets)}";
        var inPlace=job.Items.Sum(i=>i.ExcludedInPlaceAssets);
        var withoutAnimation=job.Items.Sum(i=>i.ExcludedWithoutAnimationAssets);
        if(inPlace>0)summary+=$" · 跳过 InPlace {inPlace}";
        if(withoutAnimation>0)summary+=$" · 跳过无可用动画 {withoutAnimation}";
        return summary;
    }
    private void ShowImportResult(JobRecord job)
    {
        string summary=ImportSummary(job)+$" · 失败任务 {job.Failed}";
        SetStatus("导入 · "+JobStateName(job.State)+" · "+summary);
        _message.Title="导入结果 · "+JobStateName(job.State);
        _messageBody.Text=summary+"\n"+job.Error+"\n"+string.Join("\n",job.Items.Where(i=>i.Error.Length>0).Take(8).Select(i=>i.Source.Name+"："+i.Error))
            +"\n"+string.Join("\n",job.Items.SelectMany(i=>i.Warnings).Take(12));
        if(job.Kind==JobKind.ImportUasset||job.Items.Any(i=>i.Source.IsUasset))
            _messageBody.Text+="\n仅将独立动画序列加入动画库；Skeleton 用于解析依赖，Montage、蓝图和材质等资源不作为动画导入。";
        if(job.Items.Sum(i=>i.ImportedAssets)==0&&job.Items.Sum(i=>i.DuplicateAssets)>0)
            _messageBody.Text+="\n这些资产已存在于库中，本次没有重复添加。";
        if(_search.Text.Length>0||_category!="All"||_attributeFilter.IsActive||_selectedTags.Count>0)
            _messageBody.Text+="\n当前搜索或筛选仍然生效；新资产可能不在当前结果中。";
        _messageBody.Text+="\n完整报告："+job.ReportPath+"\n可在任务面板查看各项详情、日志和转换报告。";
        PopupFit(_message,new(640,360));
    }
    private void JobAction(bool retry)
    {
        var selected=_jobsList.GetSelectedItems();if(selected.Length==0)return;
        var (api, id) = _jobIds[selected[0]];
        try{if(retry)api.Jobs.Retry(id);else api.Jobs.Cancel(id);RefreshJobs();}catch(Exception ex){ShowError("任务操作失败",ex.Message);}
    }
    private void PopupFit(Window popup,Vector2I desired,bool keepFittedMinimum=false)
    {
        var scale = _display.EffectiveScale;
        var available = GetWindow().Size;
        var size = new Vector2I(Math.Min((int)(desired.X * scale), Math.Max(160, available.X - 40)),
            Math.Min((int)(desired.Y * scale), Math.Max(100, available.Y - 60)));
        popup.ContentScaleFactor = scale;
        if (keepFittedMinimum) popup.MinSize = size;
        popup.PopupCentered(size);
    }
    private async Task QuitSafely()
    {
        if(_closing)return;SaveWorkspace();_closing=true;CancelAssetBrowse();SetStatus("正在保存并停止任务…");
        _importCancellation?.Cancel();_exportCancellation?.Cancel();_pluginCancellation?.Cancel();
        _retargetCancellation?.Cancel();StopRequest(ref _retargetSourceRequest);StopRequest(ref _comparisonRequest);
        _motionAudition?.Dismiss();
        await Task.WhenAll(CancelPreviewLoads(), PendingRetargetPreview, PendingAnimationComparison, PendingMotionAuditions,
            PendingBrowse, _pendingRetargetSource, _pluginTask, StopLibraryPreviewWarmupAsync());
        _characterCache.Dispose();
        _previewTargets.Dispose();
        await Task.WhenAll(_jobLibraries.Select(api => api.Jobs.ShutdownAsync()));
        if(_commands!=null)await _commands.ShutdownAsync();
        await _libraryUiTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        GetTree().Quit();
    }
}
