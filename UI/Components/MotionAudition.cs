using Godot;
using Animanager.Core.Retarget;

namespace Animanager.UI.Components;

internal sealed partial class MotionAudition : Window
{
    private readonly CancellationTokenSource _loading = new();
    private readonly Preview3DViewport _from = new(), _to = new();
    private readonly Label _status = new() { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private double _start, _cut, _entry, _end, _elapsed;
    private string _fromName = "", _toName = "";

    public override void _Ready()
    {
        Title = "动作试接";
        Transient = true;
        CloseRequested += QueueFree;
        var panel = new VBoxContainer();
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.AddChild(_status);
        panel.AddChild(_from);
        panel.AddChild(_to);
        _to.Hide();
        _status.Text = "正在准备试接…";
        SetProcess(false);
    }

    internal async Task Prepare(string fromPath, string fromClip, string fromName, double start, double cut,
        string toPath, string toClip, string toName, double entry, double end)
    {
        var ct = _loading.Token;
        try
        {
            await Attach(_from, fromPath, fromClip);
            await Attach(_to, toPath, toClip);
            _fromName = fromName;
            _toName = toName;
            _status.TooltipText = $"{fromName} · {fromClip} · {cut:0.###}s → {toName} · {toClip} · {entry:0.###}s";
            _start = start < cut ? start : Math.Max(0, cut - .75);
            _cut = cut;
            _entry = entry;
            _end = Math.Min(_to.Duration, Math.Max(end, entry + 1));
            _from.Seek(cut);
            _to.Seek(entry);
            _from.FrameModel();
            _to.AlignTransitionView(_from);
            SetProcess(true);
            _Process(0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested) _status.Text = "无法试接 · " + ex.Message;
        }

        async Task Attach(Preview3DViewport view, string path, string clip)
        {
            using var source = await PreviewSource.LoadAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            view.AttachSource(source);
            if (!view.Clips.Contains(clip)) throw new InvalidOperationException("找不到片段 " + clip);
            view.SelectClip(clip);
        }
    }

    public override void _Process(double delta)
    {
        double lead = Math.Max(.15, _cut - _start), tail = Math.Max(.15, _end - _entry);
        _elapsed = (_elapsed + delta) % (lead + tail);
        bool first = _elapsed < lead;
        _from.Visible = first;
        _to.Visible = !first;
        double t = first ? Math.Min(_cut, _start + _elapsed) : Math.Min(_end, _entry + _elapsed - lead);
        (first ? _from : _to).Seek(t);
        _status.Text = $"{(first ? _fromName : _toName)} · {t:0.00} 秒    |    {_fromName} {_cut:0.##}s → {_toName} {_entry:0.##}s";
    }

    public override void _ExitTree()
    {
        _loading.Cancel();
        _loading.Dispose();
    }
}

public partial class Preview3DViewport
{
    internal void AlignTransitionView(Preview3DViewport from)
    {
        static (Vector3 Hip, Vector3 Side, float Height)? Body(Preview3DViewport view)
        {
            foreach (var item in view.Bones.Skeletons)
            {
                var skel = item.Node;
                int Joint(string role) => Enumerable.Range(0, skel.GetBoneCount()).FirstOrDefault(
                    i => HumanoidMap.Role(skel.GetBoneName(i)) == role, -1);
                int pelvis = Joint("pelvis"), left = Joint("thigh_l"), right = Joint("thigh_r"), head = Joint("head"), foot = Joint("foot_l");
                if (new[] { pelvis, left, right, head, foot }.Min() < 0) continue;
                Vector3 At(int j) => skel.GlobalTransform * skel.GetBoneGlobalPose(j).Origin;
                float height = (skel.GlobalTransform.Basis * (skel.GetBoneGlobalRest(head).Origin - skel.GetBoneGlobalRest(foot).Origin)).Length();
                return (At(pelvis), (At(right) - At(left)).Slide(Vector3.Up).Normalized(), height);
            }
            return null;
        }
        if (Body(from) is not { } a || Body(this) is not { } b || a.Height < .001f || b.Height < .001f) return;
        float angle = a.Side.SignedAngleTo(b.Side, Vector3.Up);
        float scale = b.Height / a.Height;
        from._target = a.Hip;
        from.UpdateCamera();
        _target = b.Hip;
        _yaw = from._yaw + angle;
        _pitch = from._pitch;
        _distance = from._distance * scale;
        UpdateCamera();
    }
}
