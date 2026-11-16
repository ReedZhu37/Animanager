namespace Animanager.Core.Models;

public sealed record AssetItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string VirtualPath { get; init; } = "";
    public string SourceFormat { get; init; } = "";
    public string FileHash { get; init; } = "";
    public string Tags { get; set; } = "";
    public string Type { get; set; } = "Model";
    public bool Favorite { get; set; }
    public long Size { get; init; }
    public string ImportedAt { get; init; } = DateTime.UtcNow.ToString("O");
}
