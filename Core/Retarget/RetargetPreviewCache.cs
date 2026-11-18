using System.Text.Json;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Core.Retarget;

/// <summary>Small, session-only cache; every viewport receives its own nodes and animation resources.</summary>
internal sealed class RetargetPreviewCache : IDisposable
{
    private readonly LinkedList<(string Key, RetargetResult Result)> _entries = new();
    internal int Hits { get; private set; }
    internal async Task<RetargetResult> Get(string path, RetargetOptions options, Func<Task<RetargetResult>> bake, CancellationToken ct)
    {
        options.Character?.VerifiedPath();
        // Motion dependencies have their own revision/verification rules; do not cache them by manifest alone.
        bool cacheable = !Motion.MotionJson.IsMotion(path);
        string key = path + CharacterLibrary.Hash(path) + JsonSerializer.Serialize(options);
        if (cacheable)
            for (var node = _entries.First; node != null; node = node.Next)
                if (node.Value.Key == key)
                {
                    ct.ThrowIfCancellationRequested(); Hits++;
                    var result = Copy(node.Value.Result); _entries.Remove(node); _entries.AddFirst(node); return result;
                }
        var baked = await bake();
        if (ct.IsCancellationRequested) { baked.Root.Free(); ct.ThrowIfCancellationRequested(); }
        var players = Preview3DViewport.Descendants(baked.Root).OfType<AnimationPlayer>();
        long keys = players.Sum(p => p.GetAnimationList().Sum(c => Enumerable.Range(0, p.GetAnimation(c).GetTrackCount()).Sum(t => (long)p.GetAnimation(c).TrackGetKeyCount(t))));
        if (!cacheable || keys > 300_000) return baked;
        _entries.AddFirst((key, baked));
        while (_entries.Count > 3) { _entries.Last!.Value.Result.Root.Free(); _entries.RemoveLast(); }
        return Copy(baked);
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
    public void Dispose() { foreach (var entry in _entries) entry.Result.Root.Free(); _entries.Clear(); }
}
