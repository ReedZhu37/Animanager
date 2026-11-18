using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Models;
using Animanager.UI.Components;
using Godot;
using Animanager.UI.Shell;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private const int AssetPageSize = 60;
    private AssetAttributeFilter _attributeFilter = new();
    private Button _attributeToggle = null!;
    private VBoxContainer _attributePanel = null!;
    private OptionButton _skeletonFilter = null!;
    private LineEdit _durationFilter = null!, _fpsFilter = null!;
    private Label _attributeHint = null!;
    private AssetAttributes? _inspectorAttributes;
    private string _inspectorStamp="";
    private AssetSkeleton[] _skeletonChoices = [];
    private (string Catalog, int Cache)? _skeletonRevision;
    private CancellationTokenSource? _skeletonRequest, _browseRequest, _attributeDetailsRequest;
    private string? _attributeDetailsId;
    private int _attributeDetailsCacheRevision;
    private AssetBrowseRow[] _browseRows = [];
    private (string Query, string Category, AssetAttributeFilter Filter) _publishedBrowseScope = ("", "All", new());
    private int FilteredAssetCount { get; set; }
    private int _browseGeneration;
    private bool _thumbnailsEnabled, _thumbnailPending;
    private ulong _nextThumbnailTick;
    private readonly HashSet<int> _thumbnailAttempts = [];
    private AssetThumbnailRenderer? _thumbnailRenderer;
    private Task _pendingBrowse = Task.CompletedTask;
    internal Task PendingBrowse => _pendingBrowse;
    private void ApplyAssetSearch()
    {
        _searchTimer.Stop(); _assetSearch.Text=_search.Text; _page=0; _browser.Scroll.ScrollVertical=0; Refresh();
    }
    private void GoToAssetPage(int page)
    {
        _page=Math.Clamp(page,0,Math.Max(0,(FilteredAssetCount-1)/AssetPageSize));
        _browser.Scroll.ScrollVertical=0; Refresh();
    }

    private void BuildAssetBrowseControls(VBoxContainer column)
    {
        _thumbnailsEnabled = DisplayServer.GetName() != "headless";
        _attributePanel = column;
        _skeletonFilter = new OptionButton { Name = "AssetSkeletonFilter", FitToLongestItem = false, CustomMinimumSize = new(0, 32) };
        _attributePanel.AddChild(_skeletonFilter);
        SetSkeletonOptions([]);

        var numbers = new HFlowContainer();
        _attributePanel.AddChild(numbers);
        _durationFilter = AddNumberFilter(numbers, "时长（秒）", "AssetDurationFilter", "例如 1..3", "按资产中最长片段的时长筛选。" + NumberFilterHelp);
        _fpsFilter = AddNumberFilter(numbers, "源帧率", "AssetFpsFilter", "例如 60", "按文件记录的源帧率筛选。" + NumberFilterHelp);
        var actions = new HFlowContainer();
        _attributePanel.AddChild(actions);
        var apply = Button("应用筛选", ApplyAssetAttributeFilter);
        apply.Name = "AssetApplyFilters";
        actions.AddChild(apply);
        var clear = Button("清除条件", () => ResetAssetAttributeFilter(refresh: true));
        clear.Name = "AssetClearFilters";
        actions.AddChild(clear);
        _attributeHint = Wrapped("");
        _attributeHint.Visible = false;
        _attributePanel.AddChild(_attributeHint);
    }

    private const string NumberFilterHelp = "留空为不限。输入数字、1..3、1..、..3 或“未知”。";

    private LineEdit AddNumberFilter(Container parent, string label, string name, string placeholder, string hint)
    {
        var field = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(102, 0) };
        parent.AddChild(field);
        field.AddChild(Label(label, true, 12));
        var input = Input(placeholder);
        input.Name = name;
        input.TooltipText = hint;
        field.AddChild(input);
        input.TextSubmitted += _ => ApplyAssetAttributeFilter();
        return input;
    }

    private void ApplyAssetAttributeFilter()
    {
        var index = _skeletonFilter.Selected;
        var skeleton = index >= 2 ? _skeletonChoices[index - 2].Id : index == 1 ? "unknown" : "";
        try
        {
            var next = AssetAttributeFilter.Parse(skeleton, _durationFilter.Text, _fpsFilter.Text);
            SetAttributeHint("");
            if (_attributeFilter == next) return;
            _attributeFilter = next;
            _attributeToggle.Text = next.IsActive ? "属性筛选 · 已启用" : "属性筛选";
            _page = 0;
            SyncFilterChips();
            Refresh();
        }
        catch (ArgumentException error) { SetAttributeHint(error.Message); }
    }

    private void ResetAssetAttributeFilter(bool refresh)
    {
        var changed = _attributeFilter.IsActive;
        _attributeFilter = new();
        if (_attributeToggle == null) return;
        _attributeToggle.Text = "属性筛选";
        _durationFilter.Text = _fpsFilter.Text = "";
        _skeletonFilter.Select(0);
        SetAttributeHint("");
        SyncFilterChips();
        if (changed && refresh) { _page = 0; Refresh(); }
    }

    private void SetAttributeHint(string text)
    {
        _attributeHint.Text = text;
        _attributeHint.Visible = text.Length != 0;
    }

    private void SetSkeletonOptions(AssetSkeleton[] skeletons)
    {
        var selected = _skeletonFilter.Selected;
        var prior = selected >= 2 && selected - 2 < _skeletonChoices.Length ? _skeletonChoices[selected - 2] : null;
        if (prior != null && skeletons.All(s => s.Id != prior.Id)) skeletons = [.. skeletons, prior];
        _skeletonChoices = skeletons;
        _skeletonFilter.Clear();
        _skeletonFilter.AddItem("全部骨架");
        _skeletonFilter.AddItem("骨架未知");
        foreach (var skeleton in skeletons) _skeletonFilter.AddItem(skeleton.Name);
        var index = prior == null ? -1 : Array.FindIndex(skeletons, s => s.Id == prior.Id);
        _skeletonFilter.Select(index >= 0 ? index + 2 : selected == 1 ? 1 : 0);
    }

    private void LoadSkeletonOptions()
    {
        if (_api == null || _closing) return;
        var revision = (_database.CatalogRevision, _api.BrowseCacheRevision);
        if (_skeletonRevision == revision) return;
        StopRequest(ref _skeletonRequest);
        _skeletonRevision = revision;
        var request = new CancellationTokenSource();
        _skeletonRequest = request;
        SetAttributeHint("正在读取骨架选项…");
        _ = LoadSkeletonOptionsAsync(_api, request);
    }

    private async Task LoadSkeletonOptionsAsync(WorkspaceApi api, CancellationTokenSource request)
    {
        try
        {
            var skeletons = await api.GetAssetSkeletonsAsync(request.Token);
            if (!_closing && ReferenceEquals(_skeletonRequest, request))
            {
                SetSkeletonOptions(skeletons);
                if (_attributeHint.Text == "正在读取骨架选项…") SetAttributeHint("");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals(_skeletonRequest, request))
            {
                _skeletonRevision = null;
                SetAttributeHint("无法读取骨架选项 · " + ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_skeletonRequest, request)) _skeletonRequest = null;
            request.Dispose();
        }
    }

    private void SyncFilterChips()
    {
        if(_filterBar==null)return;
        var chips=new List<(string Key,string Label)>();
        if(_skeletonFilter.Selected>0)chips.Add(("skeleton",_skeletonFilter.GetItemText(_skeletonFilter.Selected)));
        if(_attributeFilter.Duration?.IsActive==true)chips.Add(("duration","时长 "+_durationFilter.Text));
        if(_attributeFilter.SourceFps?.IsActive==true)chips.Add(("fps","帧率 "+_fpsFilter.Text));
        _filterBar.SetFilters(chips);if(_assetSelection.Count>1)_filterBar.Hide();
    }
    private void RefreshAssetBrowse()
    {
        if (_database == null || _closing || _browser==null) return;
        StopRequest(ref _browseRequest); ResetThumbnailRows(); RefreshCollections(); RefreshCurrentAsset();
        if (_assetSelection.Count > 0) _assetSelection.Prune(_database.ExistingIds(_assetSelection, _category == "Trash"));
        _sideBar.SetActive(_category);
        foreach(var category in new[]{"All","Animation","Model","Favorites","Trash"})_sideBar.SetCount(category,_database.Search(category:category).Total);
        _header.SetLibrary(_libraries.Active!.Identity.Name,_database.Search().Total);
        if (_workspace.ActiveDrawer=="filters") LoadSkeletonOptions();
        FilteredAssetCount=0;_items=[];_browseRows=[];_browser.SetRows([]);
        _count.Text="正在读取…";_firstPage.Disabled=_lastPage.Disabled=_previous.Disabled=_next.Disabled=true;_empty.Visible=false;
        SyncAssetSelection();
        var request=new CancellationTokenSource();_browseRequest=request;
        _pendingBrowse = RefreshAssetBrowseAsync(_api,_search.Text,_category,_attributeFilter,_sort,_page,request);
    }

    private async Task RefreshAssetBrowseAsync(WorkspaceApi api, string query, string category, AssetAttributeFilter filter,
        AssetSort sort, int page, CancellationTokenSource request)
    {
        try
        {
            var result = await api.BrowseAssetsAsync(query, category, filter, sort, page, AssetPageSize, request.Token);
            if (result.Items.Length == 0 && page > 0)
                result = await api.BrowseAssetsAsync(query, category, filter, sort, pageSize: AssetPageSize, cancellationToken: request.Token);
            if (!_closing && ReferenceEquals(_browseRequest, request) && _api==api && query == _search.Text)
                ShowAssetBrowsePage(result, query, category, filter);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals(_browseRequest, request))
            {
                _count.Text = "筛选失败";
                _empty.Text = ex.Message;
                _empty.Visible = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_browseRequest, request)) _browseRequest = null;
            request.Dispose();
        }
    }

    private void ShowAssetBrowsePage(AssetBrowsePage result, string query, string category, AssetAttributeFilter filter)
    {
        _page = result.Page;
        _publishedBrowseScope = (query, category, filter);
        FilteredAssetCount = result.Total;
        _browseRows = result.Items;
        _items = result.Items.Select(row => row.Asset).ToList();
        _browser.SetRows(_browseRows.Select(row=>CardModel(row.Asset,row.Attributes)));
        _count.Text = $"{result.Total} 个结果";
        _pageLabel.Text = $"{_page + 1} / {Math.Max(1, ((long)result.Total + AssetPageSize - 1) / AssetPageSize)}";
        _firstPage.Disabled = _previous.Disabled = _page == 0;
        _lastPage.Disabled = _next.Disabled = ((long)_page + 1) * AssetPageSize >= result.Total;
        _empty.Visible = result.Total == 0;
        _empty.Text = _attributeFilter.IsActive ? "没有符合条件的资产\n调整属性筛选或搜索词。" :
            query.Length > 0 ? "没有匹配的资产\n试试其他名称或标签。" : "这里还没有资产\n导入文件，或载入内置示例。";
        SyncAssetSelection();
        RefreshCurrentAsset();
    }

    private static AssetCardModel CardModel(AssetItem item,AssetAttributes? attributes) => new()
    {
        Id=item.Id,Name=item.Name,MetaLine=item.SourceFormat.TrimStart('.').ToUpperInvariant()+" · "+FormatSize(item.Size),
        DetailLine=attributes==null?"读取属性…":$"{attributes.Duration?.ToString("0.##")??"—"}s · {attributes.SourceFps?.ToString("0.#")??"—"}fps · {attributes.Skeletons.FirstOrDefault()?.Name??"未知"}",
        IsAnimation=item.Type=="Animation",Favorite=item.Favorite,ThumbnailPending=true,
        Tooltip=item.Name+"\n"+item.VirtualPath+(attributes==null?"":"\n"+DescribeAttributes(attributes)),Tags=item.Tags,VirtualPath=item.VirtualPath,
        ImportedAt=item.ImportedAt,Size=item.Size,DurationSeconds=attributes?.Duration,SourceFps=attributes?.SourceFps,
        SkeletonCount=attributes?.Skeletons.Length??0,SkeletonName=attributes?.Skeletons.FirstOrDefault()?.Name??"",
        DurationText=attributes?.Duration is {} d?$"{d:0.##}s":"—",FpsText=attributes?.SourceFps is {} f?$"{f:0.#}":"—",
        SkeletonText=attributes?.Skeletons.FirstOrDefault()?.Name??"未知",SizeText=FormatSize(item.Size),
        ImportedText=DateTime.TryParse(item.ImportedAt,out var date)?date.ToLocalTime().ToString("MM-dd HH:mm"):"—"
    };

    private static string DescribeAttributes(AssetAttributes attributes)
    {
        var skeletons = attributes.Skeletons.Length == 0 ? "未知" : string.Join("、", attributes.Skeletons.Select(s => s.Name));
        var duration = attributes.Duration is { } seconds ? $"{seconds:0.###} 秒" : "未知";
        var fps = attributes.SourceFps is { } rate ? $"{rate:0.###} FPS" : "未知";
        return $"骨架  {skeletons}\n最长片段  {duration} · 源帧率  {fps}";
    }

    private Task<List<AssetItem>> GetAssetsForCurrentFilterAsync(CancellationToken cancellationToken = default)
    {
        var (query, category, filter) = _publishedBrowseScope;
        return _api.GetMatchingAssetsAsync(query, category, filter, cancellationToken);
    }

    private void ProcessAssetBrowse()
    {
        if(_closing||_api==null||_browser==null)return;
        ProcessAttributeDetails();
        if(!_thumbnailsEnabled||_thumbnailPending||_browseRequest!=null||!_browser.IsVisibleInTree()||_api.LibraryBusy||Time.GetTicksMsec()<_nextThumbnailTick)return;
        foreach(int index in _browser.VisibleIndices())
        {
            if(!_thumbnailAttempts.Add(index))continue;
            _thumbnailPending=true;_ = LoadAssetThumbnailAsync(_api,_items[index],index,_browseGeneration);return;
        }
    }
    private void ProcessAttributeDetails()
    {
        var revision=_api.BrowseCacheRevision;
        if(_attributeDetailsId==_selected?.Id&&_attributeDetailsCacheRevision==revision)return;
        StopRequest(ref _attributeDetailsRequest);_attributeDetailsId=_selected?.Id;_attributeDetailsCacheRevision=revision;
        _inspectorAttributes=null;_inspectorStamp="";
        if(_selected==null){_inspector.ShowEmpty();return;}
        if(_browseRows.FirstOrDefault(r=>r.Asset.Id==_selected.Id)?.Attributes is {} ready){_inspectorAttributes=ready;SyncInspector();return;}
        var request=new CancellationTokenSource();_attributeDetailsRequest=request;_ = LoadAttributeDetailsAsync(_api,_selected,request);
    }
    private async Task LoadAttributeDetailsAsync(WorkspaceApi api,AssetItem asset,CancellationTokenSource request)
    {
        try
        {
            var attributes=await api.GetAssetAttributesAsync(asset,request.Token);
            if(!_closing&&_api==api&&ReferenceEquals(_attributeDetailsRequest,request)&&asset.Id==_selected?.Id)
            {_inspectorAttributes=attributes;_inspectorStamp="";SyncInspector();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&_api==api)SetStatus("属性读取失败 · "+ex.Message);}
        finally{if(ReferenceEquals(_attributeDetailsRequest,request))_attributeDetailsRequest=null;request.Dispose();}
    }
    private void SyncInspector()
    {
        if(_inspector==null)return;
        if(_selected==null){_inspector.ShowEmpty();_inspectorStamp="";return;}
        var stamp=$"{_selected.Id}|{_selected.Name}|{_selected.Tags}|{_selected.Favorite}|{_preview.BoneCount}|{_preview.MeshCount}|{_preview.CurrentClip}|{_preview.Clips.Length}";
        if(stamp==_inspectorStamp)return;_inspectorStamp=stamp;
        var attributes=_browseRows.FirstOrDefault(r=>r.Asset.Id==_selected.Id)?.Attributes??_inspectorAttributes;
        _inspector.ShowAsset(CardModel(_selected,attributes),attributes,_preview.BoneCount,_preview.MeshCount,_selected.SourceFormat,
            _preview.Clips.Select(clip=>(clip,_preview.Player is {} p&&p.HasAnimation(clip)?(double?)p.GetAnimation(clip).Length:null)),_preview.CurrentClip);
    }

    private async Task LoadAssetThumbnailAsync(WorkspaceApi api, AssetItem asset, int index, int generation)
    {
        bool Current() => !_closing && _api == api && _browseGeneration == generation && index < _items.Count && _items[index].Id == asset.Id;
        try
        {
            var prepared = await Task.Run(() =>
            {
                var request = api.PrepareAssetThumbnail(asset);
                return (Request: request, Bytes: api.ReadAssetThumbnail(request));
            });
            if (!Current()) return;
            using var cached = new Image();
            if (prepared.Bytes != null && cached.LoadPngFromBuffer(prepared.Bytes) == Error.Ok)
                _browser.UpdateRow(index,card=>{card.Thumbnail=ImageTexture.CreateFromImage(cached);card.ThumbnailPending=false;});
            else
            {
                if (_api.LibraryBusy || !_browser.IsVisibleInTree())
                {
                    _thumbnailAttempts.Remove(index);
                    return;
                }
                if (_thumbnailRenderer == null)
                {
                    _thumbnailRenderer = new AssetThumbnailRenderer();
                    AddChild(_thumbnailRenderer);
                }
                using var image = await _thumbnailRenderer.RenderAsync(prepared.Request.SourcePath, Current);
                if (!Current()) return;
                if (image == null || image.IsEmpty()) throw new IOException("没有可绘制的模型或姿势。");
                _browser.UpdateRow(index,card=>{card.Thumbnail=ImageTexture.CreateFromImage(image);card.ThumbnailPending=false;});
                var bytes = image.SavePngToBuffer();
                await Task.Run(() => api.SaveAssetThumbnail(prepared.Request, bytes));
            }
            if (Current() && _browseRows[index].Attributes == null)
            {
                var attributes = await api.GetAssetAttributesAsync(asset);
                if (Current()) _browser.UpdateRow(index,card=>card.Tooltip=asset.Name+"\n"+DescribeAttributes(attributes));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if(Current())_browser.UpdateRow(index,card=>card.ThumbnailPending=false);
            if (Current()) GD.PushWarning($"资产缩略图不可用 · {asset.Name} · {ex.Message}");
        }
        finally
        {
            _thumbnailPending = false;
            if (!_closing) _nextThumbnailTick = Time.GetTicksMsec() + 32;
        }
    }

    private void ResetThumbnailRows()
    {
        _browseGeneration++;
        _thumbnailAttempts.Clear();
    }

    private static void StopRequest(ref CancellationTokenSource? request)
    {
        var previous = request;
        request = null;
        previous?.Cancel();
    }

    private void CancelAssetBrowse(bool resetFilters = false)
    {
        StopRequest(ref _browseRequest);
        StopRequest(ref _skeletonRequest);
        StopRequest(ref _attributeDetailsRequest);
        _attributeDetailsId = null;
        _skeletonRevision = null;
        ResetThumbnailRows();
        if (!resetFilters || _attributePanel == null) return;
        ResetAssetAttributeFilter(refresh: false);
        SetSkeletonOptions([]);
        if(_workspace.ActiveDrawer=="filters")_workspace.CloseDrawer();
        _inspectorAttributes=null;_inspectorStamp="";
    }
}
