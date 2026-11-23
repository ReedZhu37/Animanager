using Godot;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;

namespace Animanager.UI.Components;

public partial class Preview3DViewport
{
    private Node3D? _comparison;
    private AnimationPlayer? _comparisonPlayer;
    private MotionPlayback? _comparisonMotion;
    private Core.Preview.PoseSnapshot? _comparisonRest;
    private int _retargetView = 1;
    private double _retargetRate = 60;
    public RetargetOptions? AppliedRetarget { get; private set; }
    public int RetargetViewMode => _retargetView;
    public string RetargetSummary { get; private set; } = "";
    public string RetargetDiagnostics { get; private set; } = "";
    internal string? DeferredRetargetSource { get; private set; }
    public event Action? SourceLoaded;
    private void ClearRetarget()
    {
        _comparison?.GetParent()?.RemoveChild(_comparison); _comparison?.QueueFree();
        _comparison = null; _comparisonPlayer = null; _comparisonMotion = null; _comparisonRest = null; AppliedRetarget = null;
        DeferredRetargetSource = null;
        RetargetSummary = ""; RetargetDiagnostics = "";
    }
    public void AttachRetarget(RetargetResult result, RetargetOptions options, string sourcePath, int viewMode = 1,
        PreviewSource? preparedSource = null, bool newSource = false, AssetReadiness? preparedReadiness = null)
    {
        // Resolve all fallible source loading before replacing the visible model.
        using var loaded = preparedSource == null ? new PreviewSource(sourcePath) : null;
        var source = preparedSource ?? loaded!;
        var readiness = preparedReadiness ?? AssetReadiness.Inspect(source.Root, true);
        string clip = newSource ? "" : CurrentClip; double time = newSource ? 0 : Playhead;
        bool frameCamera = !newSource || !PreserveAnimationCamera;
        AttachRetargetTarget(result, options, readiness, source.FrameRate);
        AttachRetargetSource(source);
        _retargetView = viewMode;
        if (Clips.Contains(clip)) SelectClip(clip, frameCamera: false);
        Seek(Math.Min(time, Duration)); SetRetargetView(viewMode, frameCamera);
    }

    internal void AttachCachedRetarget(RetargetPreviewCache.Preview cached, string sourcePath)
    {
        bool frameCamera = !PreserveAnimationCamera;
        AttachRetargetTarget(cached.Result, cached.Options, cached.Readiness!, cached.SourceFps);
        DeferredRetargetSource = sourcePath;
        SetRetargetView(1, frameCamera);
    }

    internal void AttachRetargetSource(PreviewSource source)
    {
        _comparisonMotion = source.Motion;
        _comparison = source.TakeRoot();
        _comparison.Visible = false;
        _stage.AddChild(_comparison);
        _comparisonRest = new(_comparison);
        _comparisonPlayer = Descendants(_comparison).OfType<AnimationPlayer>().FirstOrDefault();
        ApplyClay(_comparison, _clay);
        DeferredRetargetSource = null;
    }

    private void AttachRetargetTarget(RetargetResult result, RetargetOptions options, AssetReadiness readiness, double sourceFps)
    {
        var snapshot = options.Snapshot();
        AttachModel(result.Root, readiness);
        _sourceRate = sourceFps;
        AppliedRetarget = snapshot;
        _retargetRate = result.SampleFps;
        RetargetSummary = $"{result.Source.Id} → {options.Character?.Name ?? result.Target.Id}\n{result.Target.Names.Length} 骨骼 · {result.SampleFps:G5} FPS\n{result.Diagnostics.Length} 个辅助骨保留参考姿势；无接触 IK。";
        RetargetDiagnostics = string.Join("\n", result.Diagnostics);
    }
    public void SetRetargetView(int mode) => SetRetargetView(mode, frameCamera: true);

    private void SetRetargetView(int mode, bool frameCamera)
    {
        _retargetView = mode; SyncRetargetComparison();
        if (frameCamera) FrameModel();
        if (AppliedRetarget != null && Model != null) { _baseline?.SetRootTransform(Model.Transform); Bones.ClipChanged(); }
        if (frameCamera && mode == 2) { _target.X = 0; _distance *= 1.5f; UpdateCamera(); }
        RefreshWeaponPreview();
        Redraw();
    }
    private void SyncRetargetComparison()
    {
        if (_comparison == null || Model == null || AppliedRetarget == null) return;
        Model.Visible = _retargetView != 0;
        // Target root is our identity wrapper, so display placement never enters the bake.
        Model.Position = _retargetView == 2 ? Vector3.Right * 1.2f : Vector3.Zero;
        _comparison.Visible = _retargetView != 1;
        if (_retargetView == 1) return;
        _comparisonRest?.Restore(_comparison);
        if (_comparisonPlayer != null && _comparisonPlayer.HasAnimation(CurrentClip))
        {
            if (_comparisonPlayer.CurrentAnimation != CurrentClip)
            { _comparisonPlayer.Play(CurrentClip); _comparisonPlayer.Advance(0); _comparisonPlayer.Pause(); }
            var animation = _comparisonPlayer.GetAnimation(CurrentClip); var loop = animation.LoopMode;
            animation.LoopMode = Animation.LoopModeEnum.None;
            _comparisonPlayer.Seek(Playhead, true, true);
            animation.LoopMode = loop;
        }
        _comparisonMotion?.Sync();
        if (_retargetView == 2) _comparison.Position += Vector3.Left * 1.2f;
    }
}
