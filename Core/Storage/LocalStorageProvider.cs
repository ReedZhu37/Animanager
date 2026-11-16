namespace Animanager.Core.Storage;

public sealed class LocalStorageProvider(string root) : IStorageProvider
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Resolve(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || Path.IsPathRooted(virtualPath) || virtualPath.Contains(':'))
            throw new IOException("资产路径必须是库内相对路径。");
        var path = Path.GetFullPath(Path.Combine(Root, virtualPath));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("资产路径不能离开库目录。");
        return path;
    }

    public Task<string> AcquireLocalFileAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve(virtualPath);
        return Task.FromResult(File.Exists(path) ? path : throw new FileNotFoundException("库文件不存在。", path));
    }

    public async Task PutFileAsync(string sourcePath, string virtualPath, CancellationToken ct = default)
    {
        var target = Resolve(virtualPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(Resolve(virtualPath)));
    }
}
