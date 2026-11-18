namespace Animanager.Core.Browse;

public enum AssetSortField { Imported, Name, Size, Favorite, Duration, Fps }

public sealed record AssetSort(AssetSortField Field = AssetSortField.Imported, bool Descending = true)
{
    public static AssetSort Default { get; } = new();
    public bool NeedsAttributes => Field is AssetSortField.Duration or AssetSortField.Fps;
    public string Key => Field.ToString().ToLowerInvariant();
    public static AssetSort FromKey(string key, bool descending) =>
        new(Enum.TryParse<AssetSortField>(key, true, out var field) ? field : AssetSortField.Imported, descending);

    // Only database-owned column names reach SQL. Attribute sorts are handled by WorkspaceApi.
    internal string SqlOrder => (Field switch
    {
        AssetSortField.Name => "name", AssetSortField.Size => "size",
        AssetSortField.Favorite => "favorite", AssetSortField.Imported => "imported",
        _ => throw new ArgumentException("Attribute sorting requires BrowseAssetsAsync.")
    }) + (Descending ? " DESC" : " ASC") + ",name,id";
}
