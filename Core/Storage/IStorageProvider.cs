namespace Animanager.Core.Storage;

public interface IStorageProvider
{
    Task<string> AcquireLocalFileAsync(string virtualPath, CancellationToken ct = default);
    Task PutFileAsync(string sourcePath, string virtualPath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default);
}
