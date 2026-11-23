using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.Core.Storage;

namespace Animanager.UI;

public partial class MainShell
{
    private sealed class LibraryPreviewWarmup(LibrarySession library)
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal LibrarySession Library { get; } = library;
        internal AssetItem? Asset;
        internal Task<PreloadedSource?> Source = null!;
    }
    private sealed record PreloadedSource(SceneAsset.ParsedScene? Scene, MotionPlayback.SourceData? Motion, RigCatalog.FileStamp Stamp)
        : IDisposable
    {
        public void Dispose() => Scene?.Dispose();
    }

    private LibraryPreviewWarmup? _libraryPreviewWarmup;
    private Task _previewWarmupCleanup = Task.CompletedTask;
    private readonly RetargetTargetCache _previewTargets = new();

    private void QueueLibraryPreviewWarmup(LibrarySession library)
    {
        _ = StopLibraryPreviewWarmupAsync();
        if (_closing) return;
        var warmup = new LibraryPreviewWarmup(library);
        _libraryPreviewWarmup = warmup;
        var target = _characterEntries.FirstOrDefault(c => c.Id == _previewCharacterId && c.Compatible);
        _preferences.RecentPreviewAssets.TryGetValue(library.Identity.Id, out var recent);
        warmup.Source = PrepareLibraryPreviewAsync(warmup, target, recent);
    }

    private async Task<PreloadedSource?> PrepareLibraryPreviewAsync(
        LibraryPreviewWarmup warmup, CharacterEntry? target, string? recent)
    {
        var ct = warmup.Cancellation.Token;
        try
        {
            // Give the browser time to draw and accept input before starting speculative work.
            await Task.Delay(200, ct);
            await Task.WhenAll(PendingAssetPreview, PendingRetargetPreview).WaitAsync(ct);
            var api = warmup.Library.Api;
            AssetItem? candidate;
            using (api.BeginActivity())
            {
                candidate = await Task.Run(() =>
                {
                    var last = recent == null ? null : api.Database.Find(recent);
                    return last != null && CanPreload(last) ? last
                        : api.Database.Search(category: "Animation", pageSize: 12).Items.FirstOrDefault(CanPreload);
                }, ct);
            }
            ct.ThrowIfCancellationRequested();
            warmup.Asset = candidate;
            PreloadedSource? source = null;
            if (candidate != null)
            {
                using var activity = api.BeginActivity();
                string path = await api.Storage.AcquireLocalFileAsync(candidate.VirtualPath, ct);
                if (MotionJson.IsMotion(path))
                    source = new(null, await Task.Run(() => MotionPlayback.ReadSource(path, true, ct), ct), default);
                else
                {
                    var stamp = RigCatalog.FileStamp.Read(path);
                    source = new(await SceneAsset.ParsePreviewAsync(path, ct), null, stamp);
                }
            }
            // Return the source promptly when a click joins its preload. The target task is shared as well.
            if (target != null && !ct.IsCancellationRequested && target.Id == _previewCharacterId)
                _ = WarmLibraryRetargetAsync(warmup, target, ct);
            return source;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }

        static bool CanPreload(AssetItem candidate) => candidate.Size <= 64 * 1024 * 1024 &&
            (MotionJson.IsMotion(candidate.VirtualPath) || Path.GetExtension(candidate.VirtualPath).ToLowerInvariant() is ".fbx" or ".glb");
    }

    private async Task WarmPreviewTargetAsync(CharacterEntry target)
    {
        try { await _previewTargets.WarmAsync(target.Options().Snapshot()); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    private async Task WarmLibraryRetargetAsync(LibraryPreviewWarmup warmup, CharacterEntry target, CancellationToken ct)
    {
        await WarmPreviewTargetAsync(target);
        try
        {
            await RigCatalog.WarmReferencesAsync(() => _libraryPreviewWarmup == warmup && !_closing &&
                PendingAssetPreview.IsCompleted && PendingRetargetPreview.IsCompleted, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    private async Task<PreloadedSource?> TakePreloadedSourceAsync(LibrarySession library, AssetItem asset,
        string path, CancellationToken ct)
    {
        var warmup = _libraryPreviewWarmup;
        if (warmup?.Library != library || warmup.Asset?.VirtualPath != asset.VirtualPath)
        {
            _ = StopLibraryPreviewWarmupAsync();
            return null;
        }
        _libraryPreviewWarmup = null;
        using var cancellation = warmup.Cancellation;
        using var registration = ct.Register(cancellation.Cancel);
        var result = await warmup.Source;
        if (result is not { } prepared) return null;
        bool transfer = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            transfer = prepared.Motion != null || RigCatalog.FileStamp.Read(path) == prepared.Stamp;
            return transfer ? prepared : null;
        }
        finally { if (!transfer) prepared.Dispose(); }
    }

    private Task StopLibraryPreviewWarmupAsync()
    {
        var warmup = _libraryPreviewWarmup;
        _libraryPreviewWarmup = null;
        if (warmup != null)
            _previewWarmupCleanup = Task.WhenAll(_previewWarmupCleanup, DiscardAsync(warmup));
        return _previewWarmupCleanup;
    }

    private static async Task DiscardAsync(LibraryPreviewWarmup warmup)
    {
        using var cancellation = warmup.Cancellation;
        cancellation.Cancel();
        (await warmup.Source)?.Dispose();
    }

    private void RememberPreviewAsset(LibrarySession library, AssetItem asset)
    {
        var recent = _preferences.RecentPreviewAssets;
        if (recent.GetValueOrDefault(library.Identity.Id) == asset.Id) return;
        recent.Remove(library.Identity.Id);
        recent.Add(library.Identity.Id, asset.Id);
        if (recent.Count > 32) recent.Remove(recent.Keys.First());
        SaveSoon();
    }
}
