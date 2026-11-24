using Animanager.Core;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Godot;

namespace Animanager.UI.Components;

internal sealed partial class MotionAudition : Window
{
    internal sealed record Take(string Path, string Clip, string Name, double Start, double End);
    private sealed record BakedTake(RetargetResult Result, double Offset, double End);
    private readonly CancellationTokenSource _loading = new();
    private readonly Preview3DViewport _view = new();
    private readonly Label _status = new() { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private double _start, _cut, _entry, _end, _elapsed, _fromOffset, _toOffset;
    private string _fromName = "", _toName = "";
    private Transform3D _alignment = Transform3D.Identity;
    private Node3D? _fromProp, _toProp;

    public override void _Ready()
    {
        Title = "动作试接";
        Transient = true;
        CloseRequested += QueueFree;
        var panel = new VBoxContainer();
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.AddChild(_status);
        panel.AddChild(_view);
        _status.Text = "正在准备试接…";
        SetProcess(false);
    }

    internal async Task Prepare(Take openingTake, Take to, RetargetOptions character)
    {
        var ct = _loading.Token;
        BakedTake? a = null, b = null;
        using var targets = new RetargetTargetCache();
        try
        {
            _start = openingTake.Start < openingTake.End ? openingTake.Start : Math.Max(0, openingTake.End - .75);
            _cut = openingTake.End;
            _entry = to.Start;
            a = await Bake(openingTake with { Start = _start });
            b = await Bake(to with { End = Math.Max(to.End, _entry + 1) });
            ct.ThrowIfCancellationRequested();
            _fromName = openingTake.Name;
            _toName = to.Name;
            _end = b.End;
            _fromOffset = a.Offset;
            _toOffset = b.Offset;
            _status.TooltipText = $"{openingTake.Name} · {openingTake.Clip} · {_cut:0.###}s → {to.Name} · {to.Clip} · {_entry:0.###}s";
            var model = a.Result.Root;
            var player = model.GetNode<AnimationPlayer>("RetargetPlayer");
            var opening = player.GetAnimation(openingTake.Clip);
            var followup = b.Result.Root.GetNode<AnimationPlayer>("RetargetPlayer").GetAnimation(to.Clip);
            _fromProp = model.GetNodeOrNull<Node3D>(RetargetBaker.PropNodeName);
            _toProp = b.Result.Root.GetNodeOrNull<Node3D>(RetargetBaker.PropNodeName);
            if (_toProp != null)
            {
                // Each prop retains its own grip calibration. Body tracks bind to the same target skeleton.
                foreach (var node in Preview3DViewport.Descendants(_toProp)) node.Owner = null;
                _toProp.Name = "CandidateWeapon";
                _toProp.Reparent(model, keepGlobalTransform: false);
                for (int i = 0; i < followup.GetTrackCount(); i++)
                    if (followup.TrackGetPath(i).ToString() == RetargetBaker.PropNodeName)
                        followup.TrackSetPath(i, model.GetPathTo(_toProp));
            }
            foreach (var library in player.GetAnimationLibraryList()) player.RemoveAnimationLibrary(library);
            RetargetBaker.AddAnimation(player, "From", opening);
            RetargetBaker.AddAnimation(player, "To", followup);
            _view.AttachModel(model);
            _view.SelectClip("From");
            _view.Seek(_cut - _fromOffset);
            var skeleton = _view.Bones.Skeletons.Single().Node;
            int pelvis = a.Result.Target.Indices["pelvis"], left = a.Result.Target.Indices["thigh_l"], right = a.Result.Target.Indices["thigh_r"];
            Vector3 Joint(int i) => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(i).Origin;
            Vector3 Side() => (Joint(right) - Joint(left)).Slide(Vector3.Up).Normalized();
            var hip = Joint(pelvis);
            var facing = Side();
            _view.FrameModel();
            _view.FocusPoint(hip);
            _view.SelectClip("To");
            _view.Seek(_entry - _toOffset);
            var yaw = new Basis(Vector3.Up, Side().SignedAngleTo(facing, Vector3.Up));
            var shift = hip - yaw * Joint(pelvis);
            _alignment = new(yaw, new Vector3(shift.X, 0, shift.Z));
            SetProcess(true);
            _Process(0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested) _status.TooltipText = _status.Text = "无法试接 · " + ex.Message;
        }
        finally
        {
            b?.Result.Root.Free();
            if (a != null && a.Result.Root.GetParent() == null) a.Result.Root.Free();
        }

        async Task<BakedTake> Bake(Take take)
        {
            using var source = await PreviewSource.LoadAsync(take.Path, ct, fullTracks: true);
            ct.ThrowIfCancellationRequested();
            RetargetBaker.RejectAdditive(take.Path);
            var readiness = await AssetReadiness.InspectPreviewAsync(source.Root, GetTree(), ct);
            var options = WorkspaceApi.ResolveRetargetRig(source.Root, character, readiness, out var rig);
            var player = Preview3DViewport.Descendants(source.Root).OfType<AnimationPlayer>().First();
            if (!player.HasAnimation(take.Clip)) throw new InvalidOperationException("找不到片段 " + take.Clip);
            double length = player.GetAnimation(take.Clip).Length;
            // Keep an interpolation interval when the seam itself is the first or last frame.
            double end = Math.Clamp(Math.Max(take.End, take.Start + .01), 0, length);
            double start = Math.Clamp(take.Start, 0, Math.Max(0, end - .01));
            var range = new ExportOptions { IncludeMesh = true, Retarget = options, Clips = [take.Clip],
                StartSeconds = start, EndSeconds = end, SampleFps = Math.Clamp(source.FrameRate, 30, 120) };
            var result = await RetargetBaker.BakePreviewRangeAsync(source, rig, range,
                () => targets.InstantiateAsync(options, ct), ct);
            return new(result, start, Math.Min(take.End, length));
        }
    }

    public override void _Process(double delta)
    {
        double lead = Math.Max(.15, _cut - _start), tail = Math.Max(.15, _end - _entry);
        _elapsed = (_elapsed + delta) % (lead + tail);
        bool first = _elapsed < lead;
        string clip = first ? "From" : "To";
        if (_view.CurrentClip != clip)
        {
            _view.SelectClip(clip);
            _view.Model!.Transform = first ? Transform3D.Identity : _alignment;
            if (_fromProp != null) _fromProp.Visible = first;
            if (_toProp != null) _toProp.Visible = !first;
        }
        double t = first ? Math.Min(_cut, _start + _elapsed) : Math.Min(_end, _entry + _elapsed - lead);
        _view.Seek(t - (first ? _fromOffset : _toOffset));
        _status.Text = $"{(first ? _fromName : _toName)} · {t:0.00} 秒    |    {_fromName} {_cut:0.##}s → {_toName} {_entry:0.##}s";
    }

    public override void _ExitTree()
    {
        _loading.Cancel();
        _loading.Dispose();
    }
}
