using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Animanager.Core.Motion;

namespace Animanager.Core.Browse;

internal static class AssetAttributeReader
{
    internal static AssetAttributes Read(string path, string format, CancellationToken cancellationToken) => format.ToLowerInvariant() switch
    {
        ".glb" or ".gltf" => ReadGltf(path),
        ".fbx" => FbxAttributeReader.Read(path, cancellationToken),
        _ => AssetAttributes.Unknown
    };

    internal static AssetSkeleton[] ReadMotionSkeleton(string path)
    {
        var rig = MotionJson.Read<MotionRig>(path);
        return rig.SchemaVersion == 1 && rig.Bones is { Length: > 0 } && Profile(rig.Id) is { } skeleton ? [skeleton] : [];
    }

    private static AssetAttributes ReadGltf(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var document = ReadGltfJson(stream, path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
        var root = document.RootElement;
        if (String(Get(Get(root, "asset"), "version")) != "2.0") return AssetAttributes.Unknown;
        var extras = Get(root, "extras");
        var animations = Get(root, "animations");
        var animationCount = animations.ValueKind == JsonValueKind.Array ? animations.GetArrayLength() : 0;
        var sourceFps = animationCount == 0 ? null : PositiveNumber(Get(extras, "animanagerSourceFps"));
        var skeletons = Profile(String(Get(extras, "skeletonProfile"))) is { } profile ? [profile] : GltfSkeletons(root);
        return new(skeletons, GltfDuration(animations, Get(root, "accessors")), sourceFps);
    }

    private static JsonDocument ReadGltfJson(FileStream stream, bool binary)
    {
        if (!binary) return JsonDocument.Parse(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != stream.Length)
            throw new InvalidDataException("Invalid GLB header.");
        var length = reader.ReadInt32();
        if (reader.ReadUInt32() != 0x4E4F534A || length <= 0 || length > 64 * 1024 * 1024 || length > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid GLB JSON chunk.");
        return JsonDocument.Parse(reader.ReadBytes(length));
    }

    private static double? GltfDuration(JsonElement animations, JsonElement accessors)
    {
        if (animations.ValueKind != JsonValueKind.Array || accessors.ValueKind != JsonValueKind.Array) return null;
        double? duration = null;
        foreach (var animation in animations.EnumerateArray())
        {
            var channels = Get(animation, "channels");
            var samplers = Get(animation, "samplers");
            if (channels.ValueKind != JsonValueKind.Array || samplers.ValueKind != JsonValueKind.Array) continue;
            foreach (var channel in channels.EnumerateArray())
            {
                if (!ArrayIndex(Get(channel, "sampler"), samplers.GetArrayLength(), out var sampler) ||
                    !ArrayIndex(Get(samplers[sampler], "input"), accessors.GetArrayLength(), out var input)) continue;
                var accessor = accessors[input];
                var max = Get(accessor, "max");
                if (String(Get(accessor, "type")) != "SCALAR" || max.ValueKind != JsonValueKind.Array || max.GetArrayLength() != 1 ||
                    max[0].ValueKind != JsonValueKind.Number || !max[0].TryGetDouble(out var end) || !double.IsFinite(end) || end < 0) continue;
                // glTF animation input times are relative to the start of the animation (t = 0).
                duration = Math.Max(duration ?? 0, end);
            }
        }
        return duration;
    }

    private static AssetSkeleton[] GltfSkeletons(JsonElement root)
    {
        var nodes = Get(root, "nodes");
        var skins = Get(root, "skins");
        if (nodes.ValueKind != JsonValueKind.Array || skins.ValueKind != JsonValueKind.Array) return [];
        var parents = new int[nodes.GetArrayLength()];
        Array.Fill(parents, -1);
        for (int i = 0; i < parents.Length; i++)
        {
            var children = Get(nodes[i], "children");
            if (children.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in children.EnumerateArray())
                if (ArrayIndex(child, parents.Length, out var index)) parents[index] = i;
        }

        var groups = new List<HashSet<int>>();
        foreach (var skin in skins.EnumerateArray())
        {
            var joints = Get(skin, "joints");
            if (joints.ValueKind != JsonValueKind.Array) continue;
            var bones = new HashSet<int>();
            foreach (var joint in joints.EnumerateArray())
                if (ArrayIndex(joint, parents.Length, out var index)) bones.Add(index);
            if (bones.Count == 0) continue;
            // A rig can be used by multiple meshes whose skins bind different subsets of its joints.
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (!bones.Overlaps(groups[i])) continue;
                bones.UnionWith(groups[i]);
                groups.RemoveAt(i);
            }
            groups.Add(bones);
        }
        string Name(int index) => String(Get(nodes[index], "name")) ?? $"joint-{index}";
        return groups.Select(bones => SkeletonOf(bones.Select(i => (Name(i), bones.Contains(parents[i]) ? Name(parents[i]) : ""))))
            .DistinctBy(s => s.Id).ToArray();
    }

    private static AssetSkeleton? Profile(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || AssetAttributeFilter.IsUnknown(id) || id.Length > 96 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) return null;
        return new(id, id);
    }

    internal static AssetSkeleton SkeletonOf(IEnumerable<(string Bone, string Parent)> entries)
    {
        var bones = entries.OrderBy(x => x.Bone, StringComparer.Ordinal).ThenBy(x => x.Parent, StringComparer.Ordinal).ToArray();
        var root = Array.Find(bones, x => x.Parent.Length == 0).Bone ?? bones[0].Bone;
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bones.Select(b => new[] { b.Bone, b.Parent })))).ToLowerInvariant()[..12];
        return new("rig-" + hash, $"{root} · {bones.Length} 骨骼 ({hash[..6]})");
    }

    private static JsonElement Get(JsonElement node, string property) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) ? value : default;

    private static string? String(JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static double? PositiveNumber(JsonElement node) => node.ValueKind == JsonValueKind.Number &&
        node.TryGetDouble(out var value) && double.IsFinite(value) && value > 0 ? value : null;

    private static bool ArrayIndex(JsonElement node, int length, out int value)
    {
        value = -1;
        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value) && value >= 0 && value < length;
    }

}
