using Animanager.Core.Pipeline;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Core.Retarget;

/// <summary>One prepared target shared by library preloading and interactive bakes on the Godot thread.</summary>
internal sealed class RetargetTargetCache : IDisposable
{
    private (string Id, string Hash, string Path)? _key;
    private CancellationTokenSource? _loadCancellation;
    private Task<PackedScene>? _load;

    internal Task WarmAsync(RetargetOptions options) => GetTemplate(options);

    internal async Task<Node3D> InstantiateAsync(RetargetOptions options, CancellationToken ct)
    {
        var template = await GetTemplate(options).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return template.Instantiate<Node3D>();
    }

    private Task<PackedScene> GetTemplate(RetargetOptions options)
    {
        var key = (options.TargetRig, options.Character?.Sha256 ?? options.TargetHash,
            options.Character?.ModelPath ?? RigCatalog.ModelPath(options.TargetRig));
        if (_key == key && _load is { IsFaulted: false, IsCanceled: false }) return _load;
        Dispose();
        _key = key;
        _loadCancellation = new();
        return _load = LoadAsync(key.Item3, options, _loadCancellation.Token);
    }

    private static async Task<PackedScene> LoadAsync(string path, RetargetOptions options, CancellationToken ct)
    {
        if (options.Character is { } character) await Task.Run(character.VerifiedPath, ct);
        using var parsed = await SceneAsset.ParsePreviewAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        var root = parsed.Generate(fullTracks: true);
        if (options.Character == null) root = RigCatalog.Complete(options.TargetRig, root, out _);
        try
        {
            foreach (var player in Preview3DViewport.Descendants(root).OfType<AnimationPlayer>().ToArray())
            {
                player.GetParent().RemoveChild(player);
                player.Free();
            }
            var scene = new PackedScene();
            var error = scene.Pack(root);
            if (error != Error.Ok) throw new IOException("无法准备预览角色：" + error);
            return scene;
        }
        finally { root.Free(); }
    }

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _key = null;
        _load = null;
    }
}
