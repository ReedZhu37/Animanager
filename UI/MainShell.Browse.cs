using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Models;
using Animanager.Core.Storage;
using Animanager.UI.Components;
using Godot;
using Animanager.UI.Shell;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private const int AssetPageSize = 60;
    private OptionButton _assetLibraryScope = null!;
    private Button _locateAsset = null!;
    private string? _assetLibraryFilter;
    private (string Id, string Name, int Count)[] _assetLibraryChoices = [];
    private bool CategoryIncludesMountedLibraries => _category is "All" or "Animation" or "Model" or "Favorites" or "Trash";
    private AssetAttributeFilter _attributeFilter = new();
    private Button _attributeToggle = null!;
    private VBoxContainer _attributePanel = null!;
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private OptionButton _tagMatchMode = null!;
    private MenuButton _tagPicker = null!;
    private OptionButton _skeletonFilter = null!;
    private LineEdit _durationFilter = null!, _fpsFilter = null!;
    private Label _attributeHint = null!;
    private AssetAttributes? _inspectorAttributes;
    private string _inspectorStamp="";
    private AssetSkeleton[] _skeletonChoices = [];
    private (string Catalog, int Cache, int Libraries)? _skeletonRevision;
    private CancellationTokenSource? _skeletonRequest, _browseRequest, _attributeDetailsRequest;
    private string? _attributeDetailsId;
    private int _attributeDetailsCacheRevision;
    private LibraryAssetRow[] _browseRows = [];
    private (LibraryBrowseScope[] Scopes, string Query, AssetAttributeFilter Filter, AssetTagFilter? Tags)
        _publishedBrowseScope = ([], "", new(), null);
    private (string Category, string? Library, int LibraryRevision, AssetSort Sort, int Page) _publishedBrowseView;
    private ClosedCollectionAsset[] _publishedClosedRows = [];
    private int FilteredAssetCount { get; set; }
    private int _onlineFilteredAssetCount;
    private int _browseGeneration;
    private bool _thumbnailsEnabled, _thumbnailPending;
    private ulong _nextThumbnailTick, _thumbnailRenderAfter;
    private readonly HashSet<int> _thumbnailAttempts = [];
    private const int ThumbnailMemoryLimit = 240;
    private readonly record struct ThumbnailKey(LibrarySession Library, string Path, int Revision);
    private readonly LinkedList<(ThumbnailKey Key, Texture2D Texture)> _recentThumbnails = new();
    private readonly Dictionary<ThumbnailKey, LinkedListNode<(ThumbnailKey Key, Texture2D Texture)>> _thumbnailTextures = [];
    private readonly Dictionary<LibrarySession, LibraryThumbnailWarmup> _thumbnailWarmups = [];
    private sealed class LibraryThumbnailWarmup(Task<AssetItem[]> assets)
    {
        internal Task<AssetItem[]> Assets { get; } = assets;
        internal int Next;
    }
    private AssetThumbnailRenderer? _thumbnailRenderer;
    private Task _pendingBrowse = Task.CompletedTask;
    private LibrarySession[] _durationProgressLibraries = [];
    private ulong _nextDurationProgress;
    internal Task PendingBrowse => _pendingBrowse;
    private void ApplyAssetSearch()
    {
        _searchTimer.Stop(); _assetSearch.Text=_search.Text; _page=0; _browser.Scroll.ScrollVertical=0; Refresh();
    }
    private void GoToAssetPage(int page)
    {
        if (_browseRequest != null) return;
        int destination = Math.Clamp(page, 0, Math.Max(0, (FilteredAssetCount - 1) / AssetPageSize));
        if (destination == _page) return;
        var (scopes, query, filter, tags) = _publishedBrowseScope;
        if (query != _search.Text) { ApplyAssetSearch(); return; }
        ResetThumbnailRows();
        _thumbnailRenderAfter = Time.GetTicksMsec() + 120;
        RequestAssetBrowse(scopes, query, filter, tags, destination, closedRows: _publishedClosedRows, reuseMotionRanking: true);
    }

    private OptionButton BuildAssetLibraryScope()
    {
        _assetLibraryScope = new OptionButton
        {
            Name = "AssetLibraryScope", Visible = false, FitToLongestItem = false, AllowReselect = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new(114, Tokens.Size.Control)
        };
        _assetLibraryScope.ItemSelected += index =>
        {
            if (index == 0) { ShowAllBrowseLibraries(); return; }
            BrowseLibrary(_assetLibraryChoices[(int)index - 1].Id);
        };
        return _assetLibraryScope;
    }

    private void BrowseLibrary(string id)
    {
        if (_libraries.FindOpenLibrary(id) == null) return;
        if (!CategoryIncludesMountedLibraries)
        {
            _collectionBrowseReturn = null;
            SetAssetCategory("All", 0);
        }
        _assetLibraryFilter = id;
        _page = 0;
        _browser.Scroll.ScrollVertical = 0;
        Refresh();
        _workspace.ShowPanel("assets");
    }

    private void ShowAllBrowseLibraries()
    {
        _assetLibraryFilter = null;
        _browser.Scroll.ScrollVertical = 0;
        SetAssetSort(new(AssetSortField.LibraryMix));
    }

    private void LocateAssetInLibrary(string key)
    {
        if (_libraryLoading || SelectionLibrary(key) is not { } library ||
            library.Api.Database.Find(SelectionAssetId(key)) is not { } asset) return;
        _searchTimer.Stop();
        _search.Text = _assetSearch.Text = "";
        ResetAssetFilters(refresh: false);
        _collectionBrowseReturn = null;
        _category = asset.Trashed ? "Trash" : "All";
        _assetLibraryFilter = library.Identity.Id;
        _page = 0;
        _browser.Scroll.ScrollVertical = 0;
        _assetSelection.Select(key);
        _workspace.ShowPanel("assets");
        RefreshAssetBrowse(key);
    }

    private void LocatePreviewAsset(string key)
    {
        if (_closing || _libraryLoading) return;
        if (!_searchTimer.IsStopped())
        {
            _searchTimer.Stop();
            _page = 0;
        }
        RefreshAssetBrowse(key);
    }

    private void SyncAssetLibraryScope((string Id, string Name, int Count)[] choices)
    {
        int selected = choices.Length > 1 ? Array.FindIndex(choices, choice => choice.Id == _assetLibraryFilter) : -1;
        if (selected < 0) _assetLibraryFilter = null;
        _assetLibraryScope.Visible = CategoryIncludesMountedLibraries && choices.Length > 1;
        if (!_assetLibraryScope.Visible) return;
        if (!_assetLibraryChoices.SequenceEqual(choices))
        {
            _assetLibraryChoices = choices;
            _assetLibraryScope.Clear();
            _assetLibraryScope.AddItem($"全部库 · {choices.Length}");
            for (int i = 0; i < choices.Length; i++)
            {
                var choice = choices[i];
                _assetLibraryScope.AddItem(Typography.Fit(Typography.Regular, $"{choice.Name} · {choice.Count:N0}", Shell.Type.Body, 270));
                _assetLibraryScope.SetItemTooltip(i + 1, $"{choice.Name} · {choice.Count:N0} 个资产");
            }
        }
        _assetLibraryScope.Select(selected + 1);
        _assetLibraryScope.TooltipText = selected < 0 ? "按来源库筛选。选择全部库可交错查看各库资产。" : choices[selected].Name;
    }

    private void BuildAssetBrowseControls(VBoxContainer column)
    {
        _thumbnailsEnabled = DisplayServer.GetName() != "headless";
        _attributePanel = column;
        BuildTagFilterControls(column);
        BuildMotionFilter(column);
        column.AddChild(new HSeparator());
        column.AddChild(Label("资产属性", false, 13));
        _skeletonFilter = new OptionButton { Name = "AssetSkeletonFilter", FitToLongestItem = false, CustomMinimumSize = new(0, 32) };
        _attributePanel.AddChild(_skeletonFilter);
        SetSkeletonOptions([]);

        var numbers = new HFlowContainer();
        _attributePanel.AddChild(numbers);
        _durationFilter = AddNumberFilter(numbers, "时长（秒）", "AssetDurationFilter", "例如 1..3", "按资产中最长片段的时长筛选。" + NumberFilterHelp);
        _fpsFilter = AddNumberFilter(numbers, "源帧率", "AssetFpsFilter", "例如 60", "按文件记录的源帧率筛选。" + NumberFilterHelp);
        var actions = new HFlowContainer();
        _attributePanel.AddChild(actions);
        var apply = Button("应用属性筛选", ApplyAssetAttributeFilter);
        apply.Name = "AssetApplyFilters";
        actions.AddChild(apply);
        var clear = Button("清除筛选", () => ResetAssetFilters(refresh: true));
        clear.Name = "AssetClearFilters";
        actions.AddChild(clear);
        _attributeHint = Wrapped("");
        _attributeHint.Visible = false;
        _attributePanel.AddChild(_attributeHint);
    }

    private void BuildTagFilterControls(VBoxContainer column)
    {
        column.AddChild(Label("标签", false, 13));
        _tagMatchMode = new OptionButton { Name = "AssetTagMatchMode", CustomMinimumSize = new(0, 32) };
        _tagMatchMode.AddItem("交集 · 包含全部所选标签");
        _tagMatchMode.AddItem("并集 · 包含任一所选标签");
        _tagMatchMode.Select(0);
        _tagMatchMode.ItemSelected += _ => { if (_selectedTags.Count > 0) RefreshAfterFilterChange(); };
        column.AddChild(_tagMatchMode);

        var actions = Row(column);
        _tagPicker = new MenuButton
        {
            Name = "AssetTagPicker", Text = "选择标签 ▾", CustomMinimumSize = new(0, 32),
            Flat = false, Disabled = true, FocusMode = FocusModeEnum.All, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var popup = _tagPicker.GetPopup();
        popup.HideOnCheckableItemSelection = false;
        popup.AllowSearch = true;
        _tagPicker.AboutToPopup += () =>
        {
            popup.Clear();
            var all = _tagSections.FirstOrDefault(section => section.GroupId == AppTagCatalog.AllGroupId).Tags ?? [];
            if (all.Length > 0) popup.AddSeparator(AppTagCatalog.AllGroupName);
            foreach (var (name, count) in all) AddTag(name, $"{name} · {count:N0}");
            var visible = all.Select(tag => tag.Name);
            var missing = _selectedTags.Except(visible, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0 && _tagGroups.Length > 0) popup.AddSeparator("其他已选标签");
            foreach (var name in missing)
                AddTag(name, name);

            void AddTag(string name, string label)
            {
                int index = popup.ItemCount;
                popup.AddCheckItem(Typography.Fit(Typography.Regular, label, Shell.Type.Body, 270));
                popup.SetItemMetadata(index, name);
                popup.SetItemTooltip(index, label);
                popup.SetItemChecked(index, _selectedTags.Contains(name));
            }
        };
        popup.IndexPressed += index =>
        {
            var name = popup.GetItemMetadata((int)index).AsString();
            ToggleAssetTag(name);
            popup.SetItemChecked((int)index, _selectedTags.Contains(name));
        };
        actions.AddChild(_tagPicker);
        actions.AddChild(BuildTagGroupMenu());
    }

    private void ToggleAssetTag(string tag)
    {
        if (!_selectedTags.Add(tag)) _selectedTags.Remove(tag);
        RefreshAfterFilterChange();
    }

    private void BrowseByTag(string tag)
    {
        if (!_workspace.IsPanelVisible("assets")) _workspace.ShowPanel("assets");
        ToggleAssetTag(tag);
    }

    private void RefreshAfterFilterChange()
    {
        _page = 0;
        _browser.Scroll.ScrollVertical = 0;
        Refresh();
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
            RefreshAfterFilterChange();
        }
        catch (ArgumentException error) { SetAttributeHint(error.Message); }
    }

    private void ResetAssetFilters(bool refresh)
    {
        var changed = _attributeFilter.IsActive || _selectedTags.Count > 0 || _motionReference != null;
        ClearMotionFilter();
        _customMotionWindow = false;
        _motionWindow.SetValueNoSignal(1);
        _motionMinimum.SetValueNoSignal(0);
        _attributeFilter = new();
        _selectedTags.Clear();
        _tagMatchMode.Select(0);
        _durationFilter.Text = _fpsFilter.Text = "";
        _skeletonFilter.Select(0);
        SetAttributeHint("");
        if (changed && refresh) RefreshAfterFilterChange();
        else SyncFilterChips();
    }

    private void RemoveAssetFilter(string key)
    {
        switch (key)
        {
            case "motion":
                ClearMotionFilter();
                break;
            case "tags":
                _selectedTags.Clear();
                break;
            case "skeleton":
                _skeletonFilter.Select(0);
                _attributeFilter = _attributeFilter with { SkeletonId = null, UnknownSkeleton = false };
                break;
            case "duration":
                _durationFilter.Text = "";
                _attributeFilter = _attributeFilter with { Duration = null };
                break;
            case "fps":
                _fpsFilter.Text = "";
                _attributeFilter = _attributeFilter with { SourceFps = null };
                break;
        }
        RefreshAfterFilterChange();
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
        var libraries = LoadedLibraries();
        var revision = (string.Join("/", libraries.Select(library => library.Api.Database.CatalogRevision)),
            libraries.Sum(library => library.Api.BrowseCacheRevision), _browseLibraryRevision);
        if (_skeletonRevision == revision) return;
        StopRequest(ref _skeletonRequest);
        _skeletonRevision = revision;
        var request = new CancellationTokenSource();
        _skeletonRequest = request;
        SetAttributeHint("正在读取骨架选项…");
        _ = LoadSkeletonOptionsAsync(libraries, request);
    }

    private async Task LoadSkeletonOptionsAsync(LibrarySession[] libraries, CancellationTokenSource request)
    {
        try
        {
            var sources = await Task.WhenAll(libraries.Select(library => library.Api.GetAssetSkeletonsAsync(request.Token)));
            var skeletons = sources.SelectMany(source => source).DistinctBy(skeleton => skeleton.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(skeleton => skeleton.Name, StringComparer.OrdinalIgnoreCase).ThenBy(skeleton => skeleton.Id, StringComparer.Ordinal).ToArray();
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
        if (_filterBar == null) return;
        int tagCount = _selectedTags.Count;
        _sideBar.SetSelectedTags(_selectedTags);
        _tagPicker.Disabled = tagCount == 0 && !_tagSections.Any(section => section.Tags.Length > 0);
        _tagPicker.Text = tagCount == 0 ? "选择标签 ▾" : $"已选 {tagCount} 个标签 ▾";
        var chips = new List<(string Key, string Label)>();
        if (_motionReference is { } motion)
            chips.Add(("motion", (motion.Start == motion.End ? "姿势 · " : "片段 · ") + motion.Asset.Name));
        if (tagCount > 0)
        {
            string mode = tagCount == 1 ? "标签" : _tagMatchMode.Selected == 0 ? "标签交集" : "标签并集";
            string names = string.Join("、", _selectedTags.Order(StringComparer.OrdinalIgnoreCase));
            chips.Add(("tags", $"{mode} · {names}"));
        }
        if (_attributeFilter.UnknownSkeleton) chips.Add(("skeleton", "骨架未知"));
        else if (_attributeFilter.SkeletonId is { } id)
            chips.Add(("skeleton", _skeletonChoices.FirstOrDefault(s => s.Id == id)?.Name ?? id));
        if (_attributeFilter.Duration?.IsActive == true) chips.Add(("duration", "时长 " + DescribeNumberFilter(_attributeFilter.Duration)));
        if (_attributeFilter.SourceFps?.IsActive == true) chips.Add(("fps", "帧率 " + DescribeNumberFilter(_attributeFilter.SourceFps)));
        _attributeToggle.Text = chips.Count > 0 ? $"筛选 {chips.Count}" : "筛选";
        _attributeToggle.AddThemeColorOverride("font_color", chips.Count > 0 ? Tokens.Accent : Tokens.Text);
        _filterBar.SetFilters(chips);
    }

    private static string DescribeNumberFilter(AssetNumberFilter filter) => filter.UnknownOnly ? "未知" :
        filter.Minimum == filter.Maximum ? $"{filter.Minimum}" : $"{filter.Minimum}..{filter.Maximum}";

    private AssetTagFilter? CurrentAssetTagFilter() =>
        _selectedTags.Count == 0 ? null : new AssetTagFilter(_selectedTags, _tagMatchMode.Selected == 0);

    private void RefreshAssetBrowse(string? locateKey = null)
    {
        if (_closing || _browser==null) return;
        StopRequest(ref _browseRequest); ResetThumbnailRows(); RefreshCollections(); RefreshCurrentAsset();
        RefreshComparisonAssets();
        if (_assetSelection.Count > 0)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in _assetSelection.GroupBy(SelectionLibrary))
            {
                if (group.Key is not { } library) continue;
                var ids = library.Api.Database.ExistingIds(group.Select(SelectionAssetId), _category == "Trash");
                live.UnionWith(ids.Select(id => AssetKey(library, id)));
            }
            _assetSelection.Prune(live);
        }
        _sideBar.SetActive(_category);
        var libraries = LoadedLibraries();
        SyncOpenLibraries(libraries);
        SyncLibraryThumbnailWarmups(libraries);
        var counts = new[] { "All", "Animation", "Model", "Favorites", "Trash" }.ToDictionary(category => category, _ => 0);
        var libraryChoices = new (string Id, string Name, int Count)[libraries.Length];
        for (int i = 0; i < libraries.Length; i++)
        {
            var library = libraries[i];
            var libraryCounts = library.Api.Database.BrowseCategoryCounts();
            libraryChoices[i] = (library.Identity.Id, library.Identity.Name, libraryCounts["All"]);
            foreach (var (category, count) in libraryCounts)
                counts[category] += count;
        }
        SyncAssetLibraryScope(libraryChoices);
        foreach (var (category, count) in counts) _sideBar.SetCount(category, count);
        var header = libraries.Length switch
        {
            0 => "打开资产库",
            1 => libraries[0].Identity.Name,
            _ => $"全部库 · {libraries.Length}"
        };
        _header.SetLibrary(header, counts["All"]);
        if (_workspace.ActiveDrawer=="filters") LoadSkeletonOptions();
        var collection = CurrentCollection;
        var scoped = ScopedLibrary;
        LibrarySession[] categoryLibraries = scoped != null ? [scoped] : libraries;
        var scopes = collection != null ? CollectionBrowseScopes(collection, libraries) :
            categoryLibraries.Select(library => new LibraryBrowseScope(library, _category)).ToArray();
        _durationProgressLibraries = _sort.Field == AssetSortField.Duration || _attributeFilter.Duration?.IsActive == true
            ? scopes.Select(scope => scope.Library).ToArray() : [];
        _nextDurationProgress = 0;
        _sortControl.SetItemText((int)AssetSortField.LibraryMix, scopes.Length > 1 ? "按库混合" : "默认顺序");
        _sortControl.Disabled = _motionReference != null;
        _browser.List.SetSort(_motionReference != null ? "motion" : _sort.Key, _sort.Descending);
        _sortControl.Text = _motionReference == null ? _sortControl.GetItemText((int)_sort.Field) : "按相似度";
        _sortControl.TooltipText = scopes.Length > 1 && _sort.Field == AssetSortField.LibraryMix ? "各来源库交错显示" :
            _sort.Descending ? "降序；点击列表表头可切换方向" : "升序；点击列表表头可切换方向";
        if (_motionReference != null) _sortControl.TooltipText = "当前按动作相似度从高到低排列";
        var tags = CurrentAssetTagFilter();
        bool sameView = _publishedBrowseView == (_category, _assetLibraryFilter, _browseLibraryRevision, _sort, _page) &&
            _publishedBrowseScope.Query == _search.Text && _publishedBrowseScope.Filter == _attributeFilter &&
            AssetTagFilter.Equivalent(_publishedBrowseScope.Tags, tags);
        if (!sameView || _motionReference != null || _publishedMotionReference != _motionReference)
        {
            FilteredAssetCount = _onlineFilteredAssetCount = 0;
            _browseRows = [];
            _browser.SetRows([]);
        }
        _empty.Visible = false;
        SyncAssetSelection();
        RequestAssetBrowse(scopes, _search.Text, _attributeFilter, tags, _page, collection, locateKey);
    }

    private void RequestAssetBrowse(LibraryBrowseScope[] scopes, string query, AssetAttributeFilter filter,
        AssetTagFilter? tags, int page, AppCollection? collection = null, string? locateKey = null,
        ClosedCollectionAsset[]? closedRows = null, bool reuseMotionRanking = false)
    {
        _count.Text = "正在读取…";
        _firstPage.Disabled = _lastPage.Disabled = _previous.Disabled = _next.Disabled = true;
        var request = new CancellationTokenSource();
        _browseRequest = request;
        _pendingBrowse = RefreshAssetBrowseAsync(scopes, query, filter, tags, _sort, page, collection, locateKey, closedRows, reuseMotionRanking, request);
    }

    private LibrarySession[] LoadedLibraries() => _libraries.OpenLibraries();

    private MotionReference? _publishedMotionReference;

    private async Task RefreshAssetBrowseAsync(LibraryBrowseScope[] scopes, string query, AssetAttributeFilter filter,
        AssetTagFilter? tags, AssetSort sort, int page, AppCollection? collection, string? locateKey,
        ClosedCollectionAsset[]? closedRows, bool reuseMotionRanking, CancellationTokenSource request)
    {
        try
        {
            var token = request.Token;
            LibraryAssetPage result;
            ClosedCollectionAsset[] unavailable;
            if (_motionReference is { } reference)
            {
                if (!reuseMotionRanking)
                {
                    var ranked = await RankMotionRows(reference, scopes, query, filter, tags, token);
                    token.ThrowIfCancellationRequested();
                    if (_closing || !ReferenceEquals(request, _browseRequest) || query != _search.Text) return;
                    _motionRows = ranked.Rows;
                    _motionSkipped = ranked.Skipped;
                }
                int index = locateKey == null ? -1 : Array.FindIndex(_motionRows, m => AssetKey(m.Row.Library, m.Row.Asset.Id) == locateKey);
                page = index >= 0 ? index / AssetPageSize : Math.Clamp(page, 0, Math.Max(0, (_motionRows.Length - 1) / AssetPageSize));
                result = await ReadMotionPage(_motionRows, page, token);
                unavailable = [];
            }
            else
            {
                if (locateKey != null && SelectionLibrary(locateKey) is { } library)
                {
                    int index = await WorkspaceApi.FindAssetBrowseIndexAsync(scopes, library, SelectionAssetId(locateKey),
                        query, filter, sort, tags, token);
                    if (index >= 0) page = index / AssetPageSize;
                }
                var closed = closedRows != null || collection == null ? Task.FromResult(closedRows ?? []) :
                    Task.Run(() => ReadClosedCollectionAssets(collection, scopes, query, filter, sort, tags, token), token);
                var online = WorkspaceApi.BrowseLibrariesAsync(scopes, query, filter, sort, page, AssetPageSize, token, tags);
                await Task.WhenAll(closed, online);
                result = await online;
                unavailable = await closed;
                int last = Math.Max(0, (result.Total + unavailable.Length - 1) / AssetPageSize);
                if (page > last)
                {
                    page = last;
                    result = await WorkspaceApi.BrowseLibrariesAsync(scopes, query, filter, sort, page, AssetPageSize, token, tags);
                }
            }
            if (!_closing && ReferenceEquals(_browseRequest, request) && query == _search.Text)
            {
                if (_publishedBrowseView.Page != page) _browser.Scroll.ScrollVertical = 0;
                ShowAssetBrowsePage(result, unavailable, page, scopes, query, filter, tags);
                if (locateKey != null)
                {
                    int generation = _browseGeneration;
                    int index = _browser.IndexOf(locateKey);
                    if (_browser.Row(index) is { } row)
                    {
                        if (!_workspace.IsPanelVisible("assets")) _workspace.ShowPanel("assets");
                        // The replacement rows must finish layout before the scrollbar can reach them.
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        if (!_closing && generation == _browseGeneration && ReferenceEquals(_browseRequest, request) && query == _search.Text)
                        {
                            _assetSelection.Select(locateKey);
                            _browser.ScrollToIndex(index);
                            SetStatus("已定位 · " + row.Name);
                        }
                    }
                    else SetStatus("当前筛选结果中没有这个资产");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals(_browseRequest, request))
            {
                _count.Text = "筛选失败";
                if (_motionReference != null)
                {
                    _motionRows = [];
                    _browseRows = [];
                    _browser.SetRows([]);
                    FilteredAssetCount = _onlineFilteredAssetCount = 0;
                    SyncAssetSelection();
                }
                _empty.Text = ex.Message;
                _empty.Visible = true;
                _firstPage.Disabled = _previous.Disabled = _page == 0;
                _lastPage.Disabled = _next.Disabled = ((long)_page + 1) * AssetPageSize >= FilteredAssetCount;
            }
        }
        finally
        {
            if (ReferenceEquals(_browseRequest, request)) _browseRequest = null;
            request.Dispose();
        }
    }

    private void ShowAssetBrowsePage(LibraryAssetPage result, ClosedCollectionAsset[] closedRows, int page,
        LibraryBrowseScope[] scopes, string query, AssetAttributeFilter filter, AssetTagFilter? tags)
    {
        _page = page;
        _publishedBrowseView = (_category, _assetLibraryFilter, _browseLibraryRevision, _sort, _page);
        _publishedBrowseScope = (scopes, query, filter, tags);
        _publishedMotionReference = _motionReference;
        _publishedClosedRows = closedRows;
        _onlineFilteredAssetCount = result.Total;
        FilteredAssetCount = result.Total + closedRows.Length;
        _browseRows = result.Items;
        bool showLibrary = LoadedLibraries().Length > 1 || closedRows.Length > 0;
        // Open members retain their existing sort order, followed by the unavailable members.
        var closedPage = closedRows.Skip(Math.Max(0, page * AssetPageSize - result.Total)).Take(AssetPageSize - _browseRows.Length);
        _browser.SetRows(_browseRows.Select((row, index) => BrowseCard(row, index, showLibrary)).Concat(closedPage.Select(ClosedCardModel)));
        _count.Text = $"{FilteredAssetCount:N0} 个结果" + (closedRows.Length > 0 ? $" · {closedRows.Length:N0} 项来源库未打开" : "");
        if (_motionReference != null && _motionSkipped > 0) _count.Text += $" · {_motionSkipped:N0} 项无法比较";
        _pageLabel.Text = $"{_page + 1} / {Math.Max(1, ((long)FilteredAssetCount + AssetPageSize - 1) / AssetPageSize)}";
        _firstPage.Disabled = _previous.Disabled = _page == 0;
        _lastPage.Disabled = _next.Disabled = ((long)_page + 1) * AssetPageSize >= FilteredAssetCount;
        _empty.Visible = FilteredAssetCount == 0;
        _empty.Text = CurrentCollection is { Members.Length: 0 } ? "集合为空\n可将资产拖入侧栏中的集合。" :
            CategoryIncludesMountedLibraries && scopes.Length == 0 ? "尚未打开资产库\n从「库管理」打开或新建资产库。" :
            _motionReference != null ? "没有匹配的动作\n可降低最低相似度、扩大搜索时间或调整筛选范围。" :
            filter.IsActive || tags != null ? "没有符合条件的资产\n调整筛选条件或搜索词。" :
            query.Length > 0 ? "没有匹配的资产\n试试其他名称或标签。" : "这里还没有资产\n导入文件，或载入内置示例。";
        SyncAssetSelection();
        RefreshCurrentAsset();
    }

    private static AssetCardModel ClosedCardModel(ClosedCollectionAsset item)
    {
        var asset = item.Row.Asset;
        var card = CardModel(asset, item.Row.Attributes, item.LibraryName, LibraryKeyPrefix + item.LibraryId + ":" + asset.Id);
        card.ClosedLibraryId = item.LibraryId;
        card.LocateClosedLibrary = item.LocateLibrary;
        card.ThumbnailPending = false;
        card.DetailLine = "预览不可用";
        if (asset.VirtualPath.Length == 0) card.MetaLine = card.SizeText = "";
        card.Tooltip = asset.Name + "\n" + item.LibraryName + "\n来源库未打开，无法预览";
        return card;
    }

    private static AssetCardModel CardModel(AssetItem item, AssetAttributes? attributes, string libraryName = "", string? key = null) => new()
    {
        Id=key??item.Id,Name=item.Name,LibraryName=libraryName,Trashed=item.Trashed,MetaLine=item.SourceFormat.TrimStart('.').ToUpperInvariant()+" · "+FormatSize(item.Size),
        DetailLine=attributes==null?"读取属性…":$"{attributes.Duration?.ToString("0.##")??"—"}s · {attributes.SourceFps?.ToString("0.#")??"—"}fps · {attributes.Skeletons.FirstOrDefault()?.Name??"未知"}",
        IsAnimation=item.Type=="Animation",Favorite=item.Favorite,ThumbnailPending=true,
        Tooltip=item.Name+"\n"+(libraryName.Length==0?"":libraryName+"\n")+item.VirtualPath+(attributes==null?"":"\n"+DescribeAttributes(attributes)),Tags=item.Tags,VirtualPath=item.VirtualPath,
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

    private void ProcessAssetBrowse()
    {
        if(_closing||_api==null||_browser==null)return;
        if (_motionReference == null && _browseRequest != null && _durationProgressLibraries.Length > 0 && Time.GetTicksMsec() >= _nextDurationProgress)
        {
            _nextDurationProgress = Time.GetTicksMsec() + 200;
            int completed = 0, total = 0;
            foreach (var library in _durationProgressLibraries)
            {
                var progress = library.Api.DurationProgress;
                completed += progress.Completed;
                total += progress.Total;
            }
            if (completed < total) _count.Text = $"正在准备时长索引 · {completed:N0} / {total:N0}";
        }
        ProcessAttributeDetails();
        if (!_thumbnailsEnabled || _thumbnailPending || Time.GetTicksMsec() < _nextThumbnailTick || !CanRenderAssetThumbnail()) return;
        foreach (int index in _browser.IsVisibleInTree() ? _browser.VisibleIndices() : [])
        {
            if (index >= _browseRows.Length || _browser.Row(index)?.Thumbnail != null || !_thumbnailAttempts.Add(index)) continue;
            _thumbnailPending = true;
            _ = LoadAssetThumbnailAsync(_browseRows[index].Library, _browseRows[index].Asset, preload: false);
            return;
        }
        if (_preview.TransportPlaying) return;
        foreach (var (library, warmup) in _thumbnailWarmups.Reverse())
        {
            if (library.Api.LibraryBusy || !warmup.Assets.IsCompletedSuccessfully || warmup.Next >= warmup.Assets.Result.Length) continue;
            _thumbnailPending = true;
            _ = LoadAssetThumbnailAsync(library, warmup.Assets.Result[warmup.Next++], preload: true);
            return;
        }
    }

    private bool CanRenderAssetThumbnail() => !_closing && !_libraryLoading && _browseRequest == null &&
        PendingAssetPreview.IsCompleted && PendingRetargetPreview.IsCompleted && _comparisonRequest == null &&
        !_api.LibraryBusy;

    private void ProcessAttributeDetails()
    {
        var api = _selectedLibrary?.Api ?? _api;
        var revision=api.BrowseCacheRevision;
        if(_attributeDetailsId==_selectedKey&&_attributeDetailsCacheRevision==revision)return;
        StopRequest(ref _attributeDetailsRequest);_attributeDetailsId=_selectedKey;_attributeDetailsCacheRevision=revision;
        _inspectorAttributes=null;_inspectorStamp="";
        if(_selected==null){_inspector.ShowEmpty();return;}
        if(_browseRows.FirstOrDefault(r=>r.Library==_selectedLibrary&&r.Asset.Id==_selected.Id)?.Attributes is {} ready){_inspectorAttributes=ready;SyncInspector();return;}
        var request=new CancellationTokenSource();_attributeDetailsRequest=request;_ = LoadAttributeDetailsAsync(api,_selected,_selectedKey!,request);
    }
    private async Task LoadAttributeDetailsAsync(WorkspaceApi api,AssetItem asset,string key,CancellationTokenSource request)
    {
        try
        {
            var attributes=await api.GetAssetAttributesAsync(asset,request.Token);
            if(!_closing&&ReferenceEquals(_attributeDetailsRequest,request)&&key==_selectedKey)
            {_inspectorAttributes=attributes;_inspectorStamp="";SyncInspector();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&ReferenceEquals(_attributeDetailsRequest,request)&&key==_selectedKey)SetStatus("属性读取失败 · "+ex.Message);}
        finally{if(ReferenceEquals(_attributeDetailsRequest,request))_attributeDetailsRequest=null;request.Dispose();}
    }
    private void SyncInspector()
    {
        if(_inspector==null)return;
        if(_selected==null){_inspector.ShowEmpty();_inspectorStamp="";return;}
        var preview = _previewAssetId == _selectedKey ? _preview : null;
        var stamp=$"{_selectedKey}|{_selected.Name}|{_selected.Tags}|{_selected.Favorite}|{_selected.Type}|{preview?.BoneCount}|{preview?.MeshCount}|{preview?.CurrentClip}|{preview?.Clips.Length}";
        if(stamp==_inspectorStamp)return;_inspectorStamp=stamp;
        var attributes = _browseRows.FirstOrDefault(r => r.Library == _selectedLibrary && r.Asset.Id == _selected.Id)?.Attributes ??
            (_attributeDetailsId == _selectedKey ? _inspectorAttributes : null);
        _inspector.SetEditable(_selectedLibrary != null);
        _inspector.ShowAsset(CardModel(_selected,attributes,_selectedLibrary!.Identity.Name),attributes,preview?.BoneCount??0,preview?.MeshCount??0,_selected.SourceFormat,
            (preview?.Clips ?? []).Select(clip => (clip, preview?.Player is { } player && player.HasAnimation(clip) ? (double?)player.GetAnimation(clip).Length : null)),
            preview?.CurrentClip);
    }

    private void SyncLibraryThumbnailWarmups(LibrarySession[] libraries)
    {
        if (!_thumbnailsEnabled) return;
        foreach (var closed in _thumbnailWarmups.Keys.Except(libraries).ToArray())
        {
            _thumbnailWarmups.Remove(closed);
            foreach (var key in _thumbnailTextures.Keys.Where(key => key.Library == closed).ToArray())
            {
                _recentThumbnails.Remove(_thumbnailTextures[key]);
                _thumbnailTextures.Remove(key);
            }
        }
        foreach (var library in libraries)
        {
            if (_thumbnailWarmups.ContainsKey(library)) continue;
            var assets = Task.Run(() =>
            {
                try { return library.Api.Database.SearchAll().ToArray(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    GD.PushWarning($"无法预载「{library.Identity.Name}」的缩略图 · {error.Message}");
                    return [];
                }
            });
            _thumbnailWarmups.Add(library, new LibraryThumbnailWarmup(assets));
        }
    }

    private async Task LoadAssetThumbnailAsync(LibrarySession library, AssetItem asset, bool preload)
    {
        var api = library.Api;
        int revision = api.BrowseCacheRevision;
        bool reused = false;
        bool Current() => !_closing && _thumbnailWarmups.ContainsKey(library) && _libraries.FindOpenLibrary(library.Identity.Id) == library;
        int CurrentRow()
        {
            if (!Current()) return -1;
            int index = _browser.IndexOf(AssetKey(library, asset.Id));
            return index >= 0 && index < _browseRows.Length && _browseRows[index] is { } row &&
                row.Library == library && row.Asset.FileHash == asset.FileHash && row.Asset.VirtualPath == asset.VirtualPath ? index : -1;
        }

        bool CanGenerate() => CanRenderAssetThumbnail() && !api.LibraryBusy && Time.GetTicksMsec() >= _thumbnailRenderAfter;

        async Task<bool> WaitToGenerate()
        {
            while (Current() && !CanGenerate())
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            return Current();
        }

        void ShowThumbnail(int index, Texture2D texture)
        {
            _browser.UpdateRow(index, card =>
            {
                card.Thumbnail = texture;
                card.ThumbnailPending = false;
            });
        }

        void CacheThumbnail(ThumbnailKey key, Image image)
        {
            int index = CurrentRow();
            if (index < 0 || revision != api.BrowseCacheRevision) return;
            var texture = ImageTexture.CreateFromImage(image);
            _thumbnailTextures.Add(key, _recentThumbnails.AddFirst((key, texture)));
            if (_recentThumbnails.Count > ThumbnailMemoryLimit)
            {
                _thumbnailTextures.Remove(_recentThumbnails.Last!.Value.Key);
                _recentThumbnails.RemoveLast();
            }
            ShowThumbnail(index, texture);
        }

        try
        {
            var request = await Task.Run(() =>
            {
                if (preload) api.ReadBrowseAttributes(asset, CancellationToken.None);
                return api.PrepareAssetThumbnail(asset);
            });
            if (!Current() || revision != api.BrowseCacheRevision) return;
            var key = new ThumbnailKey(library, request.CachePath, revision);
            int index = CurrentRow();
            if (_thumbnailTextures.TryGetValue(key, out var entry))
            {
                if (index >= 0)
                {
                    _recentThumbnails.Remove(entry);
                    _recentThumbnails.AddFirst(entry);
                    ShowThumbnail(index, entry.Value.Texture);
                }
                reused = true;
                return;
            }

            var (exists, cachedImage) = await Task.Run(() =>
            {
                var bytes = api.ReadAssetThumbnail(request);
                if (bytes == null || index < 0) return (bytes != null, (Image?)null);
                var image = new Image();
                if (image.LoadPngFromBuffer(bytes) == Error.Ok) return (true, image);
                image.Dispose();
                return (false, null);
            });
            using var cached = cachedImage;
            if (!Current() || revision != api.BrowseCacheRevision) return;
            if (exists)
            {
                if (cached != null) CacheThumbnail(key, cached);
                return;
            }

            if (!await WaitToGenerate()) return;
            if (_thumbnailRenderer == null)
            {
                _thumbnailRenderer = new AssetThumbnailRenderer();
                AddChild(_thumbnailRenderer);
            }
            using var rendered = await _thumbnailRenderer.RenderAsync(request.SourcePath, Current, WaitToGenerate);
            if (!Current() || revision != api.BrowseCacheRevision) return;
            if (rendered == null || rendered.IsEmpty()) throw new IOException("没有可绘制的模型或姿势。");
            CacheThumbnail(key, rendered);
            await Task.Run(() =>
            {
                var png = rendered.SavePngToBuffer();
                if (revision == api.BrowseCacheRevision) api.SaveAssetThumbnail(request, png);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            int index = CurrentRow();
            if (index >= 0) _browser.UpdateRow(index, card => card.ThumbnailPending = false);
            if (!preload && Current()) GD.PushWarning($"资产缩略图不可用 · {asset.Name} · {ex.Message}");
        }
        finally
        {
            _thumbnailPending = false;
            if (!_closing && !reused) _nextThumbnailTick = Time.GetTicksMsec() + (preload ? 64ul : 32ul);
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
        ResetAssetFilters(refresh: false);
        SetSkeletonOptions([]);
        if(_workspace.ActiveDrawer=="filters")_workspace.CloseDrawer();
        _inspectorAttributes=null;_inspectorStamp="";
    }
}
