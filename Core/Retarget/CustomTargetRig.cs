using Godot;

namespace Animanager.Core.Retarget;

/// <summary>Validate the body hierarchy of an explicitly selected character, retaining its own proportions and extra bones.</summary>
internal static class CustomTargetRig
{
    internal static void Validate(RigDescriptor target, string family)
    {
        var reference = RigCatalog.Load(family, out var sk);
        try
        {
            var standard = new RigDescriptor(sk, reference);
            var required = new List<string> { "root", "pelvis", "head" };
            foreach (string side in new[] { "l", "r" })
                foreach (string part in new[] { "clavicle", "upperarm", "lowerarm", "hand", "thigh", "calf", "foot", "ball" })
                    required.Add(part + "_" + side);
            required.AddRange(standard.Names.Where(n => (n.StartsWith("spine_") && n[6..].All(char.IsDigit)) ||
                (n.StartsWith("neck_") && n[5..].All(char.IsDigit))));
            var missing = required.Where(n => !target.Indices.ContainsKey(n)).ToArray();
            if (missing.Length > 0) throw new NotSupportedException("目标缺少人体骨骼：" + string.Join(", ", missing));
            string Parent(RigDescriptor r, int i) => r.Parents[i] < 0 ? "" : r.Names[r.Parents[i]];
            foreach (int i in target.Order)
                if (standard.Indices.TryGetValue(target.Names[i], out int j) && Parent(target, i) != Parent(standard, j))
                    throw new NotSupportedException("目标骨架层级与所选映射不匹配：" + target.Names[i]);
            if (target.Parents.Count(p => p < 0) != 1 || target.Parents[target.Indices["root"]] >= 0)
                throw new NotSupportedException("目标需要唯一的 root 根骨骼。");
            // Proper, finite, uniform transforms have already been checked by RigDescriptor.
            // No positional equality check: differing body proportions are the purpose of this explicit target path.
        }
        finally { reference.Free(); }
    }
}
