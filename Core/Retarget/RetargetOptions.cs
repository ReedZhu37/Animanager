using System.Text.Json.Serialization;
using Animanager.Core.Motion;

namespace Animanager.Core.Retarget;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetargetOptions
{
    public int Version { get; init; } = 1;
    public string TargetRig { get; init; } = "ue5-mannequin";
    public string TargetHash { get; init; } = "";
    public string SourceRig { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public CharacterReference? Character { get; init; }
    public bool InPlace { get; init; }
    public float RootScale { get; init; } = 1;
    public float HipHeight { get; init; }
    public bool IncludeProps { get; init; } = true;
    // Degrees in target bone-local space; intentionally separate from the immutable bind pose.
    public Dictionary<string, float[]> RotationOffsets { get; init; } = new();

    public RetargetOptions Snapshot()
    {
        if (Version != 1 || !RigCatalog.BodyIds.Contains(TargetRig) ||
            (SourceRig.Length > 0 && !RigCatalog.Ids.Contains(SourceRig) && !(SourceRig.StartsWith("humanoid:") && SourceRig.Length == 73 && SourceRig[9..].All(Uri.IsHexDigit))))
            throw new ArgumentException("Retarget 需要标准骨架或通过 Ready-v1 检查的人体映射。");
        if (!float.IsFinite(RootScale) || RootScale is < 0 or > 10 || !float.IsFinite(HipHeight) || Math.Abs(HipHeight) > 2)
            throw new ArgumentException("位移倍率必须为 0–10，骨盆高度偏移必须为 -2–2 米。");
        if (RotationOffsets == null || RotationOffsets.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null ||
            p.Value.Length != 3 || p.Value.Any(v => !float.IsFinite(v) || Math.Abs(v) > 180)))
            throw new ArgumentException("旋转补偿需要三个 -180–180 度的有限数值。");
        var hash = RigCatalog.Hash(TargetRig);
        if (TargetHash.Length > 0 && TargetHash != hash) throw new IOException("目标骨架版本已变化，请重新确认 Retarget 预设。");
        string sourceHash = SourceRig.Length == 0 ? "" : SourceRig.StartsWith("humanoid:") ? SourceRig[9..] : RigCatalog.Hash(SourceRig);
        if (SourceHash.Length > 0 && SourceHash != sourceHash)
            throw new IOException("源骨架预设版本已变化，请重新确认映射。");
        return this with { TargetHash = hash, SourceHash = sourceHash, Character = Character?.Snapshot(),
            RotationOffsets = RotationOffsets.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal) };
    }
}

public sealed record RetargetPreset(string Id, string Name, RetargetOptions Options);

/// <summary>Library-local configuration; no derived asset or changes to source motion manifests.</summary>
public sealed class RetargetPresetStore(string libraryRoot)
{
    private readonly string _folder = Path.Combine(libraryRoot, "retarget");
    private string PresetPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid retarget preset ID.");
        return Path.Combine(_folder, id + ".json");
    }
    public RetargetPreset[] List() => Directory.Exists(_folder)
        ? Directory.GetFiles(_folder, "*.json").Where(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p), "N", out _))
            .Select(MotionJson.Read<RetargetPreset>).OrderBy(p => p.Name).ToArray() : [];
    public RetargetPreset Save(string name, RetargetOptions options, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("预设名称需要 1–100 个字符。");
        var preset = new RetargetPreset(id ?? Guid.NewGuid().ToString("N"), name.Trim(), options.Snapshot());
        MotionJson.Atomic(PresetPath(preset.Id), preset);
        return preset;
    }
    public void Associate(string assetId, RetargetOptions options)
    {
        if (!Guid.TryParse(assetId, out _)) throw new ArgumentException("Invalid asset ID.");
        MotionJson.Atomic(Path.Combine(_folder, "assets", assetId + ".json"), options.Snapshot());
    }
    public RetargetOptions? ForAsset(string assetId)
    {
        if (!Guid.TryParse(assetId, out _)) return null;
        var path = Path.Combine(_folder, "assets", assetId + ".json");
        return File.Exists(path) ? MotionJson.Read<RetargetOptions>(path).Snapshot() : null;
    }
}
