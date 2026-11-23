using Godot;

namespace Animanager.UI.Components;

public partial class Preview3DViewport
{
    private sealed record SinglePreviewState(Vector3 Target, float Distance, float Yaw, float Pitch, string ViewName,
        Camera3D.ProjectionType Projection, double Playhead, bool Playing, bool ModelVisible, bool RetargetSourceVisible);

    private SinglePreviewState? _singlePreview;
    internal AnimationComparison? AssetComparison { get; private set; }
    internal bool TransportPlaying => AssetComparison?.Playing ?? Playing;
    internal double TransportDuration => AssetComparison?.Duration ?? Duration;
    internal double TransportPlayhead => AssetComparison?.Playhead ?? Playhead;
    internal double TransportStep => AssetComparison != null ? 1.0 / 30 : FrameStep;
    internal event Action<string>? ComparisonRemoveRequested;
    public event Action? ComparisonExitRequested;

    internal void AddComparisonAsset(string primaryName, string key, string name, Node3D root)
    {
        if (AssetComparison is { } active)
        {
            try { active.AddSource(key, name, root, _clay); }
            finally { _camera.MakeCurrent(); }
            active.Arrange();
            RefreshWeaponPreview();
            UpdateComparisonActions();
            return;
        }

        var model = Model ?? throw new InvalidOperationException("请先双击打开一个资产。");
        var comparison = new AnimationComparison { Name = "AnimationComparison", Visible = false };
        _stage.AddChild(comparison);
        var state = new SinglePreviewState(_target, _distance, _yaw, _pitch, _viewName, _camera.Projection,
            Playhead, Playing, model.Visible, _comparison?.Visible == true);
        try
        {
            comparison.AddSource(key, name, root, _clay);
            if (Playing) TogglePlay();
            comparison.AddPrimary(primaryName, model, Player, CurrentClip, _baseline!);
            comparison.Seek(state.Playhead);
            if (state.Playing) comparison.TogglePlay();
            comparison.Arrange();

            _singlePreview = state;
            model.Show();
            _comparison?.Hide();
            Bones.Overlay.Hide();
            AssetComparison = comparison;
            comparison.Show();
            RefreshWeaponPreview();
            UpdateComparisonNavigation();
            UpdateComparisonActions();
            SetProcess(comparison.Playing);
        }
        finally
        {
            _camera.MakeCurrent();
            if (AssetComparison != comparison)
            {
                if (comparison.IsAncestorOf(model)) model.Reparent(_stage, keepGlobalTransform: false);
                if (comparison.IsAncestorOf(root)) root.GetParent().RemoveChild(root);
                _stage.RemoveChild(comparison);
                comparison.QueueFree();
                if (state.Playing && !Playing) TogglePlay();
            }
        }
    }

    internal bool RemoveComparisonAsset(string key)
    {
        if (AssetComparison is not { } comparison || !comparison.RemoveSource(key)) return false;
        if (comparison.Count == 1) EndAnimationComparison();
        else
        {
            comparison.Arrange();
            RefreshWeaponPreview();
            UpdateComparisonActions();
            SetProcess(comparison.Playing);
            FrameModel();
        }
        return true;
    }

    internal void EndAnimationComparison(bool resume = true)
    {
        if (AssetComparison is not { } comparison || _singlePreview is not { } state) return;
        AssetComparison = null;
        _singlePreview = null;
        ClearWeaponPreview(hideToggle: false);
        Model!.Reparent(_stage, keepGlobalTransform: false);
        _stage.RemoveChild(comparison);
        comparison.QueueFree();
        Model.Visible = state.ModelVisible;
        if (_comparison != null) _comparison.Visible = state.RetargetSourceVisible;
        Bones.Overlay.Show();
        (_target, _distance, _yaw, _pitch, _viewName) = (state.Target, state.Distance, state.Yaw, state.Pitch, state.ViewName);
        _camera.Projection = state.Projection;
        RefreshWeaponPreview();
        Seek(state.Playhead);
        if (resume && state.Playing && !Playing) TogglePlay();
        SetProcess(Playing);
        UpdateComparisonNavigation();
        UpdateComparisonActions();
        UpdateCamera();
    }

    internal void ToggleTransport()
    {
        if (AssetComparison is not { } comparison) { TogglePlay(); return; }
        comparison.TogglePlay();
        SetProcess(comparison.Playing);
        Redraw();
    }

    internal void SeekTransport(double time)
    {
        if (AssetComparison is not { } comparison) { Seek(time); return; }
        comparison.Seek(time);
        SetProcess(false);
        Redraw();
    }

    private void FrameComparison(Aabb box)
    {
        _target = box.GetCenter();
        _yaw = .13f;
        _pitch = .28f;
        _viewName = "用户视图";
        var back = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(_pitch), Mathf.Sin(_pitch), Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
        var right = Vector3.Up.Cross(back).Normalized();
        var up = back.Cross(right);
        float aspect = Size.Y > 0 ? Math.Max(.1f, Size.X / Size.Y) : 1;
        float halfFov = Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2);
        float distance = 0;
        for (int i = 0; i < 8; i++)
        {
            var corner = box.GetEndpoint(i) - _target;
            float extent = Math.Max(Math.Abs(corner.Dot(right)) / aspect, Math.Abs(corner.Dot(up)));
            distance = Math.Max(distance, Orthographic ? extent * 2 / .65f : (extent / halfFov + corner.Dot(back)) * Math.Min(aspect, 1));
        }
        _distance = Math.Max(.3f, distance * 1.22f);
        UpdateCamera();
    }
}
