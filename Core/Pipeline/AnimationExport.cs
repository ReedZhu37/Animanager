using Godot;

namespace Animanager.Core.Pipeline;

internal static class AnimationExport
{
    internal static Animation Copy(Animation source, string clip, ExportOptions options, Func<int, bool> includeTrack,
        CancellationToken ct)
    {
        double start = options.StartSeconds ?? 0;
        double end = options.EndSeconds ?? source.Length;
        var tolerance = Math.Max(1e-6, source.Length * 1e-7);
        if (end > source.Length + tolerance || start >= source.Length)
            throw new IOException($"片段 {clip} 的时长为 {source.Length:G7} 秒，所选时间范围超出片段。");
        end = Math.Min(end, source.Length);
        var length = end - start;
        if ((float)length < 0.001f) throw new IOException($"片段 {clip} 的导出区间必须至少为 0.001 秒。");

        bool trimmed = start > 0 || end < source.Length;
        double? fps = options.SampleFps;
        var result = new Animation
        {
            Length = (float)length,
            LoopMode = trimmed ? Animation.LoopModeEnum.None : source.LoopMode,
            Step = fps is double rate ? (float)(1 / rate) : source.Step,
            ResourceName = source.ResourceName
        };
        try
        {
            for (int track = 0; track < source.GetTrackCount(); track++)
            {
                ct.ThrowIfCancellationRequested();
                if (!includeTrack(track)) continue;
                if (!trimmed && fps == null)
                {
                    source.CopyTrack(track, result);
                    continue;
                }

                var type = source.TrackGetType(track);
                if (source.TrackGetKeyCount(track) == 0) continue;
                bool interpolated = type is Animation.TrackType.Position3D or Animation.TrackType.Rotation3D
                    or Animation.TrackType.Scale3D or Animation.TrackType.BlendShape or Animation.TrackType.Value;
                var interpolation = source.TrackGetInterpolationType(track);
                int target = result.AddTrack(type);
                result.TrackSetPath(target, source.TrackGetPath(track));
                result.TrackSetEnabled(target, source.TrackIsEnabled(track));
                result.TrackSetImported(target, source.TrackIsImported(track));
                result.TrackSetInterpolationLoopWrap(target, !trimmed && source.TrackGetInterpolationLoopWrap(track));
                result.TrackSetInterpolationType(target, fps.HasValue && interpolated && interpolation != Animation.InterpolationType.Nearest
                    ? Animation.InterpolationType.Linear : interpolation);
                if (type == Animation.TrackType.Value) result.ValueTrackSetUpdateMode(target, source.ValueTrackGetUpdateMode(track));

                if (fps is double sampleRate && interpolated)
                {
                    int intervals = checked((int)Math.Ceiling(length * sampleRate));
                    for (int sample = 0; sample < intervals; sample++)
                    {
                        if (sample % 1024 == 0) ct.ThrowIfCancellationRequested();
                        double time = sample / sampleRate;
                        // GLB uses float times. Keep the exact interval endpoint without a duplicate rounded key.
                        if ((float)time >= (float)length) break;
                        result.TrackInsertKey(target, time, Interpolate(source, track, type, start + time));
                    }
                    result.TrackInsertKey(target, length, Interpolate(source, track, type, end));
                }
                else
                {
                    if (interpolated) CopyBoundary(source, track, result, target, type, start, 0);
                    for (int key = 0; key < source.TrackGetKeyCount(track); key++)
                    {
                        var time = source.TrackGetKeyTime(track, key);
                        if (interpolated ? time <= start || time >= end : time < start || time > end) continue;
                        if (interpolated && (float)(time - start) == (float)length) continue;
                        result.TrackInsertKey(target, time - start, source.TrackGetKeyValue(track, key), source.TrackGetKeyTransition(track, key));
                    }
                    if (interpolated) CopyBoundary(source, track, result, target, type, end, length);
                }
            }
            if (result.GetTrackCount() == 0) throw new IOException($"片段 {clip} 没有符合选项的动画轨道。");
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void CopyBoundary(Animation source, int track, Animation result, int target, Animation.TrackType type,
        double time, double targetTime)
    {
        var key = source.TrackFindKey(track, time, Animation.FindMode.Exact);
        if (key >= 0)
            result.TrackInsertKey(target, targetTime, source.TrackGetKeyValue(track, key), source.TrackGetKeyTransition(track, key));
        else
            result.TrackInsertKey(target, targetTime, Interpolate(source, track, type, time));
    }

    private static Variant Interpolate(Animation source, int track, Animation.TrackType type, double time) => type switch
    {
        Animation.TrackType.Position3D => source.PositionTrackInterpolate(track, time),
        Animation.TrackType.Rotation3D => source.RotationTrackInterpolate(track, time),
        Animation.TrackType.Scale3D => source.ScaleTrackInterpolate(track, time),
        Animation.TrackType.BlendShape => source.BlendShapeTrackInterpolate(track, time),
        _ => source.ValueTrackInterpolate(track, time)
    };
}
