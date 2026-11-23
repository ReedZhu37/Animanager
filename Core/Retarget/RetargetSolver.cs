using Godot;

namespace Animanager.Core.Retarget;

/// <summary>Deterministic FK transfer in common model space. No scene-tree timing or modifier dependency.</summary>
public sealed class RetargetSolver
{
    public RigDescriptor Source { get; }
    public RigDescriptor Target { get; }
    public RetargetOptions Options { get; }
    public string[] Diagnostics { get; }
    private readonly Dictionary<int, (int A, int B, float Weight)> _chains = new();
    private readonly Dictionary<int, (int A, int B, float Weight)> _twists = new();
    private readonly Dictionary<int, int> _direct = new();
    private readonly float _hipRatio;
    private readonly Dictionary<int, Quaternion> _offsets = new();
    private readonly Quaternion[] _sourceInverseRestRotations;
    private readonly Quaternion[] _targetRestRotations;

    public RetargetSolver(RigDescriptor source, RigDescriptor target, RetargetOptions options)
    {
        Source = source; Target = target; Options = options.Snapshot();
        HumanoidMap.RequireReady(source); HumanoidMap.RequireReady(target);
        _sourceInverseRestRotations = source.GlobalRest.Select(rest => Rotation(rest).Inverse()).ToArray();
        _targetRestRotations = target.GlobalRest.Select(Rotation).ToArray();
        if (Options.SourceRig.Length > 0 && Options.SourceRig != source.Id)
            throw new InvalidOperationException("预设的源骨架与此动画不匹配。");
        foreach (var p in Options.RotationOffsets)
        {
            if (!target.Indices.TryGetValue(p.Key, out int i)) throw new ArgumentException("目标骨架没有补偿骨骼：" + p.Key);
            _offsets[i] = Quaternion.FromEuler(new Vector3(p.Value[0], p.Value[1], p.Value[2]) * (Mathf.Pi / 180));
        }
        foreach (int i in target.Order)
            if (source.Indices.TryGetValue(target.Roles[i], out int s)) _direct[i] = s;
        _hipRatio = Height(target) / Math.Max(.001f, Height(source));
        if (source.Id != target.Id)
        {
            string Chest(RigDescriptor r) => r.Roles.Where(n => n.StartsWith("spine_") && n[6..].All(char.IsDigit)).OrderBy(n => n).Last();
            Chain("spine_", "pelvis"); Chain("neck_", Chest(source), Chest(target));
            foreach (string side in new[] { "l", "r" })
                foreach (string limb in new[] { "upperarm", "lowerarm", "thigh", "calf" }) Twist(limb, side);
        }
        Diagnostics = target.Names.Where((name, i) => !_direct.ContainsKey(i) && !_chains.ContainsKey(i) && !_twists.ContainsKey(i) && !name.StartsWith("ik_"))
            .Select(n => n + ": 保留目标参考姿势（未执行 UE 纠正骨系统）").ToArray();
    }
    private static float Height(RigDescriptor r) => Math.Abs(r.GlobalRest[r.Indices["pelvis"]].Origin.Y -
        (r.GlobalRest[r.Indices["foot_l"]].Origin.Y + r.GlobalRest[r.Indices["foot_r"]].Origin.Y) * .5f);
    private void Chain(string prefix, string sourceParent, string? targetParent = null)
    {
        var src = Source.Order.Where(i => Source.Roles[i].StartsWith(prefix) && Source.Roles[i][prefix.Length..].All(char.IsDigit)).ToArray();
        var dst = Target.Order.Where(i => Target.Roles[i].StartsWith(prefix) && Target.Roles[i][prefix.Length..].All(char.IsDigit)).ToArray();
        if (src.Length == 0 || dst.Length == 0) return;
        var s = new[] { Source.Indices[sourceParent] }.Concat(src).ToArray();
        var t = new[] { Target.Indices[targetParent ?? sourceParent] }.Concat(dst).ToArray();
        float[] Positions(RigDescriptor r, int[] chain)
        {
            var values = new float[chain.Length];
            for (int i = 1; i < chain.Length; i++) values[i] = values[i - 1] + r.GlobalRest[chain[i]].Origin.DistanceTo(r.GlobalRest[chain[i - 1]].Origin);
            float length = values[^1];
            if (length <= 1e-6f) return Enumerable.Range(0, chain.Length).Select(i => i / (float)Math.Max(1, chain.Length - 1)).ToArray();
            return values.Select(v => v / length).ToArray();
        }
        var sp = Positions(Source, s); var tp = Positions(Target, t);
        for (int i = 1; i < t.Length; i++)
        {
            int hi = 1; while (hi < sp.Length - 1 && sp[hi] < tp[i]) hi++;
            float w = (tp[i] - sp[hi - 1]) / Math.Max(1e-6f, sp[hi] - sp[hi - 1]);
            _chains[t[i]] = (s[hi - 1], s[hi], Mathf.Clamp(w, 0, 1));
        }
    }
    private void Twist(string limb, string side)
    {
        string parent = limb + "_" + side;
        var s = Source.Order.Where(i => Source.Names[i].StartsWith(limb + "_twist_") && Source.Names[i].EndsWith("_" + side)).ToArray();
        var t = Target.Order.Where(i => Target.Names[i].StartsWith(limb + "_twist_") && Target.Names[i].EndsWith("_" + side)).ToArray();
        if (s.Length == 0) return;
        float Position(RigDescriptor r, int i) => r.GlobalRest[i].Origin.DistanceTo(r.GlobalRest[r.Indices[parent]].Origin);
        s = s.OrderBy(i => Position(Source, i)).ToArray();
        float srcLength = LimbLength(Source, parent), dstLength = LimbLength(Target, parent);
        foreach (int i in t)
        {
            float p = Position(Target, i) / dstLength;
            int hi = Array.FindIndex(s, b => Position(Source, b) / srcLength >= p);
            if (hi <= 0) { int b = hi < 0 ? s[^1] : s[0]; _twists[i] = (b, b, 0); }
            else
            {
                float a = Position(Source, s[hi - 1]) / srcLength, b = Position(Source, s[hi]) / srcLength;
                _twists[i] = (s[hi - 1], s[hi], (p - a) / Math.Max(1e-6f, b - a));
            }
        }
    }
    private static float LimbLength(RigDescriptor r, string limb)
    {
        var side = limb[^1];
        string end = limb.StartsWith("upperarm") ? "lowerarm_" : limb.StartsWith("lowerarm") ? "hand_" : limb.StartsWith("thigh") ? "calf_" : "foot_";
        return Math.Max(.001f, r.GlobalRest[r.Indices[end + side]].Origin.DistanceTo(r.GlobalRest[r.Indices[limb]].Origin));
    }
    private static Quaternion Rotation(Transform3D t) => t.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
    public Transform3D[] Evaluate(Transform3D[] sourcePose, Transform3D sourceFrame, Vector3 initialRoot)
    {
        if (!sourceFrame.IsFinite() || sourceFrame.Basis.Determinant() <= 0 || !RigDescriptor.Uniform(sourceFrame.Basis.Scale))
            throw new NotSupportedException("动画场景包含不支持的缩放或镜像。");
        if (sourcePose.Length != Source.Names.Length) throw new ArgumentException("Source pose size differs from rig.");
        var globals = new Transform3D[sourcePose.Length];
        foreach (int i in Source.Order)
        {
            if (!sourcePose[i].IsFinite() || sourcePose[i].Basis.Determinant() <= 0 || !RigDescriptor.Uniform(sourcePose[i].Basis.Scale))
                throw new NotSupportedException("动画包含镜像、奇异或非均匀缩放。");
            globals[i] = (Source.Parents[i] < 0 ? sourceFrame : globals[Source.Parents[i]]) * sourcePose[i];
        }
        Quaternion Delta(int i) => (Rotation(globals[i]) * _sourceInverseRestRotations[i]).Normalized();
        var output = Target.Rest.ToArray(); var targetGlobal = new Transform3D[output.Length];
        bool same = Source.Id == Target.Id || Source.Fingerprint == Target.Fingerprint;
        foreach (int i in Target.Order)
        {
            var parent = Target.Parents[i] < 0 ? Target.Frame : targetGlobal[Target.Parents[i]];
            string name = Target.Roles[i];
            Quaternion? delta = _chains.TryGetValue(i, out var chain) ? Delta(chain.A).Slerp(Delta(chain.B), chain.Weight) :
                _direct.TryGetValue(i, out int s) ? Delta(s) : null;
            if (_twists.TryGetValue(i, out var twist))
            {
                Quaternion LocalTwist(int bone)
                {
                    var rest = Source.Rest[bone];
                    var q = (Rotation(sourcePose[bone]) * Rotation(rest).Inverse()).Normalized();
                    // Twist helpers are siblings of the primary limb, not a chain. Transfer only local axial delta.
                    var axis = rest.Origin.Normalized(); if (axis.LengthSquared() < .5f) axis = Vector3.Right;
                    var v = axis * new Vector3(q.X, q.Y, q.Z).Dot(axis);
                    var result = new Quaternion(v.X, v.Y, v.Z, q.W);
                    return result.LengthSquared() < 1e-8f ? Quaternion.Identity : result.Normalized();
                }
                var local = LocalTwist(twist.A).Slerp(LocalTwist(twist.B), twist.Weight);
                int sp = Source.Parents[twist.A];
                var sourceBasis = Rotation(Source.GlobalRest[sp]);
                var targetBasis = Target.Parents[i] < 0 ? Rotation(Target.Frame) : Rotation(Target.GlobalRest[Target.Parents[i]]);
                var change = targetBasis.Inverse() * sourceBasis;
                output[i].Basis = new Basis((change * local * change.Inverse() * Rotation(Target.Rest[i])).Normalized()).Scaled(Target.Rest[i].Basis.Scale);
            }
            else if (delta is { } d)
                output[i].Basis = new Basis((Rotation(parent).Inverse() * d * _targetRestRotations[i]).Normalized()).Scaled(Target.Rest[i].Basis.Scale);

            if (same && _direct.TryGetValue(i, out int direct))
            {
                var srcParentRest = Source.Parents[direct] < 0 ? Source.Frame : Source.GlobalRest[Source.Parents[direct]];
                var dstParentRest = Target.Parents[i] < 0 ? Target.Frame : Target.GlobalRest[Target.Parents[i]];
                output[i].Origin += dstParentRest.Basis.Inverse() * (srcParentRest.Basis * (sourcePose[direct].Origin - Source.Rest[direct].Origin));
                output[i].Basis = output[i].Basis.Scaled(sourcePose[direct].Basis.Scale / Source.Rest[direct].Basis.Scale);
            }
            if (i == Target.Indices["root"])
            {
                int sr = Source.Indices["root"];
                var displacement = (globals[sr].Origin - Source.GlobalRest[sr].Origin) * Options.RootScale;
                if (Options.InPlace) { displacement.X = initialRoot.X * Options.RootScale; displacement.Z = initialRoot.Z * Options.RootScale; }
                output[i].Origin = parent.AffineInverse() * (Target.GlobalRest[i].Origin + displacement);
            }
            if (name == "pelvis")
            {
                int hip = Source.Indices[name];
                var sp = Source.Parents[hip] < 0 ? Source.Frame : Source.GlobalRest[Source.Parents[hip]];
                var tp = Target.Parents[i] < 0 ? Target.Frame : Target.GlobalRest[Target.Parents[i]];
                var localDisplacement = hip == Source.Indices["root"] ? Vector3.Zero : sp.Basis * (sourcePose[hip].Origin - Source.Rest[hip].Origin);
                output[i].Origin = Target.Rest[i].Origin + tp.Basis.Inverse() * localDisplacement * _hipRatio;
                if (i == Target.Indices["root"])
                {
                    var displacement = (globals[hip].Origin - Source.GlobalRest[hip].Origin) * Options.RootScale;
                    if (Options.InPlace) { displacement.X = initialRoot.X * Options.RootScale; displacement.Z = initialRoot.Z * Options.RootScale; }
                    output[i].Origin = parent.AffineInverse() * (Target.GlobalRest[i].Origin + displacement);
                }
                output[i].Origin += parent.Basis.Inverse() * (Vector3.Up * Options.HipHeight);
            }
            targetGlobal[i] = parent * output[i];
        }
        foreach (var (i, offset) in _offsets) output[i].Basis *= new Basis(offset);
        if (!same)
        {
            if (_offsets.Count > 0) targetGlobal = Target.Globals(output);
            // Reconstruct helper markers after all deform bones have been solved. No limb IK is run.
            foreach (string side in new[] { "l", "r" })
                foreach (string limb in new[] { "hand", "foot" })
                    Helper("ik_" + limb + "_" + side, limb + "_" + side);
            Helper("ik_hand_gun", "hand_r");
            // Recalculate child-local transforms after ik_hand_gun moves.
            foreach (string side in new[] { "l", "r" }) Helper("ik_hand_" + side, "hand_" + side);
        }
        return output;
        void Helper(string name, string anchor)
        {
            if (!Target.Indices.TryGetValue(name, out int i)) return;
            int a = Target.Indices[anchor];
            var desired = targetGlobal[a] * Target.GlobalRest[a].AffineInverse() * Target.GlobalRest[i];
            var parent = Target.Parents[i] < 0 ? Target.Frame : targetGlobal[Target.Parents[i]];
            output[i] = parent.AffineInverse() * desired; targetGlobal[i] = desired;
        }
    }
}
