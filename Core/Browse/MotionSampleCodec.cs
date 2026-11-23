using Godot;

namespace Animanager.Core.Browse;

internal static class MotionSampleCodec
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    private const int Version = 2;

    internal static byte[]? Encode(MotionSamples[] clips)
    {
        if (clips.Length > 1024 || clips.Sum(c => (long)c.Frames.Length * c.Frames[0].Length * 12) > MaxBytes) return null;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Version);
        w.Write(clips.Length);
        foreach (var clip in clips)
        {
            w.Write(clip.Name);
            w.Write(clip.Duration);
            w.Write(clip.Frames.Length);
            w.Write(clip.Frames[0].Length);
            foreach (var frame in clip.Frames)
            foreach (var v in frame) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        }
        return stream.Length <= MaxBytes ? stream.ToArray() : null;
    }

    internal static MotionSamples[]? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(stream);
            if (r.ReadInt32() != Version) return null;
            int count = r.ReadInt32();
            if (count is < 0 or > 1024) return null;
            var clips = new MotionSamples[count];
            for (int c = 0; c < count; c++)
            {
                string name = r.ReadString();
                double duration = r.ReadDouble();
                int frames = r.ReadInt32(), joints = r.ReadInt32();
                if (name.Length == 0 || !double.IsFinite(duration) || duration <= 0 || frames is < 2 or > 18001 ||
                    joints != MotionSimilarity.Roles.Length && joints != MotionSimilarity.Roles.Length + MotionSimilarity.Fingers.Length ||
                    (long)frames * joints * 12 > stream.Length - stream.Position) return null;
                var values = new Vector3[frames][];
                for (int i = 0; i < frames; i++)
                {
                    values[i] = new Vector3[joints];
                    for (int j = 0; j < joints; j++)
                    {
                        values[i][j] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (!values[i][j].IsFinite()) return null;
                    }
                }
                clips[c] = new(name, duration, values);
            }
            return stream.Position == stream.Length ? clips : null;
        }
        catch (Exception ex) when (ex is IOException or FormatException) { return null; }
    }
}
