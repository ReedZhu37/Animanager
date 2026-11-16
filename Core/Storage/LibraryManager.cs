using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;

namespace Animanager.Core.Storage;

public sealed record LibraryIdentity(int Version, string Id, string Name);
public sealed record LibraryEntry(string Id, string Name, string Path, DateTime? LastOpenedUtc);
internal sealed record LibraryStartup(LibrarySession? Session, bool Bootstrap, string Message);
internal sealed record LibraryRegistry(int Version, LibraryEntry[] Entries);

public sealed class LibrarySession(LibraryIdentity identity, WorkspaceApi api, FileStream lease) : IDisposable
{
    public LibraryIdentity Identity { get; } = identity;
    public WorkspaceApi Api { get; } = api;
    public string Root => Api.Storage.Root;
    public void Dispose()
    {
        Api.Retire();
        lease.Dispose();
    }
}

public sealed class LibraryManager(string registryPath, BlenderPipeline blender) : IDisposable
{
    public const string IdentityName = "library.json";
    internal const string LockName = ".library.lock";
    private readonly object _gate = new();
    private bool _restoring;
    public LibrarySession? Active { get; private set; }

    internal LibraryStartup OpenStartup(string? explicitRoot, params string[] defaults)
    {
        var errors = new List<string>();
        LibrarySession? TryOpen(string root, bool create)
        {
            try { return Open(root, create); }
            catch (Exception ex) { errors.Add(ex.Message); return null; }
        }
        LibraryStartup Result(LibrarySession? session, bool bootstrap = false) => new(session, bootstrap, string.Join("\n", errors));
        if (explicitRoot != null) return Result(TryOpen(explicitRoot, true));

        LibraryEntry[] entries;
        try { entries = Entries(); }
        catch (Exception ex) { errors.Add(ex.Message); entries = []; }
        foreach (var entry in entries.Where(e => e.LastOpenedUtc != null))
            if (TryOpen(entry.Path, false) is { } session) return Result(session);

        foreach (var root in defaults.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool bootstrap = !HasLibrary(root);
            if (TryOpen(root, bootstrap) is { } session) return Result(session, bootstrap);
        }
        return Result(null);
    }

    public LibraryEntry[] Entries()
    {
        lock (_gate)
            return ReadRegistry().OrderByDescending(e => e.LastOpenedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public LibraryEntry Create(string root, string name)
    {
        lock (_gate)
        {
            root = Canonical(root);
            name = name.Trim();
            if (name.Length == 0) throw new ArgumentException("请输入库名称。");
            if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("新库需要空目录：" + root);
            Directory.CreateDirectory(root);
            using var lease = Lock(root);
            var identity = NewIdentity(name);
            _ = new AssetDatabase(Path.Combine(root, AssetDatabase.IndexName));
            MotionJson.Atomic(Path.Combine(root, IdentityName), identity);
            return Remember(identity, root, false);
        }
    }

    public LibraryEntry Register(string root)
    {
        lock (_gate)
        {
            root = Canonical(root);
            var active = Active;
            if (active != null && SamePath(active.Root, root)) return Remember(active.Identity, root, false);
            RequireExisting(root);
            using var lease = Lock(root);
            var identity = IdentityForExisting(root);
            ValidateLocation(identity, root);
            return Remember(identity, root, false);
        }
    }

    public LibraryEntry Relocate(string id, string root)
    {
        lock (_gate)
        {
            var entry = Entries().SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException("没有此库登记：" + id);
            root = Canonical(root);
            if (SamePath(entry.Path, root) && HasLibrary(root)) return entry;
            if (HasLibrary(entry.Path)) throw new InvalidOperationException("原库仍可访问，请使用备份恢复来登记独立副本。");
            RequireExisting(root);
            using var lease = Lock(root);
            var identity = ReadIdentity(root);
            if (identity.Id != id) throw new InvalidDataException("所选目录的库 ID 与登记记录不同。");
            return Remember(identity, root, false);
        }
    }

    public LibrarySession OpenById(string id) => Open(Entries().SingleOrDefault(e => e.Id == id)?.Path
        ?? throw new KeyNotFoundException("没有此库登记：" + id));

    public LibrarySession CreateAndOpen(string root, string name)
    {
        lock (_gate)
        {
            RequireAvailable();
            using var transition = Active?.Api.BeginMaintenance();
            return OpenCore(Create(root, name).Path);
        }
    }

    public LibrarySession Open(string root, bool createIfMissing = false)
    {
        lock (_gate)
        {
            RequireAvailable();
            root = Canonical(root);
            if (Active is {} current && SamePath(current.Root, root)) return current;
            using var transition = Active?.Api.BeginMaintenance();
            return OpenCore(root, createIfMissing);
        }
    }

    private LibrarySession OpenCore(string root, bool createIfMissing = false)
    {
        if (!HasLibrary(root) && createIfMissing) Create(root, DefaultName(root));
        RequireExisting(root);
        var lease = Lock(root);
        LibrarySession next;
        try
        {
            var identity = IdentityForExisting(root);
            ValidateLocation(identity, root);
            var db = new AssetDatabase(Path.Combine(root, AssetDatabase.IndexName));
            next = new LibrarySession(identity, new WorkspaceApi(db, new LocalStorageProvider(root), blender), lease);
            _ = next.Api.Jobs;
            Remember(identity, root, true);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        var previous = Active;
        Active = next;
        previous?.Dispose();
        return next;
    }

    public void RebuildAt(string root)
    {
        lock (_gate)
        {
            RequireAvailable();
            root = Canonical(root);
            using var maintenance = Active?.Api.BeginMaintenance();
            if (Active is {} active && SamePath(active.Root, root))
            {
                active.Api.Database.RebuildIndex();
                return;
            }
            RequireExisting(root);
            using var lease = Lock(root);
            if (!File.Exists(Path.Combine(root, AssetDatabase.IndexName)) && !File.Exists(Path.Combine(root, AssetDatabase.CatalogName)))
                throw new InvalidDataException(DescribeLegacyLoss(root));
            try { _ = new AssetDatabase(Path.Combine(root, AssetDatabase.IndexName), rebuild: true); }
            catch (Exception ex) when (!File.Exists(Path.Combine(root, AssetDatabase.CatalogName)))
            {
                throw new InvalidDataException(DescribeLegacyLoss(root), ex);
            }
        }
    }

    public async Task<LibrarySession> RestoreAsync(string backup, string destination)
    {
        IDisposable? maintenance;
        lock (_gate)
        {
            RequireAvailable();
            maintenance = Active?.Api.BeginMaintenance();
            _restoring = true;
        }
        try
        {
            await Task.Run(() => LibraryMaintenance.Restore(backup, destination));
            lock (_gate)
            {
                try { return OpenCore(Canonical(destination)); }
                catch (Exception ex) { throw new IOException("备份已恢复，请从以下目录重新打开：" + destination + "\n" + ex.Message, ex); }
            }
        }
        finally
        {
            lock (_gate)
            {
                maintenance?.Dispose();
                _restoring = false;
            }
        }
    }

    private void RequireAvailable()
    {
        if (_restoring) throw new InvalidOperationException("正在恢复库，请等待完成后重试。");
    }

    internal static LibraryIdentity NewIdentity(string name) => new(1, Guid.NewGuid().ToString("N"), name);
    internal static string Canonical(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        LocalStorageProvider.RequireLocalPath(root);
        return root;
    }

    internal static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    internal static bool Nested(string left, string right)
    {
        var prefix = Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar;
        return left.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLibrary(string root) =>
        new[] { AssetDatabase.IndexName, AssetDatabase.CatalogName, IdentityName }.Any(name => File.Exists(Path.Combine(root, name)));

    private static void RequireExisting(string root)
    {
        if (File.Exists(Path.Combine(root, LibraryMaintenance.BackupManifestName)))
            throw new InvalidOperationException("此目录是库备份，请使用从备份恢复：" + root);
        if (!HasLibrary(root)) throw new DirectoryNotFoundException("目录没有可用的 Animanager 库：" + root);
    }

    private static string DefaultName(string root) => Path.GetFileName(root) is { Length: > 0 } name ? name : root;

    internal static LibraryIdentity ReadIdentity(string root)
    {
        var path = new LocalStorageProvider(root).Resolve(IdentityName);
        var identity = MotionJson.Read<LibraryIdentity>(path);
        if (identity.Version != 1 || !Guid.TryParseExact(identity.Id, "N", out _) || string.IsNullOrWhiteSpace(identity.Name))
            throw new InvalidDataException("库标识文件损坏：" + path);
        return identity;
    }

    private static LibraryIdentity IdentityForExisting(string root)
    {
        var path = new LocalStorageProvider(root).Resolve(IdentityName);
        var index = Path.Combine(root, AssetDatabase.IndexName);
        var catalog = Path.Combine(root, AssetDatabase.CatalogName);
        if (File.Exists(path))
        {
            if (!File.Exists(catalog)) throw new InvalidDataException("持久资产清单缺失：" + catalog);
            AssetDatabase.ReadCatalog(catalog);
            return ReadIdentity(root);
        }
        if (File.Exists(catalog)) AssetDatabase.ReadCatalog(catalog);
        else
        {
            try { AssetDatabase.ValidateLegacy(index); }
            catch (Exception ex) { throw new InvalidDataException(DescribeLegacyLoss(root), ex); }
            _ = new AssetDatabase(index);
        }
        var identity = NewIdentity(DefaultName(root));
        MotionJson.Atomic(path, identity);
        return identity;
    }

    public static string DescribeLegacyLoss(string root)
    {
        var motion = Path.Combine(root, "motion");
        var sidecars = new List<string>();
        if (Directory.Exists(motion))
        {
            LocalStorageProvider.RequireLocalPath(motion);
            foreach (var path in Directory.EnumerateFiles(motion, "*.motion.json.asset.json"))
            {
                try
                {
                    LocalStorageProvider.RequireLocalPath(path);
                    var asset = MotionJson.Read<AssetItem>(path);
                    if (!string.IsNullOrEmpty(asset.Id) && !string.IsNullOrEmpty(asset.FileHash))
                        sidecars.Add($"{asset.Id} · {asset.Name} · {Path.GetFileName(path)}");
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            }
        }
        return "旧库索引无法读取。缺少元数据副本的资产无法找回原 ID、显示名、标签和收藏。"
            + (sidecars.Count == 0 ? "未找到有效 Motion 元数据副本。" : $"\n找到 {sidecars.Count} 份旧 Motion 元数据副本：\n" + string.Join("\n", sidecars));
    }

    private static FileStream Lock(string root)
    {
        var path = new LocalStorageProvider(root).Resolve(LockName);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("无法独占打开库，可能已由另一进程打开：" + root, ex); }
    }

    private LibraryEntry[] ReadRegistry()
    {
        if (!File.Exists(registryPath)) return [];
        try
        {
            var data = MotionJson.Read<LibraryRegistry>(registryPath);
            if (data.Version != 1 || data.Entries == null ||
                data.Entries.Any(e => e == null || !Guid.TryParseExact(e.Id, "N", out _) || string.IsNullOrWhiteSpace(e.Name) || !Path.IsPathFullyQualified(e.Path)) ||
                data.Entries.Select(e => e.Id).Distinct().Count() != data.Entries.Length)
                throw new InvalidDataException("库登记表损坏：" + registryPath);
            return data.Entries;
        }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException("库登记表损坏：" + registryPath, ex); }
    }

    private void ValidateLocation(LibraryIdentity identity, string root)
    {
        var old = ReadRegistry().SingleOrDefault(e => e.Id == identity.Id);
        if (old != null && !SamePath(old.Path, root))
            throw new InvalidOperationException(HasLibrary(old.Path)
                ? $"该 ID 已登记在 {old.Path}。请通过备份恢复创建独立副本。"
                : $"已登记路径 {old.Path} 失效，请使用重新定位。");
    }

    private LibraryEntry Remember(LibraryIdentity identity, string root, bool opened)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        using var lease = new FileStream(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = ReadRegistry();
        var previous = entries.SingleOrDefault(e => e.Id == identity.Id);
        var entry = new LibraryEntry(identity.Id, identity.Name, root, opened ? DateTime.UtcNow : previous?.LastOpenedUtc);
        if (entry != previous)
            MotionJson.Atomic(registryPath, new LibraryRegistry(1, [.. entries.Where(e => e.Id != entry.Id && !SamePath(e.Path, root)), entry]));
        return entry;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Active?.Dispose();
            Active = null;
        }
    }
}
