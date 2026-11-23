using Animanager.Core.Browse;
using Animanager.Core.Models;
using Animanager.Core.Storage;

namespace Animanager.Core;

public sealed record AssetBrowseRow(AssetItem Asset, AssetAttributes? Attributes);
public sealed record AssetBrowsePage(AssetBrowseRow[] Items, int Total, int Page);
internal sealed record LibraryAssetRow(LibrarySession Library, AssetBrowseRow Row)
{
    internal AssetItem Asset => Row.Asset;
    internal AssetAttributes? Attributes => Row.Attributes;
}
internal sealed record LibraryAssetPage(LibraryAssetRow[] Items, int Total, int Page);
internal readonly record struct LibraryBrowseScope(LibrarySession Library, string Category, string? MemberIdsJson = null);

public sealed partial class WorkspaceApi
{
    private AssetBrowseCache? _browseCache;
    private int _browseCacheRevision;
    internal AssetBrowseCache BrowseCache => LazyInitializer.EnsureInitialized(ref _browseCache,
        () => new AssetBrowseCache(Storage, write => TryWriteBrowseCache(write)));
    internal int BrowseCacheRevision => Volatile.Read(ref _browseCacheRevision);

    private AssetDurationIndex? _durationIndex;
    private readonly object _durationSnapshotGate = new();
    private DurationBrowseSnapshot? _durationSnapshot;
    private readonly object _browseResultsGate = new();
    private BrowseResults? _browseResults;

    private readonly record struct BrowseResultRow(int Source, AssetBrowseRow Row);
    private sealed record BrowseResults(BrowseSource[] Sources, string Query, AssetAttributeFilter Filter, AssetSort Sort,
        AssetTagFilter? Tags, (string Catalog, int Files)[] Revisions, BrowseResultRow[] Rows);

    internal AssetDurationIndex DurationIndex
    {
        get
        {
            lock (_activityGate)
            {
                ObjectDisposedException.ThrowIf(_retired, this);
                return _durationIndex ??= new(this);
            }
        }
    }
    internal (string Catalog, int Files) DurationRevision => (Database.CatalogRevision, DurationIndex.Revision);
    internal AssetAttributes ReadBrowseAttributes(AssetItem asset, CancellationToken ct) => BrowseCache.ReadAttributes(asset, ct);
    internal double? ReadSourceDuration(AssetItem asset, CancellationToken ct) => BrowseCache.ReadCheckedAttributes(asset, ct).Duration;
    internal (int Completed, int Total) DurationProgress => _durationIndex?.Progress ?? default;

    private static bool UsesDuration(AssetAttributeFilter filter, AssetSort sort) =>
        sort.Field == AssetSortField.Duration || filter.Duration?.IsActive == true;

    private ArraySegment<TimedRow> DurationResults(BrowseSource[] sources, string query,
        AssetAttributeFilter filter, AssetSort sort, AssetTagFilter? tags, CancellationToken ct)
    {
        filter.Validate();
        lock (_durationSnapshotGate)
        {
            ct.ThrowIfCancellationRequested();
            if (_durationSnapshot?.Current(sources, query) != true)
                _durationSnapshot = new(sources, query, ct);
            return _durationSnapshot.Select(filter, sort, tags, ct);
        }
    }

    private static ArraySegment<T> PageSlice<T>(ArraySegment<T> rows, int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        int start = (int)Math.Min((long)page * pageSize, rows.Count);
        return rows.Slice(start, Math.Min(pageSize, rows.Count - start));
    }

    public Task<AssetBrowsePage> BrowseAssetsAsync(string query, string category, AssetAttributeFilter filter,
        int page = 0, int pageSize = 60, CancellationToken cancellationToken = default) =>
        BrowseAssetsAsync(query, category, filter, AssetSort.Default, page, pageSize, cancellationToken);

    public Task<AssetBrowsePage> BrowseAssetsAsync(string query, string category, AssetAttributeFilter filter,
        AssetSort sort, int page = 0, int pageSize = 60, CancellationToken cancellationToken = default) =>
        BrowseAssetsAsync(query, category, filter, sort, page, pageSize, cancellationToken, tags: null);

    private Task<AssetBrowsePage> BrowseAssetsAsync(string query, string category, AssetAttributeFilter filter,
        AssetSort sort, int page, int pageSize, CancellationToken cancellationToken, AssetTagFilter? tags, string? memberIdsJson = null) => Task.Run(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        if (sort.Field == AssetSortField.LibraryMix) sort = sort with { Field = AssetSortField.Imported };
        if (UsesDuration(filter, sort))
        {
            var result = DurationResults([new(this, "", category, memberIdsJson)], query, filter, sort, tags, cancellationToken);
            var rows = PageSlice(result, page, pageSize).Select(row => row.Details(this, cancellationToken)).ToArray();
            return new AssetBrowsePage(rows, result.Count, page);
        }
        if (tags == null && !filter.IsActive && !sort.NeedsAttributes)
        {
            var result = Database.Search(query, category, sort, page, pageSize, memberIdsJson);
            return new AssetBrowsePage(result.Items.Select(asset => new AssetBrowseRow(asset,
                BrowseCache.ReadAttributes(asset, cancellationToken))).ToArray(), result.Total, page);
        }
        var matches = OrderedBrowseResults([new(this, "", category, memberIdsJson)], query, filter, sort, tags, cancellationToken);
        var items = PageSlice(new ArraySegment<BrowseResultRow>(matches), page, pageSize)
            .Select(row => CopyWithAttributes(row.Row, cancellationToken)).ToArray();
        return new AssetBrowsePage(items, matches.Length, page);
    }, cancellationToken);

    internal static Task<LibraryAssetPage> BrowseLibrariesAsync(LibrarySession[] libraries, string query,
        string category, AssetAttributeFilter filter, AssetSort sort, int page, int pageSize, CancellationToken ct,
        AssetTagFilter? tags = null) => BrowseLibrariesAsync(libraries.Select(library => new LibraryBrowseScope(library, category)).ToArray(),
            query, filter, sort, page, pageSize, ct, tags);

    internal static Task<int> FindAssetBrowseIndexAsync(LibraryBrowseScope[] scopes, LibrarySession library, string id,
        string query, AssetAttributeFilter filter, AssetSort sort, AssetTagFilter? tags, CancellationToken ct) => Task.Run(() =>
    {
        int sourceIndex = Array.FindIndex(scopes, scope => scope.Library == library);
        if (sourceIndex < 0) return -1;

        var api = scopes[0].Library.Api;
        var sources = BrowseSources(scopes);
        if (sources.Length == 1 && sort.Field == AssetSortField.LibraryMix) sort = sort with { Field = AssetSortField.Imported };
        IEnumerable<string?> ids;
        if (UsesDuration(filter, sort))
            ids = api.DurationResults(sources, query, filter, sort, tags, ct)
                .Select(row => row.Source == sourceIndex ? row.Row.Asset.Id : null);
        else if (sources.Length == 1 && tags == null && !filter.IsActive && !sort.NeedsAttributes)
        {
            var source = sources[0];
            ids = api.Database.Search(query, source.Category, sort, 0, int.MaxValue, source.MemberIdsJson).Items.Select(asset => asset.Id);
        }
        else
            ids = api.OrderedBrowseResults(sources, query, filter, sort, tags, ct)
                .Select(row => row.Source == sourceIndex ? row.Row.Asset.Id : null);

        int index = 0;
        foreach (var candidate in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate == id) return index;
            index++;
        }
        return -1;
    }, ct);

    private static BrowseSource[] BrowseSources(LibraryBrowseScope[] scopes) => scopes
        .Select(scope => new BrowseSource(scope.Library.Api, scopes.Length == 1 ? "" : scope.Library.Identity.Id,
            scope.Category, scope.MemberIdsJson)).ToArray();

    internal static async Task<LibraryAssetPage> BrowseLibrariesAsync(LibraryBrowseScope[] scopes, string query,
        AssetAttributeFilter filter, AssetSort sort, int page, int pageSize, CancellationToken ct, AssetTagFilter? tags)
    {
        if (scopes.Length == 0) return new([], 0, 0);
        var first = scopes[0];
        if (scopes.Length == 1)
        {
            var result = await first.Library.Api.BrowseAssetsAsync(query, first.Category, filter, sort, page, pageSize, ct, tags, first.MemberIdsJson);
            return new(result.Items.Select(row => new LibraryAssetRow(first.Library, row)).ToArray(), result.Total, result.Page);
        }

        return await Task.Run(() =>
        {
            var sources = BrowseSources(scopes);
            if (UsesDuration(filter, sort))
            {
                var slice = first.Library.Api.DurationResults(sources, query, filter, sort, tags, ct);
                var rows = PageSlice(slice, page, pageSize).Select(row =>
                    new LibraryAssetRow(scopes[row.Source].Library, row.Details(sources[row.Source].Api, ct))).ToArray();
                return new LibraryAssetPage(rows, slice.Count, page);
            }
            var matches = first.Library.Api.OrderedBrowseResults(sources, query, filter, sort, tags, ct);
            var items = PageSlice(new ArraySegment<BrowseResultRow>(matches), page, pageSize)
                .Select(row => new LibraryAssetRow(scopes[row.Source].Library, sources[row.Source].Api.CopyWithAttributes(row.Row, ct))).ToArray();
            return new LibraryAssetPage(items, matches.Length, page);
        }, ct);
    }

    private BrowseResultRow[] OrderedBrowseResults(BrowseSource[] sources, string query,
        AssetAttributeFilter filter, AssetSort sort, AssetTagFilter? tags, CancellationToken ct)
    {
        lock (_browseResultsGate)
        {
            ct.ThrowIfCancellationRequested();
            var revisions = sources.Select(source => filter.IsActive || sort.NeedsAttributes
                ? source.Api.DurationRevision : (source.Api.Database.CatalogRevision, 0)).ToArray();
            if (_browseResults is { } cached && cached.Query == query && cached.Filter == filter && cached.Sort == sort &&
                AssetTagFilter.Equivalent(cached.Tags, tags) && cached.Sources.AsSpan().SequenceEqual(sources) &&
                cached.Revisions.AsSpan().SequenceEqual(revisions))
                return cached.Rows;

            var matches = new List<BrowseResultRow>();
            for (int i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                var rows = source.Api.FilterAssets(query, source.Category, filter, ct, tags, sort.NeedsAttributes, source.MemberIdsJson);
                foreach (var row in rows) matches.Add(new(i, row));
            }
            // Interleave the import ranks from each source before slicing the combined page.
            var importOrder = sort with { Field = AssetSortField.Imported };
            var ordered = sort.Field == AssetSortField.LibraryMix
                ? matches.GroupBy(row => row.Source)
                    .SelectMany(group => OrderBrowseRows(group, importOrder, row => row.Row).Select((row, rank) => (row, rank)))
                    .OrderBy(entry => entry.rank).Select(entry => entry.row)
                : OrderBrowseRows(matches, sort, row => row.Row).ThenBy(row => sources[row.Source].LibraryId, StringComparer.Ordinal);
            var result = ordered.ToArray();
            ct.ThrowIfCancellationRequested();
            _browseResults = new(sources, query, filter, sort, tags, revisions, result);
            return result;
        }
    }

    private AssetBrowseRow CopyWithAttributes(AssetBrowseRow row, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(row.Asset with { }, row.Attributes ?? BrowseCache.ReadAttributes(row.Asset, ct));
    }

    internal static IOrderedEnumerable<T> OrderBrowseRows<T>(IEnumerable<T> rows, AssetSort sort, Func<T, AssetBrowseRow> data)
    {
        object? Value(T entry)
        {
            var row = data(entry);
            return sort.Field switch
            {
                AssetSortField.Duration => row.Attributes?.Duration, AssetSortField.Fps => row.Attributes?.SourceFps,
                AssetSortField.Name => row.Asset.Name, AssetSortField.Size => row.Asset.Size,
                AssetSortField.Favorite => row.Asset.Favorite, _ => row.Asset.ImportedAt
            };
        }
        var known = rows.OrderBy(row => Value(row) == null);
        var comparer = Comparer<object?>.Create((a, b) => a is string x && b is string y
            ? StringComparer.Ordinal.Compare(x, y) : Comparer<object?>.Default.Compare(a, b));
        var ordered = sort.Descending ? known.ThenByDescending(Value, comparer) : known.ThenBy(Value, comparer);
        return ordered.ThenBy(row => data(row).Asset.Name, StringComparer.Ordinal)
            .ThenBy(row => data(row).Asset.Id, StringComparer.Ordinal);
    }

    public Task<List<AssetItem>> GetMatchingAssetsAsync(string query, string category, AssetAttributeFilter filter,
        CancellationToken cancellationToken = default) => GetMatchingAssetsAsync(query, category, filter, cancellationToken, tags: null);

    internal Task<List<AssetItem>> GetMatchingAssetsAsync(string query, string category, AssetAttributeFilter filter,
        CancellationToken cancellationToken, AssetTagFilter? tags, string? memberIdsJson = null) => Task.Run(() => filter.Duration?.IsActive == true
            ? DurationResults([new(this, "", category, memberIdsJson)], query, filter, AssetSort.Default, tags, cancellationToken)
                .Select(row => row.Row.Asset with { }).ToList()
            : FilterAssets(query, category, filter, cancellationToken, tags, memberIdsJson: memberIdsJson).Select(row => row.Asset).ToList(), cancellationToken);

    public Task<AssetAttributes> GetAssetAttributesAsync(AssetItem asset, CancellationToken cancellationToken = default) =>
        Task.Run(() => BrowseCache.ReadAttributes(asset, cancellationToken), cancellationToken);

    public Task<AssetSkeleton[]> GetAssetSkeletonsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var skeletons = new Dictionary<string, AssetSkeleton>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in Database.Snapshot().Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var skeleton in BrowseCache.ReadAttributes(asset, cancellationToken).Skeletons)
                skeletons.TryAdd(skeleton.Id, skeleton);
        }
        return skeletons.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
    }, cancellationToken);

    private List<AssetBrowseRow> FilterAssets(string query, string category, AssetAttributeFilter filter,
        CancellationToken cancellationToken, AssetTagFilter? tags, bool includeAttributes = false, string? memberIdsJson = null)
    {
        filter.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = Database.SearchAll(query, category, memberIdsJson);
        var matches = new List<AssetBrowseRow>();
        foreach (var asset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tags?.Matches(asset.Tags) == false) continue;
            var attributes = filter.IsActive || includeAttributes ? BrowseCache.ReadAttributes(asset, cancellationToken) : null;
            if (attributes == null || filter.Matches(attributes)) matches.Add(new(asset, attributes));
        }
        return matches;
    }

    internal AssetThumbnailRequest PrepareAssetThumbnail(AssetItem asset) => BrowseCache.PrepareThumbnail(asset);
    internal byte[]? ReadAssetThumbnail(AssetThumbnailRequest request) => BrowseCache.ReadThumbnail(request);
    internal void SaveAssetThumbnail(AssetThumbnailRequest request, byte[] png) => BrowseCache.SaveThumbnail(request, png);

    internal void InvalidateAssetBrowseCache()
    {
        _browseCache?.Invalidate();
        _durationIndex?.Invalidate();
        Interlocked.Increment(ref _browseCacheRevision);
    }

    internal bool TryWriteBrowseCache(Action write)
    {
        lock (_activityGate)
        {
            if (_maintenance || _retired) return false;
            write();
            return true;
        }
    }
}
