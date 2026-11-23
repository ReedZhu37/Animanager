using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animanager.Core.Storage;

public sealed record LibraryIdentity(int Version, string Id, string Name);
public sealed record LibraryEntry(string Id, string Name, string Path, DateTime? LastOpenedUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LibraryTag[]? Tags { get; init; }
}
internal sealed record LibraryStartup(LibrarySession? Session, bool Bootstrap, string Message);
internal sealed record LibraryRegistry(int Version, LibraryEntry[] Entries)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LibraryTag[]? TagCatalog { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssetTagGroup[]? TagGroups { get; init; }
}

public sealed class LibrarySession(LibraryIdentity identity, WorkspaceApi api, FileStream lease) : IDisposable
{
    public LibraryIdentity Identity { get; internal set; } = identity;
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
    private const int RegistryVersion = 2;
    public const string IdentityName = "library.json";
    internal const string LockName = ".library.lock";
    private readonly object _gate = new();
    private readonly Dictionary<string, LibrarySession> _browseLibraries = new(StringComparer.Ordinal);
    private bool _restoring, _loading, _disposed;
    public LibrarySession? Active { get; private set; }

    internal LibrarySession[] BrowseLibraries()
    {
        lock (_gate) return _browseLibraries.Values.ToArray();
    }

    internal LibrarySession[] OpenLibraries()
    {
        lock (_gate) return Active == null ? [] : [Active, .. _browseLibraries.Values];
    }

    internal LibrarySession? FindOpenLibrary(string id)
    {
        lock (_gate) return Active?.Identity.Id == id ? Active : _browseLibraries.GetValueOrDefault(id);
    }

    internal LibrarySession? FindBrowseLibrary(string id)
    {
        lock (_gate) return _browseLibraries.GetValueOrDefault(id);
    }

    internal void CloseLibrary(string id)
    {
        lock (_gate)
        {
            RequireAvailable();
            if (FindOpenLibrary(id) is not { } library) return;
            using var transition = library.Api.BeginMaintenance();
            _browseLibraries.Remove(id);
            if (library == Active)
            {
                Active = _browseLibraries.Values.FirstOrDefault();
                if (Active != null) _browseLibraries.Remove(Active.Identity.Id);
            }
            library.Dispose();
        }
    }

    internal async Task<LibraryStartup> OpenStartupAsync(string? explicitRoot, params string[] defaults)
    {
        var errors = new List<string>();
        async Task<LibrarySession?> TryOpen(string root, bool create)
        {
            try { return await OpenAsync(root, createIfMissing: create); }
            catch (Exception ex) { errors.Add(ex.Message); return null; }
        }
        LibraryStartup Result(LibrarySession? session, bool bootstrap = false) => new(session, bootstrap, string.Join("\n", errors));
        if (explicitRoot != null) return Result(await TryOpen(explicitRoot, true));

        LibraryEntry[] entries;
        try { entries = Entries(); }
        catch (Exception ex) { errors.Add(ex.Message); entries = []; }
        foreach (var entry in entries.Where(e => e.LastOpenedUtc != null))
            if (await TryOpen(entry.Path, false) is { } session) return Result(session);

        foreach (var root in defaults.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool bootstrap = !HasLibrary(root);
            if (await TryOpen(root, bootstrap) is { } session) return Result(session, bootstrap);
        }
        return Result(null);
    }

    public LibraryEntry[] Entries()
    {
        lock (_gate) return SortEntries(ReadRegistry().Entries);
    }

    private static LibraryEntry[] SortEntries(LibraryEntry[] entries) => entries.OrderByDescending(entry => entry.LastOpenedUtc)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public LibraryEntry Create(string root, string name)
    {
        lock (_gate)
        {
            RequireAvailable();
            return CreateCore(Canonical(root), name);
        }
    }

    private LibraryEntry CreateCore(string root, string name)
    {
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

    public LibraryEntry Register(string root)
    {
        lock (_gate)
        {
            RequireAvailable();
            return RegisterCore(root);
        }
    }

    internal Task<LibraryEntry> RegisterAsync(string root) => RunLoadAsync(() => Task.Run(() => RegisterCore(root)));

    private LibraryEntry RegisterCore(string root)
    {
        root = Canonical(root);
        var open = OpenLibraries().FirstOrDefault(library => SamePath(library.Root, root));
        if (open != null) return Remember(open.Identity, root, false);
        RequireExisting(root);
        using var lease = Lock(root);
        var identity = IdentityForExisting(root);
        ValidateLocation(identity, root);
        return Remember(identity, root, false);
    }

    public LibraryEntry Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) throw new ArgumentException("请输入有效的库名称。");
        lock (_gate)
        {
            RequireAvailable();
            var entry = ReadRegistry().Entries.SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException("没有此库登记：" + id);
            string root = Canonical(entry.Path);
            var session = Active?.Identity.Id == id ? Active : _browseLibraries.GetValueOrDefault(id);
            if (session != null && !SamePath(session.Root, root))
                throw new InvalidOperationException("已打开的库与登记目录不一致，请重新打开该库。");

            RequireExisting(root);
            using var lease = session == null ? Lock(root) : null;
            using var registryLease = LockRegistry();
            var registry = ReadRegistry();
            var entries = registry.Entries;
            int index = Array.FindIndex(entries, e => e.Id == id && SamePath(e.Path, root));
            if (index < 0) throw new InvalidOperationException("库登记已变化，请重新选择该库。");

            var identity = ReadIdentity(root);
            if (identity.Id != id) throw new InvalidDataException("所选目录的库 ID 与登记记录不同。");
            var renamed = identity with { Name = name };
            string identityPath = new LocalStorageProvider(root).Resolve(IdentityName);
            using var maintenance = renamed != identity ? session?.Api.BeginMaintenance() : null;
            if (renamed != identity) MotionJson.Atomic(identityPath, renamed);
            var result = entries[index] with { Name = name };
            try
            {
                if (result != entries[index])
                {
                    entries[index] = result;
                    MotionJson.Atomic(registryPath, registry);
                }
            }
            catch
            {
                if (renamed != identity) MotionJson.Atomic(identityPath, identity);
                throw;
            }
            if (session != null) session.Identity = renamed;
            return result;
        }
    }

    public LibraryEntry Relocate(string id, string root)
    {
        lock (_gate)
        {
            RequireAvailable();
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

    internal (LibraryEntry[] Entries, LibraryTag[] Tags, AssetTagGroup[] Groups) BrowseCatalog()
    {
        lock (_gate)
        {
            var registry = ReadRegistry();
            return (SortEntries(registry.Entries), CatalogTags(registry), registry.TagGroups ?? []);
        }
    }

    internal void CreateTag(string name)
    {
        var tag = new LibraryTag(name.Trim());
        if (!tag.IsValid) throw new ArgumentException("请输入有效的标签名称。");
        lock (_gate)
        {
            RequireAvailable();
            using var lease = LockRegistry();
            var registry = ReadRegistry();
            var tags = CatalogTags(registry);
            if (tags.Contains(tag, LibraryTag.Comparer)) throw new InvalidOperationException("已经有同名标签。");
            MotionJson.Atomic(registryPath, registry with { TagCatalog = [.. tags, tag] });
        }
    }

    internal void SaveTagGroup(string? id, string name, IEnumerable<string> tags)
    {
        name = name.Trim();
        if (!new LibraryTag(name).IsValid) throw new ArgumentException("请输入有效的标签组名称。");
        var comparer = StringComparer.OrdinalIgnoreCase;
        lock (_gate)
        {
            RequireAvailable();
            using var lease = LockRegistry();
            var registry = ReadRegistry();
            var groups = registry.TagGroups ?? [];
            if (id != null && !groups.Any(group => group.Id == id)) throw new KeyNotFoundException("找不到该标签组。");
            if (groups.Any(group => group.Id != id && comparer.Equals(group.Name, name)))
                throw new ArgumentException("已有同名标签组：" + name);
            var known = CatalogTags(registry).ToDictionary(tag => tag.Name, comparer);
            var members = tags.Distinct(comparer).Select(tag => known.TryGetValue(tag, out var found) ? found.Name :
                throw new ArgumentException("找不到库标签：" + tag)).Order(comparer).ToArray();
            var group = new AssetTagGroup(id ?? Guid.NewGuid().ToString("N"), name, members);
            AssetTagGroup[] next = id == null ? [.. groups, group] : groups.Select(item => item.Id == id ? group : item).ToArray();
            MotionJson.Atomic(registryPath, registry with { TagGroups = next });
        }
    }

    internal void DeleteTagGroup(string id)
    {
        lock (_gate)
        {
            RequireAvailable();
            using var lease = LockRegistry();
            var registry = ReadRegistry();
            var groups = (registry.TagGroups ?? []).Where(group => group.Id != id).ToArray();
            MotionJson.Atomic(registryPath, registry with { TagGroups = groups.Length == 0 ? null : groups });
        }
    }

    internal LibraryEntry ChangeTag(string id, LibraryTag tag, bool add)
    {
        lock (_gate)
        {
            RequireAvailable();
            using var lease = LockRegistry();
            var registry = ReadRegistry();
            int index = Array.FindIndex(registry.Entries, entry => entry.Id == id);
            if (index < 0) throw new KeyNotFoundException("没有此库登记：" + id);
            var entry = registry.Entries[index];
            var tags = (entry.Tags ?? []).ToHashSet(LibraryTag.Comparer);
            if (tags.Contains(tag) == add) return entry;
            var knownTags = CatalogTags(registry);
            if (add)
            {
                var known = knownTags.FirstOrDefault(candidate => LibraryTag.Comparer.Equals(candidate, tag))
                    ?? throw new InvalidOperationException("库标签不存在。");
                tags.Add(known);
            }
            else tags.Remove(tag);
            entry = entry with { Tags = tags.Count == 0 ? null : tags.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
            registry.Entries[index] = entry;
            MotionJson.Atomic(registryPath, registry with { TagCatalog = knownTags });
            return entry;
        }
    }

    private static LibraryTag[] CatalogTags(LibraryRegistry registry) => (registry.TagCatalog ?? [])
        .Union(registry.Entries.SelectMany(entry => entry.Tags ?? []), LibraryTag.Comparer).ToArray();

    public LibrarySession OpenById(string id) => Open(Entries().SingleOrDefault(e => e.Id == id)?.Path
        ?? throw new KeyNotFoundException("没有此库登记：" + id));

    internal Task<LibrarySession> OpenByIdAsync(string id) => OpenAsync(Entries().SingleOrDefault(e => e.Id == id)?.Path
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
        if (_browseLibraries.Values.FirstOrDefault(library => SamePath(library.Root, root)) is { } loaded)
        {
            _ = loaded.Api.Jobs;
            Remember(loaded.Identity, root, true);
            return SetActiveLibrary(loaded, alreadyBrowsing: true);
        }

        if (!HasLibrary(root) && createIfMissing) Create(root, DefaultName(root));
        return SetActiveLibrary(OpenSession(root), alreadyBrowsing: false);
    }

    private LibrarySession SetActiveLibrary(LibrarySession next, bool alreadyBrowsing)
    {
        var previous = Active;
        if (alreadyBrowsing)
        {
            _browseLibraries.Remove(next.Identity.Id);
            if (previous != null) _browseLibraries.Add(previous.Identity.Id, previous);
        }
        Active = next;
        if (!alreadyBrowsing) previous?.Dispose();
        return next;
    }

    internal Task<LibrarySession> OpenAsync(string root, bool browse = false, string? newName = null,
        bool createIfMissing = false, string? expectedId = null) => RunLoadAsync(async () =>
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var active = Active;
        if (newName == null && active != null && SamePath(active.Root, root))
        {
            RequireIdentity(active.Identity, expectedId);
            return active;
        }
        var mounted = newName == null ? BrowseLibraries().FirstOrDefault(library => SamePath(library.Root, root)) : null;
        if (mounted != null)
        {
            RequireIdentity(mounted.Identity, expectedId);
            if (browse) return mounted;
        }

        using var transition = browse ? null : active?.Api.BeginMaintenance();
        // Disk work runs without the manager lock. The current sessions remain available to readers.
        var next = await Task.Run(() =>
        {
            root = Canonical(root);
            if (mounted != null)
            {
                _ = mounted.Api.Jobs;
                Remember(mounted.Identity, root, true);
                return mounted;
            }
            if (newName != null || createIfMissing && !HasLibrary(root)) CreateCore(root, newName ?? DefaultName(root));
            return OpenSession(root, expectedId);
        });
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return browse ? AddOpenLibrary(next) : SetActiveLibrary(next, mounted != null);
            }
        }
        catch
        {
            if (mounted == null) next.Dispose();
            throw;
        }
    });

    private async Task<T> RunLoadAsync<T>(Func<Task<T>> load)
    {
        lock (_gate)
        {
            RequireAvailable();
            _loading = true;
        }
        try { return await load(); }
        finally { lock (_gate) _loading = false; }
    }

    private LibrarySession AddOpenLibrary(LibrarySession session)
    {
        if (Active == null) Active = session;
        else _browseLibraries.Add(session.Identity.Id, session);
        return session;
    }

    private LibrarySession OpenSession(string root, string? expectedId = null)
    {
        RequireExisting(root);
        var lease = Lock(root);
        try
        {
            var identity = IdentityForExisting(root, expectedId);
            ValidateLocation(identity, root);
            var db = new AssetDatabase(Path.Combine(root, AssetDatabase.IndexName));
            var session = new LibrarySession(identity, new WorkspaceApi(db, new LocalStorageProvider(root), blender), lease);
            _ = session.Api.Jobs;
            Remember(identity, root, true);
            session.Api.DurationIndex.Warm();
            return session;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void RequireIdentity(LibraryIdentity identity, string? expectedId)
    {
        if (expectedId != null && identity.Id != expectedId)
            throw new InvalidDataException("所选目录与集合中的来源库不匹配。");
    }

    public void RebuildAt(string root)
    {
        lock (_gate)
        {
            RequireAvailable();
            root = Canonical(root);
            if (OpenLibraries().FirstOrDefault(library => SamePath(library.Root, root)) is { } open)
            {
                using var maintenance = open.Api.BeginMaintenance();
                open.Api.Database.RebuildIndex();
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

    public async Task<LibrarySession> RestoreAsync(string backup, string destination, bool browse = false)
    {
        IDisposable? maintenance;
        lock (_gate)
        {
            RequireAvailable();
            maintenance = browse ? null : Active?.Api.BeginMaintenance();
            _restoring = true;
        }
        try
        {
            await Task.Run(() => LibraryMaintenance.Restore(backup, destination));
            lock (_gate)
            {
                try { return browse ? AddOpenLibrary(OpenSession(Canonical(destination))) : OpenCore(Canonical(destination)); }
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loading) throw new InvalidOperationException("正在加载资产库，请等待完成后重试。");
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

    private static LibraryIdentity IdentityForExisting(string root, string? expectedId = null)
    {
        var path = new LocalStorageProvider(root).Resolve(IdentityName);
        var index = Path.Combine(root, AssetDatabase.IndexName);
        var catalog = Path.Combine(root, AssetDatabase.CatalogName);
        if (File.Exists(path) || expectedId != null)
        {
            if (!File.Exists(catalog)) throw new InvalidDataException("持久资产清单缺失：" + catalog);
            AssetDatabase.ReadCatalog(catalog);
            var existing = ReadIdentity(root);
            RequireIdentity(existing, expectedId);
            return existing;
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

    private LibraryRegistry ReadRegistry()
    {
        if (!File.Exists(registryPath)) return new LibraryRegistry(RegistryVersion, []);
        try
        {
            using var file = new FileStream(registryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var data = JsonSerializer.Deserialize<LibraryRegistry>(file, MotionJson.Options);
            if (data == null || data.Version is not (1 or RegistryVersion) || data.Entries == null ||
                data.Entries.Any(e => e == null || !Guid.TryParseExact(e.Id, "N", out _) || string.IsNullOrWhiteSpace(e.Name) || !Path.IsPathFullyQualified(e.Path)) ||
                !ValidTags(data.TagCatalog) || data.Entries.Any(e => !ValidTags(e.Tags)) ||
                data.Entries.Select(e => e.Id).Distinct().Count() != data.Entries.Length)
                throw new InvalidDataException("库登记表损坏：" + registryPath);
            if (!ValidTagGroups(data)) throw new InvalidDataException("库标签组损坏：" + registryPath);
            if (data.Version == RegistryVersion) return data;
            return data with { Version = RegistryVersion, TagCatalog = CatalogTags(data), Entries = data.Entries.Select(entry =>
                entry.Tags == null ? entry : entry with { Tags = entry.Tags.Distinct(LibraryTag.Comparer).ToArray() }).ToArray() };
        }
        catch (JsonException ex) { throw new InvalidDataException("库登记表损坏：" + registryPath, ex); }
    }

    private static bool ValidTags(LibraryTag[]? tags) => tags == null ||
        tags.All(tag => tag != null && tag.IsValid);

    private static bool ValidTagGroups(LibraryRegistry registry)
    {
        if (registry.TagGroups is not { Length: > 0 } groups) return true;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var known = CatalogTags(registry).Select(tag => tag.Name).ToHashSet(comparer);
        var ids = new HashSet<string>(comparer);
        var names = new HashSet<string>(comparer);
        return groups.All(group => group != null && Guid.TryParseExact(group.Id, "N", out _) && ids.Add(group.Id) &&
            new LibraryTag(group.Name).IsValid && names.Add(group.Name) && group.Tags != null &&
            group.Tags.All(known.Contains) && group.Tags.Distinct(comparer).Count() == group.Tags.Length);
    }

    private void ValidateLocation(LibraryIdentity identity, string root)
    {
        var old = ReadRegistry().Entries.SingleOrDefault(e => e.Id == identity.Id);
        if (old != null && !SamePath(old.Path, root))
            throw new InvalidOperationException(HasLibrary(old.Path)
                ? $"该 ID 已登记在 {old.Path}。请通过备份恢复创建独立副本。"
                : $"已登记路径 {old.Path} 失效，请使用重新定位。");
    }

    private FileStream LockRegistry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        return new FileStream(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private LibraryEntry Remember(LibraryIdentity identity, string root, bool opened)
    {
        using var lease = LockRegistry();
        var registry = ReadRegistry();
        var entries = registry.Entries;
        var previous = entries.SingleOrDefault(e => e.Id == identity.Id);
        var entry = new LibraryEntry(identity.Id, identity.Name, root, opened ? DateTime.UtcNow : previous?.LastOpenedUtc)
        {
            Tags = previous?.Tags
        };
        if (entry != previous)
            MotionJson.Atomic(registryPath, registry with { Entries = [.. entries.Where(e => e.Id != entry.Id && !SamePath(e.Path, root)), entry] });
        return entry;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Active?.Dispose();
            Active = null;
            foreach (var library in _browseLibraries.Values) library.Dispose();
            _browseLibraries.Clear();
        }
    }
}
