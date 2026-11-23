using Godot;
using System.Text.RegularExpressions;

namespace Animanager.Core.Retarget;

/// <summary>Ready-v1 is a deterministic structural contract, never a judgement of motion quality.</summary>
public static class HumanoidMap
{
    public static readonly string[] Required = ["pelvis", "spine_01", "head", "upperarm_l", "lowerarm_l", "hand_l",
        "upperarm_r", "lowerarm_r", "hand_r", "thigh_l", "calf_l", "foot_l", "thigh_r", "calf_r", "foot_r"];
    private static readonly Dictionary<string, string> Aliases = BuildAliases();
    internal static string Role(string name) => Aliases.GetValueOrDefault(Normalize(name), name);
    private static string Normalize(string value)
    {
        value = value.Split(':', '|').Last().ToLowerInvariant();
        value = Regex.Replace(value, "^(mixamorig[0-9]*|bip[0-9]*|def)[ _.-]*", "");
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }
    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>();
        void Add(string role, params string[] names) { map[Normalize(role)] = role; foreach (var name in names) map[Normalize(name)] = role; }
        Add("root", "armature", "rootmotion"); Add("pelvis", "hips", "hip"); Add("head");
        Add("spine_01", "spine"); Add("spine_02", "spine1", "chest"); Add("spine_03", "spine2", "upperchest");
        Add("spine_04", "spine3"); Add("spine_05", "spine4"); Add("neck_01", "neck"); Add("neck_02", "neck1");
        foreach (var (side, word) in new[] { ("l", "left"), ("r", "right") })
        {
            foreach (var (role, alias) in new[] { ("clavicle", "shoulder"), ("upperarm", "arm"), ("lowerarm", "forearm"),
                         ("hand", "hand"), ("thigh", "upleg"), ("calf", "leg"), ("foot", "foot"), ("ball", "toebase") })
                Add(role + "_" + side, word + alias, side + role, alias + side, word + role);
            Add("thigh_" + side, word + "upperleg"); Add("calf_" + side, word + "lowerleg");
            Add("ball_" + side, word + "toes", "toe" + side);
            foreach (var finger in new[] { "thumb", "index", "middle", "ring", "pinky" })
                for (int i = 1; i <= 3; i++) Add($"{finger}_{i:00}_{side}", $"{word}hand{finger}{i}", $"{finger}{i}{side}");
        }
        return map;
    }
    public static string[] Prepare(RigDescriptor rig)
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < rig.Names.Length; i++)
        {
            string role = Role(rig.Names[i]);
            rig.Roles[i] = role;
            if (seen.TryGetValue(role, out int other)) problems.Add($"映射冲突：{rig.Names[other]} / {rig.Names[i]} → {role}");
            else { seen[role] = i; rig.Indices[role] = i; }
        }
        foreach (string role in Required)
            if (!rig.Indices.ContainsKey(role)) problems.Add("缺少人体映射：" + role);
        if (problems.Count > 0) return problems.ToArray();
        int pelvis = rig.Indices["pelvis"], root = pelvis;
        while (rig.Parents[root] >= 0) root = rig.Parents[root];
        if (!rig.Indices.ContainsKey("root")) rig.Indices["root"] = root;
        if (rig.Parents.Count(p => p < 0) != 1) problems.Add("人体需要唯一根骨骼");
        bool Ancestor(string a, string b)
        {
            int parent = rig.Parents[rig.Indices[b]];
            while (parent >= 0) { if (parent == rig.Indices[a]) return true; parent = rig.Parents[parent]; }
            return false;
        }
        foreach (var (a, b) in new[] { ("pelvis", "spine_01"), ("spine_01", "head"),
                     ("spine_01", "upperarm_l"), ("spine_01", "upperarm_r"), ("upperarm_l", "lowerarm_l"),
                     ("lowerarm_l", "hand_l"), ("upperarm_r", "lowerarm_r"), ("lowerarm_r", "hand_r"),
                     ("pelvis", "thigh_l"), ("thigh_l", "calf_l"), ("calf_l", "foot_l"),
                     ("pelvis", "thigh_r"), ("thigh_r", "calf_r"), ("calf_r", "foot_r") })
            if (!Ancestor(a, b)) problems.Add($"人体层级不匹配：{a} 应为 {b} 的祖先");
        return problems.ToArray();
    }
    public static void RequireReady(RigDescriptor rig)
    {
        var errors = Prepare(rig);
        if (errors.Length > 0) throw new NotSupportedException("待修复 · Ready-v1\n" + string.Join("\n", errors));
    }
    public static RigDescriptor Describe(Skeleton3D skeleton, Node3D root)
    {
        RigDescriptor rig;
        try { rig = RigCatalog.Identify(skeleton, root); }
        catch (NotSupportedException) { rig = new(skeleton, root); rig.Id = "humanoid:" + rig.Fingerprint; }
        RequireReady(rig); return rig;
    }
}
