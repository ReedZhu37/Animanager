using System.Numerics;
using System.Text.Json.Nodes;
using Animanager.Core.Motion;

namespace Animanager.Core.Plugins;

internal sealed record CascadeurBindJoint(string[] Path, int Parent, Vector3 Position, Quaternion Rotation);
internal sealed record CascadeurWireJoint(string[] Path, float[] Position, float[] Rotation);

internal sealed record CascadeurBindPose(CascadeurBindJoint[] Joints)
{
    public static CascadeurBindPose Empty { get; } = new([]);

    public bool? IsUpright(Quaternion orientation)
    {
        Vector3? Point(string name) => Joints.FirstOrDefault(joint =>
            joint.Path[^1].Replace("_", "").Equals(name, StringComparison.OrdinalIgnoreCase))?.Position;
        var head = Point("head");
        var left = Point("footl") ?? Point("leftfoot");
        var right = Point("footr") ?? Point("rightfoot");
        if (head is not { } top || left is not { } footL || right is not { } footR) return null;
        var vertical = Vector3.Transform(top - (footL + footR) / 2, orientation);
        return vertical.LengthSquared() > 0.0001f && Vector3.Normalize(vertical).Y >= 0.85f;
    }

    public CascadeurWireJoint[] ToTransfer(Quaternion orientation) => Joints.Select(joint =>
    {
        var position = Vector3.Transform(joint.Position, orientation);
        var rotation = Quaternion.Normalize(orientation * joint.Rotation);
        return new CascadeurWireJoint(joint.Path, [position.X, position.Y, position.Z],
            [rotation.X, rotation.Y, rotation.Z, rotation.W]);
    }).ToArray();

    public static Quaternion MapAxes(Vector3 up, Vector3 forward)
    {
        if (Vector3.Dot(up, forward) != 0) throw new ArgumentException("向上轴和朝前轴必须垂直。");
        var right = Vector3.Cross(up, forward);
        var source = new Matrix4x4(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(source)));
    }

    public static CascadeurBindPose Read(string path)
    {
        var (document, binary) = MotionGlb.Unpack(File.ReadAllBytes(path));
        if (document["nodes"] is not JsonArray nodes || document["skins"] is not JsonArray skins) return Empty;
        var candidates = nodes.Select((node, index) => (Node: node, Index: index))
            .Where(item => item.Node?["mesh"] != null && item.Node["skin"] != null)
            .Select(item => (Mesh: item.Index, Skin: skins[item.Node!["skin"]!.GetValue<int>()]!))
            .Where(item => item.Skin["inverseBindMatrices"] != null)
            .OrderByDescending(item => item.Skin["joints"]!.AsArray().Count).ToArray();
        if (candidates.Length == 0) return Empty;

        var (mesh, skin) = candidates[0];
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (int index = 0; index < nodes.Count; index++)
        {
            foreach (var child in nodes[index]?["children"] as JsonArray ?? [])
            {
                int childIndex = child!.GetValue<int>();
                if (childIndex < 0 || childIndex >= nodes.Count || parents[childIndex] != -1)
                    throw new InvalidDataException("GLB 节点层级无效。");
                parents[childIndex] = index;
            }
        }

        var world = LocalMatrix(nodes[mesh]!);
        int ancestorCount = 0;
        for (int parent = parents[mesh]; parent >= 0; parent = parents[parent])
        {
            if (++ancestorCount > nodes.Count) throw new InvalidDataException("GLB 节点层级有循环。");
            world *= LocalMatrix(nodes[parent]!);
        }

        var indices = skin["joints"]!.AsArray().Select(joint => joint!.GetValue<int>()).ToArray();
        var jointOrder = indices.Select((node, order) => (node, order)).ToDictionary(item => item.node, item => item.order);
        var accessor = document["accessors"]![skin["inverseBindMatrices"]!.GetValue<int>()]!;
        var view = document["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        const int matrixBytes = 16 * sizeof(float);
        long start = (long)(view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = view["byteStride"]?.GetValue<int>() ?? matrixBytes;
        if (accessor["type"]?.GetValue<string>() != "MAT4" || accessor["componentType"]?.GetValue<int>() != 5126 ||
            accessor["count"]?.GetValue<int>() != indices.Length || accessor["sparse"] != null ||
            view["buffer"]?.GetValue<int>() != 0 || stride < matrixBytes || start < 0 ||
            start + (long)(indices.Length - 1) * stride + matrixBytes > binary.Length)
            throw new InvalidDataException("GLB 蒙皮绑定矩阵无效。");

        var joints = new CascadeurBindJoint[indices.Length];
        for (int order = 0; order < indices.Length; order++)
        {
            int node = indices[order];
            var names = new List<string>();
            int parentOrder = -1;
            for (int parent = node, depth = 0; parent >= 0; parent = parents[parent])
            {
                if (++depth > nodes.Count) throw new InvalidDataException("GLB 骨架层级有循环。");
                if (!jointOrder.TryGetValue(parent, out int joint)) continue;
                if (parent != node && parentOrder < 0) parentOrder = joint;
                names.Add(nodes[parent]?["name"]?.GetValue<string>() ?? throw new InvalidDataException("GLB 骨骼缺少名称。"));
            }
            names.Reverse();

            int offset = checked((int)(start + order * (long)stride));
            var inverseBind = Matrix(Enumerable.Range(0, 16).Select(i => BitConverter.ToSingle(binary, offset + i * 4)).ToArray());
            // glTF inverse bind matrices relate the skinned mesh's local coordinates to the joint.
            // System.Numerics uses row vectors, so apply the full mesh world transform on the right.
            if (!Matrix4x4.Invert(inverseBind, out var bind) ||
                !Matrix4x4.Decompose(bind * world, out _, out var rotation, out var position) ||
                !float.IsFinite(position.LengthSquared()) || !float.IsFinite(rotation.LengthSquared()))
                throw new InvalidDataException("GLB 绑定姿态无法转换到场景坐标。");
            joints[order] = new(names.ToArray(), parentOrder, position, Quaternion.Normalize(rotation));
        }
        return new(joints);
    }

    // An extra parent preserves animated root TRS channels while rotating the full exported scene.
    public static void RotateExport(string path, Quaternion orientation)
    {
        if (orientation == Quaternion.Identity) return;
        var (document, binary) = MotionGlb.Unpack(File.ReadAllBytes(path));
        var nodes = document["nodes"]!.AsArray();
        foreach (var scene in document["scenes"]!.AsArray())
        {
            if (scene?["nodes"] is not JsonArray { Count: > 0 } roots) continue;
            int index = nodes.Count;
            nodes.Add(new JsonObject
            {
                ["name"] = "AnimanagerOrientation",
                ["rotation"] = new JsonArray(orientation.X, orientation.Y, orientation.Z, orientation.W),
                ["children"] = roots.DeepClone()
            });
            scene["nodes"] = new JsonArray(index);
        }
        var staged = path + ".orientation.tmp";
        try
        {
            File.WriteAllBytes(staged, MotionGlb.Pack(document, binary));
            File.Move(staged, path, overwrite: true);
        }
        finally { File.Delete(staged); }
    }

    private static Matrix4x4 LocalMatrix(JsonNode node)
    {
        if (node["matrix"] is JsonArray matrix) return Matrix(Floats(matrix));
        var p = Floats(node["translation"], [0, 0, 0]);
        var r = Floats(node["rotation"], [0, 0, 0, 1]);
        var s = Floats(node["scale"], [1, 1, 1]);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(MotionGlb.QuaternionOf(r)) *
               Matrix4x4.CreateTranslation(p[0], p[1], p[2]);
    }

    private static float[] Floats(JsonNode? node, float[]? fallback = null) =>
        node?.AsArray().Select(value => value!.GetValue<float>()).ToArray() ?? fallback!;

    private static Matrix4x4 Matrix(float[] m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
}
