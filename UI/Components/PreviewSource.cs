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
    public string Path { get; }
    public bool HasAnimation => Preview3DViewport.Descendants(Root).OfType<AnimationPlayer>().Any(p => p.GetAnimationList().Any(c => c != "RESET"));
    private bool _owned = true;
    public PreviewSource(string path)
    {
        Path = path;
        if (MotionJson.IsMotion(path)) { Motion = new(path); Root = Motion.Root; FrameRate = Motion.Manifest.SourceFps; }
        else { Root = SceneAsset.Load(path, out var rate); FrameRate = rate ?? 30; }
    }
    public Node3D TakeRoot() { _owned = false; return Root; }
    public void Dispose() { if (_owned) { Root.Free(); _owned = false; } }
}
