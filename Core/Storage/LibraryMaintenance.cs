using System.Security.Cryptography;
using Animanager.Core.Jobs;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Browse;

namespace Animanager.Core.Storage;

public sealed record IntegrityIssue(string AssetId, string Path, string Message);
public sealed record LibraryFile(string Path, long Bytes, string Sha256);
public sealed record BackupManifest(int Version, LibraryIdentity Library, LibraryFile[] Files);
public sealed record CacheFile(string Path, long Bytes, DateTime LastWriteUtc);
public sealed record CachePlan(string Root, CacheFile[] Files, long ConversionBytes, long PersistentBytes)
{
    public long EligibleBytes => Files.Sum(f => f.Bytes);
    public long PreviewDiskBytes { get; init; }
}
public sealed record CleanupResult(int Removed, long Bytes, string[] Retained);

public sealed class LibraryMaintenance(WorkspaceApi api)
{
    internal const string BackupManifestName = "backup-manifest.json";
    private readonly LocalStorageProvider _storage = api.Storage;

    public Task<IntegrityIssue[]> CheckAsync() => ExclusiveAsync(() => Check(api.Database, _storage));

    public Task<string> BackupAsync(string destination) => ExclusiveAsync(() =>
    {
        destination = PrepareDestination(destination, _storage.Root);
        var stage = StageName(destination);
        try
        {
            Directory.CreateDirectory(stage);
            api.Database.CheckIndex();
            var index = Path.Combine(stage, AssetDatabase.IndexName);
            api.Database.BackupIndex(index);
            var eligibleCache = CacheCandidates().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new List<LibraryFile> { Describe(index, AssetDatabase.IndexName) };
            foreach (var (source, relative) in Files(_storage.Root))
            {
                if (RootTransient(relative) || eligibleCache.Contains(relative)) continue;
                var target = new LocalStorageProvider(stage).Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                files.Add(Describe(target, relative));
            }
            var identity = LibraryManager.ReadIdentity(_storage.Root);
            var manifest = new BackupManifest(1, identity, files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray());
            MotionJson.Atomic(Path.Combine(stage, BackupManifestName), manifest);
            ValidateBackup(stage);
            RequireHealthy(stage);
            Directory.Move(stage, destination);
            return destination;
        }
        finally { RemoveStage(stage); }
    });

    internal static void Restore(string backup, string destination)
    {
        backup = LibraryManager.Canonical(backup);
        destination = PrepareDestination(destination, backup);
        var manifest = ValidateBackup(backup);
        var stage = StageName(destination);
        try
        {
            Directory.CreateDirectory(stage);
            var sourceStorage = new LocalStorageProvider(backup);
            var targetStorage = new LocalStorageProvider(stage);
            foreach (var file in manifest.Files)
            {
                var target = targetStorage.Resolve(file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceStorage.Resolve(file.Path), target);
                if (Describe(target, file.Path) != file) throw new IOException("恢复时文件校验失败：" + file.Path);
            }
            RequireHealthy(stage);
            var identity = LibraryManager.NewIdentity(manifest.Library.Name + "（恢复）");
            MotionJson.Atomic(targetStorage.Resolve(LibraryManager.IdentityName), identity);
            Directory.Move(stage, destination);
        }
        finally { RemoveStage(stage); }
    }

    public Task<CachePlan> PreviewCacheAsync() => ExclusiveAsync(PreviewCache);

    public Task<CleanupResult> ClearCacheAsync(CachePlan approved) => ExclusiveAsync(() =>
    {
        if (!LibraryManager.SamePath(approved.Root, _storage.Root))
            throw new InvalidOperationException("已切换库，请重新预览清理范围。");
        var current = CacheCandidates().ToHashSet();
        if (approved.Files.Any(f => !current.Contains(f)))
            throw new InvalidOperationException("缓存范围已变更，请重新预览。");
        var result = RemoveFiles(approved.Files);
        if (approved.Files.Any(f => AssetBrowseCache.IsCacheFile(f.Path))) api.InvalidateAssetBrowseCache();
        return result;
    });

    public Task<CleanupResult> PurgeAsync(string[] ids) => ExclusiveAsync(() =>
    {
        var snapshot = api.Database.Snapshot();
        var selected = snapshot.Assets.Where(a => a.Trashed && ids.Contains(a.Id)).ToArray();
        if (selected.Length == 0 || selected.Length != ids.Distinct().Count())
            throw new InvalidOperationException("永久删除只适用于回收站中的选中资产。");
        var candidates = PurgeCandidates(snapshot, selected, out var retained);
        api.Database.Purge(ids);
        var result = RemoveFiles(candidates);
        return result with { Retained = [.. retained, .. result.Retained] };
    });

    private async Task<T> ExclusiveAsync<T>(Func<T> operation)
    {
        using var maintenance = api.BeginMaintenance();
        return await Task.Run(operation);
    }

    private static IntegrityIssue[] Check(AssetDatabase db, LocalStorageProvider storage)
    {
        var issues = new List<IntegrityIssue>();
        var shared = new Dictionary<string, (long Size, string Hash)>(StringComparer.OrdinalIgnoreCase);
        void Inspect(string id, string path, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                issues.Add(new(id, path, ex.Message));
            }
        }
        Inspect("", AssetDatabase.IndexName, db.CheckIndex);
        Inspect("", AssetDatabase.CatalogName, () =>
        {
            var catalog = AssetDatabase.ReadCatalog(storage.Resolve(AssetDatabase.CatalogName));
            var current = db.Snapshot();
            if (catalog.Revision != current.Revision || !catalog.Assets.SequenceEqual(current.Assets) ||
                catalog.Collections.Length != current.Collections.Length ||
                catalog.Collections.Zip(current.Collections).Any(pair => pair.First.Id != pair.Second.Id ||
                    pair.First.Name != pair.Second.Name || pair.First.ParentId != pair.Second.ParentId ||
                    !pair.First.AssetIds.SequenceEqual(pair.Second.AssetIds) ||
                    !pair.First.LinkedAssets.SequenceEqual(pair.Second.LinkedAssets)))
                throw new InvalidDataException("磁盘上的持久清单与当前会话不一致。");
        });
        foreach (var asset in db.Snapshot().Assets)
        {
            Inspect(asset.Id, asset.VirtualPath, () =>
            {
                var file = storage.Resolve(asset.VirtualPath);
                if (asset.SourceFormat != ".motion")
                {
                    if (new FileInfo(file).Length != asset.Size) throw new InvalidDataException("资产文件大小不符。");
                    VerifyHash(file, asset.FileHash, asset.SourceFormat == ".gltf" ? ImportService.ReadDependencies(file, asset.SourceFormat).Select(d => d.Source) : []);
                    return;
                }

                Inspect(asset.Id, asset.VirtualPath, () => VerifyHash(file, asset.FileHash, []));
                var manifest = MotionJson.ReadManifest(file);
                Inspect(asset.Id, asset.VirtualPath, () =>
                {
                    if (new FileInfo(file).Length + manifest.Objects["body"].Bytes + manifest.Objects["prop"].Bytes != asset.Size)
                        throw new InvalidDataException("Motion 清单及轨道大小不符。");
                });
                foreach (var item in manifest.Objects.Values.DistinctBy(o => (o.Path, o.Sha256, o.Bytes)))
                    Inspect(asset.Id, "motion/" + item.Path, () =>
                    {
                        var path = MotionJson.ResolveObject(file, item, verify: false);
                        if (!shared.TryGetValue(path, out var verified))
                        {
                            verified = (new FileInfo(path).Length, HashFile(path));
                            shared.Add(path, verified);
                        }
                        if (verified.Size != item.Bytes || verified.Hash != item.Sha256)
                            throw new InvalidDataException("Motion 对象大小或 SHA-256 不符。");
                    });
                CheckPresentations(storage, asset, file, manifest, Inspect);
            });
        }
        CheckRetainedMotion(storage, db.Snapshot(), Inspect);
        var retargetFolder = storage.Resolve("retarget");
        if (Directory.Exists(retargetFolder))
            foreach (var file in Directory.GetFiles(retargetFolder, "*.json", SearchOption.AllDirectories))
                Inspect("", Path.GetRelativePath(storage.Root, file), () =>
                {
                    if (Path.GetFileName(Path.GetDirectoryName(file)) == "assets") MotionJson.Read<Retarget.RetargetOptions>(file).Snapshot();
                    else MotionJson.Read<Retarget.RetargetPreset>(file).Options.Snapshot();
                });
        return issues.ToArray();
    }

    private static void CheckPresentations(LocalStorageProvider storage, AssetItem asset, string file, MotionManifest manifest,
        Action<string, string, Action> inspect)
    {
        var current = MotionJson.PresentationPath(file);
        var history = storage.Resolve("motion/presentations/" + Path.GetFileName(file));
        inspect(asset.Id, asset.VirtualPath + ".presentation.json", () =>
        {
            LocalStorageProvider.RequireLocalPath(current);
            var presentation = MotionJson.Presentation(file, manifest);
            if (File.Exists(current) && !File.Exists(Path.Combine(history, presentation.Revision + ".json")))
                throw new InvalidDataException("缺少当前展示配置对应的历史修订：" + presentation.Revision);
        });
        if (!Directory.Exists(history)) return;
        foreach (var revision in Directory.EnumerateFiles(history, "*.json"))
            inspect(asset.Id, Path.GetRelativePath(storage.Root, revision), () =>
            {
                LocalStorageProvider.RequireLocalPath(revision);
                var p = MotionJson.Read<MotionPresentation>(revision);
                MotionJson.ValidatePresentation(p, manifest);
                if (!Guid.TryParseExact(p.Revision, "N", out _) || Path.GetFileNameWithoutExtension(revision) != p.Revision ||
                    p.PreviousRevision != "default" && (!Guid.TryParseExact(p.PreviousRevision, "N", out _) ||
                        p.PreviousRevision == p.Revision || !File.Exists(Path.Combine(history, p.PreviousRevision + ".json"))))
                    throw new InvalidDataException("展示配置修订链不完整。");
            });
    }

    private static void CheckRetainedMotion(LocalStorageProvider storage, AssetCatalog catalog,
        Action<string, string, Action> inspect)
    {
        var directory = storage.Resolve("motion/presentations");
        if (!Directory.Exists(directory)) return;
        var active = catalog.Assets.Select(a => a.VirtualPath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var history in Directory.EnumerateDirectories(directory))
        {
            var relative = "motion/" + Path.GetFileName(history);
            if (active.Contains(relative)) continue;
            inspect("", relative, () =>
            {
                LocalStorageProvider.RequireLocalPath(history);
                if (!Directory.EnumerateFileSystemEntries(history).Any()) return;
                var manifestPath = storage.Resolve(relative);
                var manifest = MotionJson.Load(manifestPath);
                var retained = new AssetItem { Id = "", VirtualPath = relative };
                CheckPresentations(storage, retained, manifestPath, manifest, inspect);
            });
        }
    }

    private static void RequireHealthy(string root)
    {
        var db = new AssetDatabase(Path.Combine(root, AssetDatabase.IndexName), repairProjection: false);
        var issues = Check(db, new LocalStorageProvider(root));
        if (issues.Length > 0)
            throw new InvalidDataException("库校验失败：\n" + string.Join("\n", issues.Take(20).Select(FormatIssue)));
    }

    public static string FormatIssue(IntegrityIssue issue) => $"{issue.AssetId} {issue.Path} · {issue.Message}".Trim();

    private static void VerifyHash(string path, string expected, IEnumerable<string> dependencies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var part in dependencies.Prepend(path))
        {
            using var stream = File.OpenRead(part);
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资产 SHA-256 不符：" + path);
    }

    private CachePlan PreviewCache()
    {
        long conversion = 0, persistent = 0, preview = 0;
        foreach (var (path, relative) in Files(_storage.Root))
        {
            if (RootTransient(relative)) continue;
            long size = new FileInfo(path).Length;
            if (AssetBrowseCache.IsCacheFile(relative)) preview += size;
            else if (relative.StartsWith("cache/conversion/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("uasset/", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith("-report.json", StringComparison.OrdinalIgnoreCase))
                conversion += size;
            else persistent += size;
        }
        return new(_storage.Root, CacheCandidates(), conversion, persistent) { PreviewDiskBytes = preview };
    }

    private CacheFile[] CacheCandidates()
    {
        var assets = api.Database.Snapshot().Assets.ToDictionary(a => a.Id);
        var assetsRoot = _storage.Resolve("assets");
        var motionRoot = _storage.Resolve("motion");
        if (assets.Values.Any(a => !LibraryManager.Nested(_storage.Resolve(a.VirtualPath), a.SourceFormat == ".motion" ? motionRoot : assetsRoot)))
            return [];
        var candidates = new List<CacheFile>();
        var previewRoot = _storage.Resolve(AssetBrowseCache.DirectoryName);
        if (Directory.Exists(previewRoot))
            candidates.AddRange(Files(previewRoot)
                .Where(f => AssetBrowseCache.IsCacheFile(Relative(_storage.Root, f.Full)))
                .Select(f => CacheInfo(f.Full)));
        var reports = api.Jobs.Recent(int.MaxValue).Where(j => j.Kind == JobKind.ImportUasset && j.State == JobState.Completed)
            .SelectMany(j => j.Items).Where(i => i.State is ItemState.Succeeded or ItemState.Skipped).Select(i => i.Output);
        foreach (var report in reports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetFileName(report).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileName(Path.GetDirectoryName(report));
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var receipt = MotionJson.Read<ImportReceipt>(_storage.Resolve($"uasset/{id}/import-report.json"));
                var durableReport = _storage.Resolve($"uasset/{id}/conversion-report.json");
                var conversion = MotionJson.Read<UassetReport>(durableReport);
                if (receipt.Version is not (1 or 2) || receipt.Errors is not { Length: 0 } || conversion.Failed != 0 ||
                    receipt.Items is not { Length: > 0 } || conversion.Items == null)
                    continue;
                var converted = conversion.Items.Where(i => i.Status == "converted").ToArray();
                if (converted.Length != receipt.Items.Length) continue;
                var cache = _storage.Resolve((receipt.Version == 1 ? "uasset/" : "cache/conversion/") + id);
                if (!Directory.Exists(cache) || !receipt.Items.All(item =>
                    (item.Status is "added" or "duplicate") && assets.TryGetValue(item.Id, out var asset) &&
                    asset.FileHash == item.Sha256 && asset.VirtualPath == item.Path &&
                    converted.Any(row => row.Source == item.Source && row.OutputSha256 == item.Sha256) &&
                    Archived(asset, _storage))) continue;

                var objectFolder = Path.Combine(cache, "objects");
                var knownObjects = KnownConversionObjects(cache, assets, receipt);
                foreach (var row in converted)
                {
                    var name = Path.GetFileName(row.Output);
                    if (string.IsNullOrEmpty(name) || !MotionJson.IsMotion(name) && !name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Path.Combine(cache, name);
                    LocalStorageProvider.RequireLocalPath(path);
                    if (File.Exists(path) && HashFile(path) == row.OutputSha256)
                        candidates.Add(CacheInfo(path));
                }
                foreach (var obj in knownObjects.Values)
                {
                    var path = Path.Combine(objectFolder, Path.GetFileName(obj.Path));
                    LocalStorageProvider.RequireLocalPath(path);
                    if (File.Exists(path) && new FileInfo(path).Length == obj.Bytes && HashFile(path) == obj.Sha256)
                        candidates.Add(CacheInfo(path));
                }
                var reportCopy = Path.Combine(cache, "conversion-report.json");
                LocalStorageProvider.RequireLocalPath(reportCopy);
                if (receipt.Version == 2 && File.Exists(reportCopy) && HashFile(reportCopy) == HashFile(durableReport))
                    candidates.Add(CacheInfo(reportCopy));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // An unclassified directory stays available for diagnosis or manual recovery.
            }
        }
        return candidates.Distinct().OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Archived(AssetItem asset, LocalStorageProvider storage)
    {
        var path = storage.Resolve(asset.VirtualPath);
        if (!File.Exists(path)) return false;
        if (asset.SourceFormat != ".motion")
            return new FileInfo(path).Length == asset.Size && HashFile(path) == asset.FileHash;
        if (HashFile(path) != asset.FileHash) return false;
        _ = MotionJson.Load(path);
        return true;
    }

    private Dictionary<string, MotionObject> KnownConversionObjects(string cache, Dictionary<string, AssetItem> assets, ImportReceipt receipt)
    {
        if (!Directory.Exists(Path.Combine(cache, "objects"))) return [];
        var known = new Dictionary<string, MotionObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in receipt.Items)
        {
            var asset = assets[item.Id];
            if (asset.SourceFormat != ".motion") continue;
            var manifest = MotionJson.ReadManifest(_storage.Resolve(asset.VirtualPath));
            foreach (var obj in manifest.Objects.Values)
                known.TryAdd(Path.GetFileName(obj.Path), obj);
        }
        return known;
    }

    private CacheFile[] PurgeCandidates(AssetCatalog snapshot, AssetItem[] selected, out string[] retained)
    {
        var keep = new List<string>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetsMotion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedIds = selected.Select(a => a.Id).ToHashSet();
        var survivors = snapshot.Assets.Where(a => !selectedIds.Contains(a.Id)).ToArray();
        var survivorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var survivor in survivors)
        {
            var path = _storage.Resolve(survivor.VirtualPath);
            survivorPaths.Add(path);
            if (survivor.SourceFormat == ".gltf")
            {
                try
                {
                    foreach (var dependency in ImportService.ReadDependencies(path, survivor.SourceFormat)) survivorPaths.Add(dependency.Source);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    safePaths.Add(Path.GetDirectoryName(path)!);
                }
            }
        }
        foreach (var asset in selected)
        {
            var path = _storage.Resolve(asset.VirtualPath);
            if (asset.SourceFormat == ".motion")
            {
                var history = _storage.Resolve("motion/presentations/" + Path.GetFileName(path));
                if (survivorPaths.Contains(path)) { keep.Add(asset.VirtualPath + " · 仍有索引引用"); continue; }
                if (File.Exists(MotionJson.PresentationPath(path))) targets.Add(MotionJson.PresentationPath(path));
                if (File.Exists(path + ".asset.json")) targets.Add(path + ".asset.json");
                if (Directory.Exists(history) && Directory.EnumerateFileSystemEntries(history).Any())
                {
                    keep.Add(asset.VirtualPath + " · 保留的展示修订仍引用此清单及对象");
                    continue;
                }
                if (File.Exists(path)) targetsMotion.Add(path);
                continue;
            }
            var prefix = _storage.Resolve("assets/" + asset.FileHash);
            if (!LibraryManager.Nested(path, prefix) || safePaths.Contains(prefix) || survivorPaths.Any(p => LibraryManager.Nested(p, prefix)))
            {
                keep.Add(asset.VirtualPath + " · 路径归属无法确认或仍有引用");
                continue;
            }
            if (!File.Exists(path)) continue;
            try
            {
                var files = ImportService.ReadDependencies(path, asset.SourceFormat).Select(d => d.Source).Prepend(path);
                foreach (var file in files.Where(f => LibraryManager.Nested(f, prefix))) targets.Add(file);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                keep.Add(asset.VirtualPath + " · 无法确认依赖：" + ex.Message);
            }
        }
        if (targetsMotion.Count > 0)
        {
            var motion = _storage.Resolve("motion");
            var unreferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in targetsMotion)
                    foreach (var reference in MotionJson.ReadManifest(path).Objects.Values)
                        unreferenced.Add(MotionJson.ResolveObject(path, reference, verify: false));
                var remainingManifests = survivors.Where(a => a.SourceFormat == ".motion").Select(a => _storage.Resolve(a.VirtualPath))
                    .Concat(Directory.EnumerateFiles(motion, "*.motion.json")).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var path in remainingManifests.Where(p => !targetsMotion.Contains(p)))
                {
                    LocalStorageProvider.RequireLocalPath(path);
                    foreach (var reference in MotionJson.ReadManifest(path).Objects.Values)
                        unreferenced.Remove(_storage.Resolve("motion/" + reference.Path));
                }
                foreach (var history in Directory.EnumerateDirectories(motion, "presentations").SelectMany(Directory.EnumerateDirectories))
                    if (!File.Exists(_storage.Resolve("motion/" + Path.GetFileName(history))))
                        throw new IOException("存在缺少清单的保留修订。");
                targets.UnionWith(unreferenced);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
            {
                keep.Add("motion/objects · 引用扫描未完成，保留对象：" + ex.Message);
            }
            targets.UnionWith(targetsMotion);
        }
        retained = keep.ToArray();
        return targets.Where(path => !survivorPaths.Contains(path) && File.Exists(path)).Select(CacheInfo)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private CacheFile CacheInfo(string path)
    {
        LocalStorageProvider.RequireLocalPath(path);
        var info = new FileInfo(path);
        return new(Relative(_storage.Root, path), info.Length, info.LastWriteTimeUtc);
    }

    private CleanupResult RemoveFiles(IEnumerable<CacheFile> files)
    {
        int count = 0;
        long size = 0;
        var retained = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var path = _storage.Resolve(file.Path);
                if (!File.Exists(path)) continue;
                if (CacheInfo(path) != file) throw new IOException("文件已变化");
                File.Delete(path);
                count++;
                size += file.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                retained.Add(file.Path + " · " + ex.Message);
            }
        }
        return new(count, size, retained.ToArray());
    }

    private static string PrepareDestination(string destination, string source)
    {
        destination = LibraryManager.Canonical(destination);
        if (LibraryManager.SamePath(destination, source) || LibraryManager.Nested(destination, source) || LibraryManager.Nested(source, destination) ||
            File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("请选择库外尚不存在的全新目录：" + destination);
        for (var parent = Path.GetDirectoryName(destination); parent != null; parent = Path.GetDirectoryName(parent))
            if (LibraryManager.HasLibrary(parent))
                throw new IOException("目标目录不能位于其他库内：" + parent);
        return destination;
    }

    private static string StageName(string destination) => destination + ".animanager-staging-" + Guid.NewGuid().ToString("N");

    private static void RemoveStage(string stage)
    {
        if (!Directory.Exists(stage)) return;
        LocalStorageProvider.RequireLocalPath(stage);
        foreach (var entry in Files(stage)) LocalStorageProvider.RequireLocalPath(entry.Full);
        Directory.Delete(stage, recursive: true);
    }

    private static BackupManifest ValidateBackup(string root)
    {
        var storage = new LocalStorageProvider(root);
        var manifest = MotionJson.Read<BackupManifest>(storage.Resolve(BackupManifestName));
        if (manifest.Version != 1 || manifest.Files == null || manifest.Library != LibraryManager.ReadIdentity(root))
            throw new InvalidDataException("无法验证备份标识。");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
            if (file == null || RootTransient(file.Path) && file.Path != AssetDatabase.IndexName || !expected.Add(file.Path) ||
                !File.Exists(storage.Resolve(file.Path)) || Describe(storage.Resolve(file.Path), file.Path) != file)
                throw new InvalidDataException("备份文件缺失或校验失败：" + file?.Path);
        foreach (var required in new[] { AssetDatabase.IndexName, AssetDatabase.CatalogName, LibraryManager.IdentityName })
            if (!expected.Contains(required)) throw new InvalidDataException("备份缺少必要文件：" + required);
        foreach (var (_, relative) in Files(root))
            if (relative != BackupManifestName && !expected.Contains(relative))
                throw new InvalidDataException("备份包含清单外的文件：" + relative);
        return manifest;
    }

    private static IEnumerable<(string Full, string Relative)> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            LocalStorageProvider.RequireLocalPath(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) == 0)
            {
                yield return (entry, Relative(root, entry));
                continue;
            }
            foreach (var (file, _) in Files(entry)) yield return (file, Relative(root, file));
        }
    }

    private static bool RootTransient(string relative) =>
        relative is LibraryManager.LockName or BackupManifestName or AssetDatabase.IndexName or "index.sqlite-wal" or "index.sqlite-shm" or "index.sqlite-journal";

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static LibraryFile Describe(string path, string relative) => new(relative, new FileInfo(path).Length, HashFile(path));
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record Imported(string Source, string Id, string Path, string Sha256, string Status);
    private sealed record ImportReceipt(int Version, string[] Errors, Imported[] Items);
}
