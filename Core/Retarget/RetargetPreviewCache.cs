using System.Text.Json;
using Animanager.Core.Motion;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Core.Retarget;

/// <summary>Small, session-only cache; every viewport receives its own nodes and animation resources.</summary>
internal sealed class RetargetPreviewCache : IDisposable
{
    private const int MaxEntries = 3;
    private const long MaxCachedKeys = 2_500_000;
    internal sealed record Preview(RetargetResult Result, RetargetOptions Options, AssetReadiness? Readiness, double SourceFps);
    private readonly LinkedList<(string Key, Preview Preview, long Keys)> _entries = new();
    internal int Hits { get; private set; }
    internal static async Task<string?> KeyAsync(string path, RetargetOptions options, CancellationToken ct,
        MotionPlayback.SourceData? decodedMotion = null)
    {
        // Motion assets are cacheable after their full closure has been verified, including the mutable presentation.
        // A glTF can have external dependencies that its file hash does not cover.
        return await Task.Run(() =>
        {
            options.Character?.VerifiedPath();
            string? revision = decodedMotion != null
                ? MotionJson.Hash(MotionJson.Bytes(new { decodedMotion.Manifest, decodedMotion.Presentation }))
                : !MotionJson.IsMotion(path) && !Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                    ? CharacterLibrary.Hash(path) : null;
            ct.ThrowIfCancellationRequested();
            return revision == null ? null : path + revision + JsonSerializer.Serialize(options);
        }, ct);
    }

    internal Preview? Find(string? key)
    {
        if (key == null) return null;
        for (var node = _entries.First; node != null; node = node.Next)
            if (node.Value.Key == key)
            {
                var preview = node.Value.Preview;
                if (RigCatalog.Ids.Contains(preview.Options.SourceRig) &&
                    RigCatalog.Hash(preview.Options.SourceRig) != preview.Options.SourceHash) return null;
                var copy = preview with { Result = Copy(preview.Result) };
                Hits++; _entries.Remove(node); _entries.AddFirst(node); return copy;
            }
        return null;
    }

    internal async Task<RetargetResult> Get(string path, RetargetOptions options, Func<Task<RetargetResult>> bake, CancellationToken ct)
    {
        var key = await KeyAsync(path, options, ct);
        ct.ThrowIfCancellationRequested();
        if (Find(key) is { } hit) return hit.Result;
        var baked = await bake();
        if (ct.IsCancellationRequested) { baked.Root.Free(); ct.ThrowIfCancellationRequested(); }
        return Store(key, new(baked, options, null, 0)).Result;
    }

    internal Preview Store(string? key, Preview preview)
    {
        if (key == null) return preview;
        long keys = 0;
        foreach (var player in Preview3DViewport.Descendants(preview.Result.Root).OfType<AnimationPlayer>())
            foreach (var clip in player.GetAnimationList())
            {
                var animation = player.GetAnimation(clip);
                int trackCount = animation.GetTrackCount();
                for (int track = 0; track < trackCount; track++) keys += animation.TrackGetKeyCount(track);
            }
        if (keys > MaxCachedKeys) return preview;
        var copy = preview with { Result = Copy(preview.Result) };
        for (var node = _entries.First; node != null; node = node.Next)
            if (node.Value.Key == key)
            {
                node.Value.Preview.Result.Root.Free(); _entries.Remove(node); break;
            }
        _entries.AddFirst((key, preview, keys));
        while (_entries.Count > MaxEntries || _entries.Sum(entry => entry.Keys) > MaxCachedKeys)
        {
            _entries.Last!.Value.Preview.Result.Root.Free();
            _entries.RemoveLast();
        }
        return copy;
    }
    private static RetargetResult Copy(RetargetResult source)
    {
        var root = (Node3D)source.Root.Duplicate();
        foreach (var player in Preview3DViewport.Descendants(root).OfType<AnimationPlayer>())
            foreach (var name in player.GetAnimationLibraryList())
            {
                var original = player.GetAnimationLibrary(name);
                var library = new AnimationLibrary();
                foreach (var clip in original.GetAnimationList()) library.AddAnimation(clip, (Animation)original.GetAnimation(clip).Duplicate(true));
                player.RemoveAnimationLibrary(name); player.AddAnimationLibrary(name, library);
            }
        return source with { Root = root };
    }
    public void Dispose() { foreach (var entry in _entries) entry.Preview.Result.Root.Free(); _entries.Clear(); }
}
