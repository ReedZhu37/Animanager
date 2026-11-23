using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI.Components;
using Godot;
using System.Text.Json;

namespace Animanager.Core.Preview;

internal readonly record struct WeaponVector(float X, float Y, float Z)
{
    internal Vector3 Value => new(X, Y, Z);
}

internal sealed record WeaponSocket(string NodePath, string? Bone = null, string? SourceTrack = null, string? Palm = null)
{
    internal static WeaponSocket PrimaryTrack { get; } = new("", SourceTrack: MotionProp.PrimaryId);
    internal static WeaponSocket RightPalm { get; } = new("", Palm: "hand_r");
    internal static WeaponSocket LeftPalm { get; } = new("", Palm: "hand_l");

    internal WeaponSocket ResolveLegacyTrack() => Bone == null && Palm == null && (SourceTrack == MotionProp.PrimaryId ||
        NodePath == RetargetBaker.PropNodeName ||
        NodePath?.EndsWith("/PrimaryWeaponAnchor/" + RetargetBaker.PropNodeName, StringComparison.Ordinal) == true)
        ? PrimaryTrack : this;
}

internal sealed record WeaponTrailBox(WeaponVector Center, WeaponVector Size, WeaponVector Rotation)
{
    internal static WeaponTrailBox Default { get; } = new(new(0, 0, MotionPlayback.ReferenceBladeLength / 2),
        new(.035f, .008f, MotionPlayback.ReferenceBladeLength), default);
    internal Transform3D Transform => new(Basis.FromEuler(Rotation.Value * (Mathf.Pi / 180)), Center.Value);

    internal (Vector3 Base, Vector3 Tip) Edge()
    {
        var size = Size.Value;
        var length = size.X >= size.Y && size.X >= size.Z ? new Vector3(size.X, 0, 0)
            : size.Y >= size.Z ? new Vector3(0, size.Y, 0) : new Vector3(0, 0, size.Z);
        var frame = Transform;
        return (frame * (-length / 2), frame * (length / 2));
    }
}

internal sealed record WeaponMeshSettings
{
    internal static WeaponMeshSettings Default { get; } = new();
    public WeaponVector Position { get; init; }
    public WeaponVector Rotation { get; init; }
    public float Scale { get; init; } = 1;
    internal Transform3D Transform => new(
        Basis.FromEuler(Rotation.Value * (Mathf.Pi / 180)).Scaled(Vector3.One * Scale), Position.Value);

    internal void Validate()
    {
        if (!Position.Value.IsFinite() || !Rotation.Value.IsFinite() || !float.IsFinite(Scale) || Scale <= 0)
            throw new ArgumentException("网格偏移和旋转需要有限数值，缩放必须大于零。");
    }
}

internal sealed record PreviewWeapon(string Name)
{
    internal const double DefaultTrailSeconds = .24;
    internal const double MinTrailSeconds = .05;
    internal const double MaxTrailSeconds = 3;
    public string? ModelId { get; init; }
    public WeaponSocket? Socket { get; init; }
    public WeaponVector Position { get; init; }
    public WeaponVector Rotation { get; init; }
    public WeaponMeshSettings Mesh { get; init; } = WeaponMeshSettings.Default;
    public WeaponTrailBox Trail { get; init; } = WeaponTrailBox.Default;
    public double TrailSeconds { get; init; } = DefaultTrailSeconds;
    internal bool Builtin => ModelId == null;
    internal Transform3D Mount => new(Basis.FromEuler(Rotation.Value * (Mathf.Pi / 180)), Position.Value);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException("武器名称需要 1–100 个字符。");
        if (Trail == null || !Position.Value.IsFinite() || !Rotation.Value.IsFinite() || !Trail.Center.Value.IsFinite() ||
            !Trail.Size.Value.IsFinite() || !Trail.Rotation.Value.IsFinite() || Trail.Size.Value.X <= 0 ||
            Trail.Size.Value.Y <= 0 || Trail.Size.Value.Z <= 0)
            throw new ArgumentException("挂接和拖尾参数需要有限数值，盒子三轴尺寸必须大于零。");
        if (!double.IsFinite(TrailSeconds) || TrailSeconds is < MinTrailSeconds or > MaxTrailSeconds)
            throw new ArgumentException("拖尾时长需要在 0.05 到 3 秒之间。");
        if (Mesh == null) throw new ArgumentException("缺少网格预处理参数。");
        Mesh.Validate();
    }
}

internal sealed record WeaponProfile(string Id, string Name)
{
    internal const string BuiltinId = "reference";
    internal const string DualId = "reference-dual";
    public PreviewWeapon[] Weapons { get; init; } = [new("参考刀")];

    internal static WeaponProfile Reference => new(BuiltinId, "通用参考刀");
    internal static WeaponProfile Dual => new(DualId, "通用双刀")
    {
        Weapons = [new("右手刀") { Socket = WeaponSocket.RightPalm }, new("左手刀") { Socket = WeaponSocket.LeftPalm }]
    };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException("Profile 名称需要 1–100 个字符。");
        if (Weapons == null || Weapons.Length == 0) throw new ArgumentException("请为 Profile 添加至少一件武器。");
        foreach (var weapon in Weapons) weapon.Validate();
    }
}

internal sealed class WeaponProfileLibrary(string root)
{
    private sealed record LibraryState(string Selected, WeaponProfile[] Profiles);
    private sealed record StoredLibrary(string Selected, JsonElement[] Profiles);
    private string Catalog => Path.Combine(root, "profiles.json");
    private LibraryState Read()
    {
        if (!File.Exists(Catalog)) return new(WeaponProfile.BuiltinId, [WeaponProfile.Reference, WeaponProfile.Dual]);
        var stored = MotionJson.Read<StoredLibrary>(Catalog);
        var profiles = stored.Profiles.Select(item =>
        {
            var profile = item.Deserialize<WeaponProfile>(MotionJson.Options) ?? throw new InvalidDataException("武器 Profile 为空。");
            if (item.TryGetProperty("weapons", out _) || item.TryGetProperty("Weapons", out _)) return profile;
            // Older profiles stored the only weapon directly and named its model file after the profile ID.
            var weapon = item.Deserialize<PreviewWeapon>(MotionJson.Options)!;
            return profile with { Weapons = [weapon with { ModelId = profile.Id == WeaponProfile.BuiltinId ? null : profile.Id }] };
        }).ToList();
        if (profiles.All(profile => profile.Id != WeaponProfile.BuiltinId)) profiles.Insert(0, WeaponProfile.Reference);
        if (profiles.All(profile => profile.Id != WeaponProfile.DualId)) profiles.Add(WeaponProfile.Dual);
        return new(stored.Selected, profiles.ToArray());
    }
    internal WeaponProfile[] List() => Read().Profiles;
    internal WeaponProfile Active()
    {
        var state = Read();
        return state.Profiles.FirstOrDefault(profile => profile.Id == state.Selected) ?? state.Profiles[0];
    }

    internal string? ModelPath(PreviewWeapon weapon)
    {
        if (weapon.ModelId == null) return null;
        if (!Guid.TryParseExact(weapon.ModelId, "N", out _)) throw new InvalidDataException("武器模型 ID 无效。");
        return Path.Combine(root, weapon.ModelId + ".glb");
    }

    internal void Use(WeaponProfile profile)
    {
        profile.Validate();
        var state = Read();
        var profiles = state.Profiles.Where(item => item.Id != profile.Id).Append(profile)
            .OrderBy(item => item.Id == WeaponProfile.BuiltinId ? 0 : item.Id == WeaponProfile.DualId ? 1 : 2)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
        MotionJson.Atomic(Catalog, new LibraryState(profile.Id, profiles));
    }

    internal PreviewWeapon Install(string source)
    {
        if (Path.GetExtension(source).ToLowerInvariant() is not (".glb" or ".gltf" or ".fbx"))
            throw new NotSupportedException("请选择 FBX、GLB 或 glTF 武器模型。");
        var weapon = new PreviewWeapon(Path.GetFileNameWithoutExtension(source)) { ModelId = Guid.NewGuid().ToString("N") };
        weapon.Validate();
        var scene = SceneAsset.Load(source, out _, forExport: true);
        string model = ModelPath(weapon)!;
        string temporary = model + ".tmp.glb";
        try
        {
            var nodes = Preview3DViewport.Descendants(scene).ToArray();
            if (!nodes.OfType<MeshInstance3D>().Any(node => node.Mesh != null)) throw new NotSupportedException("武器模型没有可显示的网格。");
            foreach (var player in nodes.OfType<AnimationPlayer>()) player.Free();
            Directory.CreateDirectory(root);
            ExportService.WriteScene(temporary, scene, null, false, 0);
            File.Move(temporary, model);
            return weapon;
        }
        finally
        {
            scene.Free();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
