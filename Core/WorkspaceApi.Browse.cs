using Animanager.Core.Browse;
using Animanager.Core.Models;

namespace Animanager.Core;

public sealed record AssetBrowseRow(AssetItem Asset, AssetAttributes? Attributes);
public sealed record AssetBrowsePage(AssetBrowseRow[] Items, int Total, int Page);

public sealed partial class WorkspaceApi
{
    private AssetBrowseCache? _browseCache;
    private int _browseCacheRevision;
    private AssetBrowseCache BrowseCache => LazyInitializer.EnsureInitialized(ref _browseCache,
        () => new AssetBrowseCache(Storage, WriteBrowseCache));
    internal int BrowseCacheRevision => Volatile.Read(ref _browseCacheRevision);

    public Task<AssetBrowsePage> BrowseAssetsAsync(string query, string category, AssetAttributeFilter filter,
        int page = 0, int pageSize = 60, CancellationToken cancellationToken = default) =>
        BrowseAssetsAsync(query, category, filter, AssetSort.Default, page, pageSize, cancellationToken);

    public Task<AssetBrowsePage> BrowseAssetsAsync(string query, string category, AssetAttributeFilter filter,
        AssetSort sort, int page = 0, int pageSize = 60, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        if (!filter.IsActive && !sort.NeedsAttributes)
        {
            var result = Database.Search(query, category, sort, page, pageSize);
            return new AssetBrowsePage(result.Items.Select(asset => new AssetBrowseRow(asset,
                BrowseCache.ReadAttributes(asset, cancellationToken))).ToArray(), result.Total, page);
        }
        var matches = FilterAssets(query, category, filter, cancellationToken);
        if (sort.NeedsAttributes)
            matches = matches.Select(row => row.Attributes != null ? row : row with
                { Attributes = BrowseCache.ReadAttributes(row.Asset, cancellationToken) }).ToList();
        object? Value(AssetBrowseRow row) => sort.Field switch
        {
            AssetSortField.Duration => row.Attributes?.Duration, AssetSortField.Fps => row.Attributes?.SourceFps,
            AssetSortField.Name => row.Asset.Name, AssetSortField.Size => row.Asset.Size,
            AssetSortField.Favorite => row.Asset.Favorite, _ => row.Asset.ImportedAt
        };
        var known = matches.OrderBy(row => Value(row) == null);
        var comparer = Comparer<object?>.Create((a, b) => a is string x && b is string y
            ? StringComparer.Ordinal.Compare(x, y) : Comparer<object?>.Default.Compare(a, b));
        var ordered = sort.Descending ? known.ThenByDescending(Value, comparer) : known.ThenBy(Value, comparer);
        matches = ordered.ThenBy(row => row.Asset.Name, StringComparer.Ordinal).ThenBy(row => row.Asset.Id, StringComparer.Ordinal).ToList();
        long start = (long)page * pageSize;
        var items = start >= matches.Count ? [] : matches.Skip((int)start).Take(pageSize)
            .Select(row => row.Attributes != null ? row : row with { Attributes = BrowseCache.ReadAttributes(row.Asset, cancellationToken) }).ToArray();
        return new AssetBrowsePage(items, matches.Count, page);
    }, cancellationToken);

    public Task<List<AssetItem>> GetMatchingAssetsAsync(string query, string category, AssetAttributeFilter filter,
        CancellationToken cancellationToken = default) => Task.Run(() => FilterAssets(query, category, filter, cancellationToken)
        .Select(row => row.Asset).ToList(), cancellationToken);

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

    private List<AssetBrowseRow> FilterAssets(string query, string category, AssetAttributeFilter filter, CancellationToken cancellationToken)
    {
        filter.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = Database.SearchAll(query, category);
        var matches = new List<AssetBrowseRow>();
        foreach (var asset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = filter.IsActive ? BrowseCache.ReadAttributes(asset, cancellationToken) : null;
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
        Interlocked.Increment(ref _browseCacheRevision);
    }

    private void WriteBrowseCache(Action write)
    {
        lock (_activityGate)
        {
            if (!_maintenance && !_retired) write();
        }
    }
}
