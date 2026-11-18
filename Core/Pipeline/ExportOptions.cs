using System.Globalization;
using System.Text.Json.Serialization;

namespace Animanager.Core.Pipeline;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportOptions
{
    public bool IncludeMesh { get; init; } = true;
    public string[]? Clips { get; init; }
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public double? SampleFps { get; init; }
    public Animanager.Core.Retarget.RetargetOptions? Retarget { get; init; }

    internal bool HasTimeOptions => StartSeconds.HasValue || EndSeconds.HasValue || SampleFps.HasValue;

    internal ExportOptions Snapshot(bool animationLibrary)
    {
        if (Clips != null && (Clips.Length == 0 || Clips.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("请选择至少一个有效的动画片段。");
        if (StartSeconds is double start && (!double.IsFinite(start) || start < 0))
            throw new ArgumentException("起始时间必须是非负秒数。");
        if (EndSeconds is double end && (!double.IsFinite(end) || end <= (StartSeconds ?? 0)))
            throw new ArgumentException("结束时间必须大于起始时间。");
        if (SampleFps is double fps && (!double.IsFinite(fps) || fps is < 1 or > 600))
            throw new ArgumentException("采样帧率必须在 1–600 FPS 之间。");

        return this with { IncludeMesh = !animationLibrary && IncludeMesh, Clips = Clips?.Distinct(StringComparer.Ordinal).ToArray(), Retarget = Retarget?.Snapshot() };
    }

    public string Describe()
    {
        string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
        var clips = Clips == null ? "全部片段" : string.Join("、", Clips);
        var interval = StartSeconds.HasValue || EndSeconds.HasValue
            ? $"{Number(StartSeconds ?? 0)} 秒至 {(EndSeconds is double end ? Number(end) + " 秒" : "片段末尾")}" : "完整时长";
        var rate = SampleFps is double fps ? Number(fps) + " FPS" : Retarget != null ? "默认重定向采样" : "原采样";
        return $"{(IncludeMesh ? "包含网格" : "无网格")} · {clips} · {interval} · {rate}" + (Retarget is {} r ? $" · Retarget → {r.Character?.Name ?? r.TargetRig}{(r.InPlace ? " · 原地" : "")}" : "");
    }
}
