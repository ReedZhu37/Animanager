using Godot;
using Animanager.Core.Preview;

namespace Animanager.UI.Components;

/// <summary>Assets laid out beside the open preview and driven by one clock.</summary>
internal sealed partial class AnimationComparison : Node3D
{
    internal sealed class Entry(string? key, Node3D slot, Node3D root, BoneLabelAnchor label,
        AnimationPlayer? player, string clip, Animation? animation, PoseSnapshot baseline, Aabb bounds,
        Node[] nodes, BoneAttachment3D[] attachments, ImmediateMesh? pose)
    {
        private double _lastTime = double.NaN;
        public string? Key { get; } = key;
        public Node3D Slot { get; } = slot;
        public Node3D Root { get; } = root;
        public AnimationPlayer? Player { get; } = player;
        public string Clip { get; } = clip;
        public PoseSnapshot Baseline { get; } = baseline;
        public BoneLabelAnchor Label { get; } = label;
        public double Duration { get; } = animation?.Length ?? 0;
        public Aabb Bounds { get; } = bounds;

        public void Seek(double time)
        {
            time = Math.Min(time, Duration);
            if (_lastTime == time) return;
            _lastTime = time;
            if (Player != null && animation != null)
            {
                var loop = animation.LoopMode;
                animation.LoopMode = Animation.LoopModeEnum.None;
                try { Player.Seek(time, update: true, updateOnly: true); }
                finally { animation.LoopMode = loop; }
            }
            foreach (var attachment in attachments) attachment.OnSkeletonUpdate();
            if (pose == null) return;
            pose.ClearSurfaces();
            bool begun = false;
            foreach (var (from, to) in AssetThumbnailRenderer.PoseLines(nodes))
            {
                if (!begun) { pose.SurfaceBegin(Mesh.PrimitiveType.Lines); begun = true; }
                pose.SurfaceAddVertex(Slot.ToLocal(from));
                pose.SurfaceAddVertex(Slot.ToLocal(to));
            }
            if (begun) pose.SurfaceEnd();
        }
    }

    private readonly List<Entry> _entries = [];
    public int Count => _entries.Count;
    public double Duration { get; private set; }
    public double Playhead { get; private set; }
    public bool Playing { get; private set; }
    public Aabb Bounds { get; private set; }
    internal IReadOnlyList<Entry> Entries => _entries;
    public IEnumerable<BoneLabelAnchor> Labels => _entries.Select(entry => entry.Label);
    public IEnumerable<(string Key, string Name)> ComparedAssets => _entries.Where(entry => entry.Key != null)
        .Select(entry => (entry.Key!, entry.Label.Text));
    public bool Contains(string key) => _entries.Any(entry => entry.Key == key);

    public void AddPrimary(string name, Node3D root, AnimationPlayer? player, string clip, PoseSnapshot baseline) =>
        AddEntry(null, name, root, Preview3DViewport.Descendants(root).ToArray(), player, clip, baseline);

    public void AddSource(string key, string name, Node3D root, bool clay)
    {
        var nodes = Preview3DViewport.Descendants(root).ToArray();
        var players = nodes.OfType<AnimationPlayer>().ToArray();
        foreach (var p in players) p.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        var player = players.FirstOrDefault(p => p.GetAnimationList().Any(clip => clip != "RESET"));
        var clip = player?.GetAnimationList().First(c => c != "RESET") ?? "";
        Preview3DViewport.ApplyClay(root, clay);
        AddEntry(key, name, root, nodes, player, clip, new PoseSnapshot(root));
    }

    private void AddEntry(string? key, string name, Node3D root, Node[] nodes, AnimationPlayer? player, string clip, PoseSnapshot baseline)
    {
        var animation = clip.Length > 0 ? player!.GetAnimation(clip) : null;
        if (animation != null && (animation.Length <= 0 || !double.IsFinite(animation.Length)))
            throw new InvalidDataException("动画片段时长无效。");

        var originalParent = root.GetParent();
        var slot = new Node3D();
        AddChild(slot);
        try
        {
            if (originalParent != null) root.Reparent(slot, keepGlobalTransform: false);
            else slot.AddChild(root);
            if (key != null && player != null)
            {
                player.Play(clip);
                player.Advance(0);
                player.Pause();
            }
            var attachments = nodes.OfType<BoneAttachment3D>().ToArray();
            foreach (var attachment in attachments) attachment.OnSkeletonUpdate();

            var bounds = Preview3DViewport.CalculateWorldBounds(root);
            ImmediateMesh? pose = null;
            if (bounds == null)
            {
                foreach (var (from, to) in AssetThumbnailRenderer.PoseLines(nodes))
                    bounds = (bounds ?? new Aabb(from, Vector3.Zero)).Expand(from).Expand(to);
                if (bounds == null) throw new InvalidDataException("没有可显示的网格或骨架。");
                pose = new ImmediateMesh();
                var visual = new MeshInstance3D { Mesh = pose, MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("c5def7")
                } };
                visual.SetMeta("animanager_prop_visual", true);
                slot.AddChild(visual);
            }
            var box = bounds.Value;
            if (!box.Position.IsFinite() || !box.Size.IsFinite()) throw new InvalidDataException("模型范围无效。");
            var label = new BoneLabelAnchor
            {
                Text = name,
                Position = new Vector3(box.GetCenter().X, box.End.Y + Math.Max(.03f, box.Size.Y * .05f), box.GetCenter().Z)
            };
            slot.AddChild(label);
            var entry = new Entry(key, slot, root, label, player, clip, animation, baseline, box, nodes, attachments, pose);
            entry.Seek(key == null && animation != null ? player!.CurrentAnimationPosition : Playhead);
            if (key == null) _entries.Insert(0, entry);
            else _entries.Add(entry);
            Duration = Math.Max(Duration, entry.Duration);
        }
        catch
        {
            if (originalParent != null) root.Reparent(originalParent, keepGlobalTransform: false);
            else slot.RemoveChild(root);
            RemoveChild(slot);
            slot.QueueFree();
            throw;
        }
    }

    internal bool MoveEntry(int index, int insertion)
    {
        if (index < 0 || index >= Count || insertion < 0 || insertion > Count) return false;
        int destination = insertion > index ? insertion - 1 : insertion;
        if (index == destination) return false;
        var entry = _entries[index];
        _entries.RemoveAt(index);
        _entries.Insert(destination, entry);
        Arrange();
        return true;
    }

    public bool RemoveSource(string key)
    {
        int index = _entries.FindIndex(entry => entry.Key == key);
        if (index < 0) return false;
        var slot = _entries[index].Slot;
        _entries.RemoveAt(index);
        RemoveChild(slot);
        slot.QueueFree();
        Duration = _entries.Max(entry => entry.Duration);
        if (Duration == 0) Playing = false;
        if (Playhead > Duration) ApplyTime(Duration);
        return true;
    }

    public void Arrange()
    {
        float height = _entries.Max(e => e.Bounds.Size.Y);
        float gap = Math.Max(.1f, height * .55f);
        float left = -(_entries.Sum(e => e.Bounds.Size.X) + (Count - 1) * gap) * .5f;
        Aabb? bounds = null;
        foreach (var entry in _entries)
        {
            entry.Slot.Position = new Vector3(left - entry.Bounds.Position.X,
                -entry.Bounds.Position.Y, -entry.Bounds.GetCenter().Z);
            var placed = new Aabb(entry.Bounds.Position + entry.Slot.Position, entry.Bounds.Size);
            bounds = bounds?.Merge(placed) ?? placed;
            left += entry.Bounds.Size.X + gap;
        }
        Bounds = bounds!.Value;
    }

    public void TogglePlay()
    {
        if (Duration == 0) return;
        if (!Playing && Playhead >= Duration) ApplyTime(0);
        Playing = !Playing;
    }

    public void Seek(double time)
    {
        Playing = false;
        ApplyTime(Math.Clamp(time, 0, Duration));
    }

    public void Advance(double delta, bool loop)
    {
        if (!Playing) return;
        double next = Playhead + delta;
        if (!loop && next >= Duration) Seek(Duration);
        else ApplyTime(loop ? next % Duration : next);
    }

    private void ApplyTime(double time)
    {
        Playhead = time;
        foreach (var entry in _entries) entry.Seek(time);
    }
}
