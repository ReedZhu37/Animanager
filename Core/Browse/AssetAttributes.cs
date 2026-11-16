using System.Globalization;

namespace Animanager.Core.Browse;

public sealed record AssetSkeleton(string Id, string Name);

/// <summary>Duration is the longest clip in the asset. A null numeric field means unknown.</summary>
public sealed record AssetAttributes(AssetSkeleton[] Skeletons, double? Duration, double? SourceFps)
{
    public static AssetAttributes Unknown { get; } = new([], null, null);
}

public sealed record AssetNumberFilter(double? Minimum = null, double? Maximum = null, bool UnknownOnly = false)
{
    public bool IsActive => UnknownOnly || Minimum.HasValue || Maximum.HasValue;

    internal void Validate(string field)
    {
        static bool Valid(double? value) => value == null || double.IsFinite(value.Value) && value.Value >= 0;
        if (!Valid(Minimum) || !Valid(Maximum) || Minimum > Maximum || UnknownOnly && (Minimum.HasValue || Maximum.HasValue))
            throw new ArgumentException(field + "范围无效。");
    }

    internal bool Matches(double? value)
    {
        if (!IsActive) return true;
        if (UnknownOnly) return value == null;
        if (value is not { } number) return false;
        // Keep whole-number boundaries useful with source times stored as float32.
        var tolerance = Math.Max(1e-7, Math.Abs(number) * 1e-6);
        return (Minimum == null || number >= Minimum.Value - tolerance) &&
            (Maximum == null || number <= Maximum.Value + tolerance);
    }

    internal static AssetNumberFilter? Parse(string text, string field)
    {
        text = text.Trim();
        if (text is "" or "*") return null;
        if (AssetAttributeFilter.IsUnknown(text)) return new(UnknownOnly: true);
        double Number(string part) => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException(field + "请输入数字、范围或“未知”。");
        var parts = text.Split("..", StringSplitOptions.TrimEntries);
        var filter = parts.Length switch
        {
            1 => new AssetNumberFilter(Number(parts[0]), Number(parts[0])),
            2 when parts[0].Length > 0 || parts[1].Length > 0 => new AssetNumberFilter(
                parts[0].Length == 0 ? null : Number(parts[0]), parts[1].Length == 0 ? null : Number(parts[1])),
            _ => throw new ArgumentException(field + "请输入数字、范围或“未知”。")
        };
        filter.Validate(field);
        return filter;
    }
}

public sealed record AssetAttributeFilter(string? SkeletonId = null, bool UnknownSkeleton = false,
    AssetNumberFilter? Duration = null, AssetNumberFilter? SourceFps = null)
{
    public bool IsActive => SkeletonId != null || UnknownSkeleton || Duration?.IsActive == true || SourceFps?.IsActive == true;

    public static AssetAttributeFilter Parse(string skeleton, string duration, string sourceFps)
    {
        skeleton = skeleton.Trim();
        bool unknown = IsUnknown(skeleton);
        return new(skeleton is "" or "*" || unknown ? null : skeleton, unknown,
            AssetNumberFilter.Parse(duration, "时长"), AssetNumberFilter.Parse(sourceFps, "源帧率"));
    }

    internal static bool IsUnknown(string value) => value.Equals("unknown", StringComparison.OrdinalIgnoreCase) || value == "未知";

    internal void Validate()
    {
        if (SkeletonId != null && (UnknownSkeleton || string.IsNullOrWhiteSpace(SkeletonId)))
            throw new ArgumentException("骨架筛选条件无效。");
        Duration?.Validate("时长");
        SourceFps?.Validate("源帧率");
    }

    internal bool Matches(AssetAttributes attributes) =>
        (UnknownSkeleton ? attributes.Skeletons.Length == 0 : SkeletonId == null ||
            attributes.Skeletons.Any(s => s.Id.Equals(SkeletonId, StringComparison.OrdinalIgnoreCase))) &&
        Duration?.Matches(attributes.Duration) != false && SourceFps?.Matches(attributes.SourceFps) != false;
}
