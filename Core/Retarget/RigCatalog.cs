using Godot;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;

namespace Animanager.Core.Retarget;

public sealed class RigDescriptor
{
    public string Id { get; internal set; } = "";
    public string Fingerprint { get; }
    public string[] Names { get; }
    public string[] Roles { get; }
    public int[] Parents { get; }
    public Transform3D[] Rest { get; }
    public Transform3D[] GlobalRest { get; }
    public Transform3D Frame { get; }
    public Dictionary<string, int> Indices { get; }
    public int[] Order { get; }
    public RigDescriptor(Skeleton3D skeleton, Node3D root)
    {
        Names = Enumerable.Range(0, skeleton.GetBoneCount()).Select(i => skeleton.GetBoneName(i).ToString()).ToArray();
        Roles = Names.ToArray();
        Parents = Enumerable.Range(0, Names.Length).Select(skeleton.GetBoneParent).ToArray();
        Rest = Enumerable.Range(0, Names.Length).Select(skeleton.GetBoneRest).ToArray();
        Indices = Names.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i, StringComparer.Ordinal);
        Frame = SceneFrame(skeleton, root);
        var order = new List<int>(); var visiting = new HashSet<int>(); var done = new HashSet<int>();
        void Visit(int i)
        {
            if (done.Contains(i)) return;
            if (!visiting.Add(i) || Parents[i] >= Names.Length) throw new IOException("骨架层级包含循环或无效父节点。");
            if (Parents[i] >= 0) Visit(Parents[i]);
            visiting.Remove(i); done.Add(i); order.Add(i);
        }
        for (int i = 0; i < Names.Length; i++) Visit(i);
        Order = order.ToArray();
        GlobalRest = Globals(Rest);
        foreach (var t in GlobalRest)
            if (!t.IsFinite() || t.Basis.Determinant() <= 0 || !Uniform(t.Basis.Scale))
                throw new NotSupportedException("Retarget 不支持镜像、奇异变换或非均匀骨架缩放。");
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", Order.Select(i =>
            Names[i] + ":" + (Parents[i] < 0 ? "" : Names[Parents[i]]) + ":" + Convert.ToBase64String(GD.VarToBytes(Rest[i]))))))).ToLowerInvariant();
    }
    internal static Transform3D SceneFrame(Skeleton3D skeleton, Node3D root)
    {
        var frame = skeleton.Transform;
        if (skeleton != root)
            for (var node = skeleton.GetParent(); node is Node3D parent; node = node.GetParent())
            {
                frame = parent.Transform * frame;
                if (parent == root) break;
            }
        return frame;
    }
    internal static bool Uniform(Vector3 s) => Math.Abs(s.X - s.Y) < 1e-4f && Math.Abs(s.Y - s.Z) < 1e-4f;
    public Transform3D[] Globals(Transform3D[] local)
    {
        var result = new Transform3D[local.Length];
        foreach (int i in Order) result[i] = (Parents[i] < 0 ? Frame : result[Parents[i]]) * local[i];
        return result;
    }
}

public static class RigCatalog
{
    public static readonly string[] Ids = ["ue4-mannequin", "ue5-mannequin"];
    public static readonly string[] BodyIds = ["ue4-mannequin", "ue5-mannequin", "ue4-female", "ue5-quinn"];
    public static string Family(string id) => id == "ue4-female" ? Ids[0] : id == "ue5-quinn" ? Ids[1] : id;
    public static string ModelPath(string id) => FilePath(id, true);
    private sealed class Profile { public string id { get; set; } = ""; public Bone[] bones { get; set; } = []; }
    private sealed class Bone { public string name { get; set; } = ""; public int parent { get; set; } public float[] p { get; set; } = []; public float[] r { get; set; } = []; public float[] s { get; set; } = []; }
    private static string FilePath(string id, bool mesh = false)
    {
        if (!BodyIds.Contains(id)) throw new ArgumentException("Unknown standard rig: " + id);
        if (mesh && id is "ue4-female" or "ue5-quinn")
            return RuntimePaths.Bundled("BuiltinAssets/Baselines/" + (id == "ue4-female" ? "UE4_Mannequin_Female.fbx" : "UE5_Quinn.fbx"));
        id = Family(id);
        return RuntimePaths.Bundled("BuiltinAssets/Retarget/" + (mesh ? (id == Ids[0] ? "UE4_Mannequin.glb" : "UE5_Manny.glb") : id + ".json"));
    }
    public static string Hash(string id)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("animanager-retarget-fk-v2-ready1"));
        hash.AppendData(File.ReadAllBytes(FilePath(id))); hash.AppendData(File.ReadAllBytes(FilePath(id, true)));
        if (id == "ue5-mannequin") hash.AppendData(File.ReadAllBytes(RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx")));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static Node3D Load(string id, out Skeleton3D skeleton)
    {
        var root = SceneAsset.Load(FilePath(id, true), out _, forExport: true);
        try
        {
            skeleton = Preview3DViewport.Descendants(root).OfType<Skeleton3D>().Single();
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(FilePath(id)))!;
            foreach (var b in profile.bones)
            {
                if (skeleton.FindBone(b.name) >= 0) continue;
                int i = skeleton.AddBone(b.name);
                skeleton.SetBoneParent(i, b.parent < 0 ? -1 : skeleton.FindBone(profile.bones[b.parent].name));
                var rest = new Transform3D(new Basis(new Quaternion(-b.r[0], b.r[1], -b.r[2], b.r[3]).Normalized())
                    .Scaled(new(b.s[0], b.s[1], b.s[2])), new(b.p[0] * .01f, -b.p[1] * .01f, b.p[2] * .01f));
                skeleton.SetBoneRest(i, rest); skeleton.SetBonePose(i, rest);
            }
            return root;
        }
        catch { root.Free(); throw; }
    }
    public static RigDescriptor Identify(Skeleton3D skeleton, Node3D root)
    {
        var rig = new RigDescriptor(skeleton, root);
        var reasons = new List<string>();
        foreach (var (id, variant) in new[] { (Ids[0], false), (Ids[1], false), (Ids[1], true) })
        {
            var reference = variant ? SceneAsset.Load(RuntimePaths.Bundled("BuiltinAssets/Baselines/UE5_Manny_Idle.fbx"), out _, forExport: true) : Load(id, out _);
            try
            {
                var sk = Preview3DViewport.Descendants(reference).OfType<Skeleton3D>().Single();
                var expected = new RigDescriptor(sk, reference);
                if (!rig.Indices.ContainsKey("pelvis") || !rig.Indices.ContainsKey("root") || rig.Names.Length < (id == Ids[0] ? 68 : 89)) { reasons.Add(id + ": 缺少标准骨骼，实际 " + rig.Names.Length); continue; }
                if (rig.Names.Any(n => !expected.Indices.ContainsKey(n))) { reasons.Add(id + ": 未知骨骼 " + string.Join(",", rig.Names.Where(n => !expected.Indices.ContainsKey(n)).Take(4))); continue; }
                if (new[] { "head", "hand_l", "hand_r", "foot_l", "foot_r" }.Any(n => !rig.Indices.ContainsKey(n))) continue;
                bool matches = rig.Order.All(i =>
                {
                    int e = expected.Indices[rig.Names[i]];
                    string Parent(RigDescriptor r, int b) => r.Parents[b] < 0 ? "" : r.Names[r.Parents[b]];
                    // Joint positions validate the silhouette and proportions while allowing bone-roll differences from FBX.
                    float distance = (rig.GlobalRest[i].Origin - rig.GlobalRest[rig.Indices["root"]].Origin).DistanceTo(
                            expected.GlobalRest[e].Origin - expected.GlobalRest[expected.Indices["root"]].Origin);
                    bool valid = Parent(rig, i) == Parent(expected, e) && distance < .001f;
                    if (!valid) reasons.Add($"{id}: {rig.Names[i]} 父节点 {Parent(rig, i)} / {Parent(expected, e)}，参考位置差 {distance:G5} 米");
                    return valid;
                });
                if (matches) { rig.Id = id; return rig; }
            }
            finally { reference.Free(); }
        }
        throw new NotSupportedException("无法识别标准 UE4 / UE5 骨架：名称、层级、参考姿势或比例不匹配。" + string.Join("; ", reasons));
    }
}
