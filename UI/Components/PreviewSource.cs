using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Godot;

namespace Animanager.UI.Components;

/// <summary>A detached source with explicit ownership. Preparing it never changes the displayed viewport.</summary>
public sealed class PreviewSource : IDisposable
{
    public Node3D Root { get; }
    public MotionPlayback? Motion { get; }
    public double FrameRate { get; }
    internal float? ReportedFrameRate { get; }
    public string Path { get; }
    internal MotionPlayback.SourceData? DecodedMotion { get; private init; }
    public bool HasAnimation => Preview3DViewport.Descendants(Root).OfType<AnimationPlayer>().Any(p => p.GetAnimationList().Any(c => c != "RESET"));
    internal bool HasSkeletonWithoutMesh => Preview3DViewport.Descendants(Root).OfType<Skeleton3D>().Any(skeleton => skeleton.GetBoneCount() > 0)
        && !Preview3DViewport.Descendants(Root).OfType<MeshInstance3D>().Any(mesh => mesh.Mesh != null);
    private bool _owned = true;
    public PreviewSource(string path) : this(path, MotionJson.IsMotion(path) ? new MotionPlayback(path) : null) { }

    private PreviewSource(string path, MotionPlayback? motion)
    {
        Path = path;
        Motion = motion;
        if (motion != null) { Root = motion.Root; FrameRate = motion.Manifest.SourceFps; }
        else { Root = SceneAsset.Load(path, out var rate); ReportedFrameRate = rate; FrameRate = rate ?? 30; }
    }

    internal static PreviewSource FromParsed(string path, SceneAsset.ParsedScene scene, bool fullTracks) =>
        new(path, scene.Generate(fullTracks), scene.SourceFps);

    private PreviewSource(string path, Node3D root, float? rate)
    {
        Path = path; Root = root; ReportedFrameRate = rate; FrameRate = rate ?? 30;
    }

    internal static async Task<PreviewSource> LoadAsync(string path, CancellationToken ct, bool fullTracks = false,
        MotionPlayback.SourceData? decodedMotion = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!MotionJson.IsMotion(path))
        {
            using var parsed = await SceneAsset.ParsePreviewAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            return FromParsed(path, parsed, fullTracks);
        }

        // Decode the files off the UI thread. The continuation creates the Godot scene on the caller's thread.
        var source = decodedMotion ?? await Task.Run(() => MotionPlayback.ReadSource(path, verifySourceObjects: true, ct), ct);
        ct.ThrowIfCancellationRequested();
        return new(path, new MotionPlayback(path, source)) { DecodedMotion = source };
    }

    public Node3D TakeRoot() { _owned = false; return Root; }
    public void Dispose() { if (_owned) { Root.Free(); _owned = false; } }
}
