using Animanager.Core.Models;

namespace Animanager.Core.Browse;

internal readonly record struct BrowseSource(WorkspaceApi Api, string LibraryId, string Category, string? MemberIdsJson = null);
internal sealed record TimedRow(int Source, AssetBrowseRow Row, double? Duration)
{
    private AssetAttributes? _details;

    internal AssetBrowseRow Details(WorkspaceApi api, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = Row.Attributes ?? Volatile.Read(ref _details);
        if (attributes == null)
        {
            attributes = api.ReadBrowseAttributes(Row.Asset, ct);
            Interlocked.CompareExchange(ref _details, attributes, null);
        }
        return new(Row.Asset with { }, attributes);
    }
}

internal sealed class DurationBrowseSnapshot
{
    private readonly BrowseSource[] _sources;
    private readonly (string Catalog, int Files)[] _revisions;
    private readonly string _query;
    private readonly TimedRow[] _ascending, _descending;
    private readonly int _known;
    private (AssetAttributeFilter Filter, AssetSort Sort, AssetTagFilter? Tags, ArraySegment<TimedRow> Rows)? _last;

    internal DurationBrowseSnapshot(BrowseSource[] sources, string query, CancellationToken ct)
    {
        _sources = sources;
        _query = query;
        _revisions = sources.Select(s => s.Api.DurationRevision).ToArray();
        var rows = new List<TimedRow>();
        for (int i = 0; i < sources.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[i];
            var api = source.Api;
            var assets = api.Database.SearchAll(query, source.Category, source.MemberIdsJson);
            var durations = api.DurationIndex.Read(assets, ct);
            for (int j = 0; j < assets.Count; j++) rows.Add(new(i, new(assets[j], null), durations[j]));
        }
        _ascending = Sort(false);
        _descending = Sort(true);
        _known = rows.Count(row => row.Duration.HasValue);

        TimedRow[] Sort(bool descending)
        {
            var knownFirst = rows.OrderBy(row => !row.Duration.HasValue);
            var byDuration = descending ? knownFirst.ThenByDescending(row => row.Duration) : knownFirst.ThenBy(row => row.Duration);
            return byDuration.ThenBy(row => row.Row.Asset.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Row.Asset.Id, StringComparer.Ordinal)
                .ThenBy(row => sources[row.Source].LibraryId, StringComparer.Ordinal).ToArray();
        }
    }

    internal bool Current(BrowseSource[] sources, string query)
    {
        if (_query != query || !_sources.AsSpan().SequenceEqual(sources)) return false;
        for (int i = 0; i < sources.Length; i++)
            if (_revisions[i] != sources[i].Api.DurationRevision) return false;
        return true;
    }

    internal ArraySegment<TimedRow> Select(AssetAttributeFilter filter, AssetSort sort, AssetTagFilter? tags, CancellationToken ct)
    {
        if (_last is { } last && last.Filter == filter && last.Sort == sort && AssetTagFilter.Equivalent(last.Tags, tags))
            return last.Rows;

        bool descending = sort.Field == AssetSortField.Duration && sort.Descending;
        var order = descending ? _descending : _ascending;
        var (start, end) = Bounds(filter.Duration, descending);
        var range = new ArraySegment<TimedRow>(order, start, end - start);
        bool details = filter.SkeletonId != null || filter.UnknownSkeleton || filter.SourceFps?.IsActive == true || sort.Field == AssetSortField.Fps;
        ArraySegment<TimedRow> result;
        if (sort.Field == AssetSortField.Duration && !details && tags == null)
            result = range;
        else
        {
            var matches = new List<TimedRow>();
            foreach (var candidate in range)
            {
                ct.ThrowIfCancellationRequested();
                if (tags?.Matches(candidate.Row.Asset.Tags) == false) continue;
                var row = candidate;
                if (details)
                {
                    var hydrated = row.Details(_sources[row.Source].Api, ct);
                    if (!filter.Matches(hydrated.Attributes!)) continue;
                    row = row with { Row = hydrated };
                }
                matches.Add(row);
            }
            IEnumerable<TimedRow> sorted = sort.Field switch
            {
                AssetSortField.Duration => matches,
                AssetSortField.LibraryMix => matches.GroupBy(r => r.Source).OrderBy(group => group.Key)
                    .SelectMany(group => WorkspaceApi.OrderBrowseRows(group, sort with { Field = AssetSortField.Imported }, r => r.Row)
                        .Select((row, rank) => (row, rank))).OrderBy(entry => entry.rank).Select(entry => entry.row),
                _ => WorkspaceApi.OrderBrowseRows(matches, sort, r => r.Row)
                    .ThenBy(r => _sources[r.Source].LibraryId, StringComparer.Ordinal)
            };
            result = new(sorted.ToArray());
        }
        ct.ThrowIfCancellationRequested();
        _last = (filter, sort, tags, result);
        return result;
    }

    private (int Start, int End) Bounds(AssetNumberFilter? filter, bool descending)
    {
        if (filter?.IsActive != true) return (0, _ascending.Length);
        if (filter.UnknownOnly) return (_known, _ascending.Length);
        int from = filter.Minimum is { } min ? First(value => AssetNumberFilter.AtLeast(value, min)) : 0;
        int to = filter.Maximum is { } max ? First(value => !AssetNumberFilter.AtMost(value, max)) : _known;
        // Both bounds lie between equal-duration groups; names within each group stay ascending.
        return descending ? (_known - to, _known - from) : (from, to);
    }

    private int First(Func<double, bool> predicate)
    {
        int low = 0, high = _known;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (predicate(_ascending[mid].Duration!.Value)) high = mid;
            else low = mid + 1;
        }
        return low;
    }
}
