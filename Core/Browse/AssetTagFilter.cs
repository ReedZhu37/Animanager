namespace Animanager.Core.Browse;

internal sealed class AssetTagFilter(IEnumerable<string> tags, bool matchAll)
{
    private readonly HashSet<string> _tags = new(tags, StringComparer.OrdinalIgnoreCase);
    private readonly bool _matchAll = matchAll;

    internal static bool Equivalent(AssetTagFilter? a, AssetTagFilter? b) => ReferenceEquals(a, b) ||
        a != null && b != null && a._matchAll == b._matchAll && a._tags.SetEquals(b._tags);

    internal bool Matches(string text)
    {
        var assetTags = WorkspaceApi.ParseTags(text);
        return _matchAll ? _tags.IsSubsetOf(assetTags) : _tags.Overlaps(assetTags);
    }
}
