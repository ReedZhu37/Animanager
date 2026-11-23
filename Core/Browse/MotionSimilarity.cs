using Godot;

namespace Animanager.Core.Browse;

internal sealed record MotionHit(string Clip, double Start, double End, double Score);

// Frames contain body-relative joint positions in units of standing height.
internal sealed record MotionSamples(string Name, double Duration, Vector3[][] Frames)
{
    internal const int Rate = 30;
    internal static int FrameCount(double duration, double rate = Rate) =>
        (int)Math.Clamp(Math.Ceiling(duration * Math.Clamp(rate, 1, 120) - 1e-6) + 1, 2, 18001);
    internal double Time(int frame) => Duration * ((double)frame / (Frames.Length - 1));
    internal int Frame(double time) => (int)Math.Round(Math.Clamp(time / Duration, 0, 1) * (Frames.Length - 1));
}

internal static class MotionSimilarity
{
    // Hips, head, shoulders, elbows, hands, knees and feet. Hands are weighted for weapon stances.
    internal static readonly string[] Roles = ["pelvis", "head", "upperarm_l", "upperarm_r", "lowerarm_l",
        "lowerarm_r", "hand_l", "hand_r", "calf_l", "calf_r", "foot_l", "foot_r", "thigh_l", "thigh_r"];
    internal static readonly string[] Fingers = ["index_01_l", "pinky_01_l", "index_01_r", "pinky_01_r"];
    private static readonly float[] Weights = [0, 1, 1, 1, 2, 2, 3, 3, 1, 1, 2, 2, 1, 1, 2, 2, 2, 2];

    internal sealed class BodySpace(Vector3[] rest)
    {
        private readonly Vector3 _vertical = rest[1] - (rest[10] + rest[11]) * .5f;
        public Vector3[] Encode(Vector3[] joints)
        {
            var up = _vertical.Normalized();
            var side = (joints[13] - joints[12]).Slide(up).Normalized();
            if (side.LengthSquared() < .5f) side = (joints[3] - joints[2]).Slide(up).Normalized();
            if (side.LengthSquared() < .5f) side = (rest[13] - rest[12]).Slide(up).Normalized();
            float height = _vertical.Length();
            if (!(height > 1e-6f) || !side.IsFinite() || side.LengthSquared() < .5f)
                throw new InvalidDataException("无法确定人体骨架的身高和朝向。");
            var forward = side.Cross(up);
            var result = new Vector3[joints.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var p = (joints[i] - joints[0]) / height;
                if (i >= Roles.Length)
                {
                    // Two palm directions distinguish wrist rotation even when the wrist is stationary.
                    var wrist = joints[i < Roles.Length + 2 ? 6 : 7];
                    p = (joints[i] - wrist).Normalized() * .22f;
                }
                result[i] = new(p.Dot(side), p.Dot(up), p.Dot(forward));
                if (!result[i].IsFinite()) throw new InvalidDataException("动作包含无效的骨骼坐标。");
            }
            return result;
        }
    }

    internal static MotionHit? Find(MotionSamples reference, double start, double end, MotionSamples[] candidates,
        double searchSeconds, CancellationToken ct)
    {
        int first = reference.Frame(start), last = reference.Frame(end);
        int span = last - first;
        double best = double.PositiveInfinity;
        MotionHit? hit = null;
        foreach (var candidate in candidates)
        {
            int joints = Math.Min(reference.Frames[first].Length, candidate.Frames[0].Length);
            double weight = Weights.Take(joints).Sum();
            double limit = searchSeconds > 0 ? Math.Min(searchSeconds, candidate.Duration) : candidate.Duration;
            int final = candidate.Frame(limit);
            if (candidate.Time(final) - limit > 1e-9) final--;
            // Keep temporal order and permit modest differences in speed.
            foreach (var rate in span == 0 ? new[] { 1.0 } : new[] { 1.0, .8, 1.25 })
            {
                double seconds = (reference.Time(last) - reference.Time(first)) * rate;
                int width = span == 0 ? 0 : Math.Max(1, (int)Math.Round(seconds / candidate.Duration * (candidate.Frames.Length - 1)));
                int steps = Math.Min(span, 60);
                for (int i = 0; i + width <= final; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    double sum = 0;
                    for (int k = 0; k <= steps; k++)
                    {
                        double t = steps == 0 ? 0 : (double)k / steps;
                        var a = reference.Frames[first + (int)Math.Round(span * t)];
                        var b = candidate.Frames[i + (int)Math.Round(width * t)];
                        for (int j = 1; j < joints; j++)
                            sum += Weights[j] * a[j].DistanceSquaredTo(b[j]);
                        if (sum >= best * (steps + 1) * weight) break;
                    }
                    double cost = sum / ((steps + 1) * weight);
                    if (cost >= best) continue;
                    best = cost;
                    double score = 100 / (1 + 8 * Math.Sqrt(cost));
                    hit = new(candidate.Name, candidate.Time(i), candidate.Time(i + width), score);
                }
            }
        }
        return hit;
    }
}
