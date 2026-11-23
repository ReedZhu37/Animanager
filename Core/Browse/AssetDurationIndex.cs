using Animanager.Core.Models;
using Animanager.Core.Storage;

namespace Animanager.Core.Browse;

internal sealed class AssetDurationIndex : IDisposable
{
    private readonly WorkspaceApi _api;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly object _changesGate = new();
    private readonly CancellationTokenSource _closing = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, (string Hash, double? Seconds)> _verified = new(PathComparer);
    private HashSet<string>? _changedPaths = new(PathComparer);
    private Dictionary<string, StoredDuration> _stored = [];
    private string? _catalogRevision;
    private int _revision;
    private int _completed, _total;
    internal (int Completed, int Total) Progress => (Volatile.Read(ref _completed), Volatile.Read(ref _total));
    // Without change notifications revalidate at the next request.
    internal int Revision => _watcher?.EnableRaisingEvents == true ? Volatile.Read(ref _revision) : Interlocked.Increment(ref _revision);

    internal AssetDurationIndex(WorkspaceApi api)
    {
        _api = api;
        try
        {
            _watcher = new FileSystemWatcher(api.Storage.Root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName };
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += (_, e) => { Changed(null, e); SourceChanged(e.OldName); };
            _watcher.Error += (_, _) => Invalidate();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void Changed(object? sender, FileSystemEventArgs e) => SourceChanged(e.Name);

    private void SourceChanged(string? name)
    {
        if (name == null) { Invalidate(); return; }
        var path = name.Replace('\\', '/');
        if (Path.GetExtension(path).ToLowerInvariant() is ".fbx" or ".glb" or ".gltf" || Motion.MotionJson.IsMotion(path))
            Invalidate(path);
        else if (path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("motion/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("assets", StringComparison.OrdinalIgnoreCase) || path.Equals("motion", StringComparison.OrdinalIgnoreCase))
            Invalidate();
    }

    internal void Invalidate(string? path = null)
    {
        lock (_changesGate)
        {
            // Directory moves and notification overflows require a full stamp check.
            if (path == null || _changedPaths?.Count >= 1024) _changedPaths = null;
            else _changedPaths?.Add(path);
            Interlocked.Increment(ref _revision);
        }
    }

    internal void Warm()
    {
        var ct = _closing.Token;
        _ = Task.Run(() =>
        {
            try
            {
                var assets = _api.Database.Snapshot().Assets.Where(asset => !asset.Trashed).ToArray();
                Volatile.Write(ref _total, assets.Length);
                foreach (var batch in assets.Chunk(128))
                {
                    Read(batch, ct);
                    Interlocked.Add(ref _completed, batch.Length);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Duration index warm-up: " + ex.Message); }
            finally { Volatile.Write(ref _total, Volatile.Read(ref _completed)); }
        });
    }

    internal double?[] Read(IReadOnlyList<AssetItem> assets, CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            lock (_changesGate)
            {
                if (_changedPaths == null || _watcher?.EnableRaisingEvents != true)
                {
                    _verified.Clear();
                    _changedPaths = new(PathComparer);
                }
                else
                {
                    foreach (var path in _changedPaths) _verified.Remove(path);
                    _changedPaths.Clear();
                }
            }
            string catalog = _api.Database.CatalogRevision;
            if (catalog != _catalogRevision)
            {
                _stored = _api.Database.ReadDurations();
                _catalogRevision = catalog;
            }
            var changed = new List<StoredDuration>();
            var result = new double?[assets.Count];
            for (int i = 0; i < assets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var asset = assets[i];
                string path = asset.VirtualPath.Replace('\\', '/');
                if (_verified.TryGetValue(path, out var timing) && timing.Hash == asset.FileHash)
                    result[i] = timing.Seconds;
                else result[i] = ReadAsset(asset, path, ct, changed);
                if (changed.Count >= 128) Flush();
            }
            Flush();
            return result;

            void Flush()
            {
                if (changed.Count == 0) return;
                if (_api.TryWriteBrowseCache(() => _api.Database.SaveDurations(changed)))
                    foreach (var entry in changed) _stored[entry.Hash] = entry;
                changed.Clear();
            }
        }
    }

    private double? ReadAsset(AssetItem asset, string relative, CancellationToken ct, List<StoredDuration> changed)
    {
        try
        {
            var path = _api.Storage.Resolve(relative);
            var file = new FileInfo(path);
            if (_stored.TryGetValue(asset.FileHash, out var entry) && file.Exists &&
                file.Length == entry.Size && file.LastWriteTimeUtc.Ticks == entry.Modified)
            {
                _verified[relative] = (asset.FileHash, entry.Seconds);
                return entry.Seconds;
            }
            entry = StoredDuration.Inspect(asset.FileHash, path, () => _api.ReadSourceDuration(asset, ct));
            if (entry != null) changed.Add(entry);
            return entry?.Seconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _watcher?.Dispose();
    }
}
