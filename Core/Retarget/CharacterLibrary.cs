using System.Security.Cryptography;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Core.Retarget;

/// <summary>An immutable, hash-checked target reference carried by presets and queued exports.</summary>
public sealed record CharacterReference(string Id, string Name, string ModelPath, string Sha256)
{
    public CharacterReference Snapshot()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name) ||
            !Path.IsPathFullyQualified(ModelPath) || !Path.GetExtension(ModelPath).Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
            Sha256.Length != 64 || Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("角色引用无效，请从角色库重新选择。");
        return this with { ModelPath = Path.GetFullPath(ModelPath) };
    }
    public string VerifiedPath()
    {
        var reference = Snapshot();
        if (!File.Exists(reference.ModelPath) || CharacterLibrary.Hash(reference.ModelPath) != Sha256)
            throw new IOException("角色文件缺失或已变化，请检查角色库：" + Name);
        return reference.ModelPath;
    }
}

public sealed record CharacterEntry(string Id, string Name, string Family, string ModelPath, string Sha256,
    int Bones, int Meshes, string Reason, bool Builtin = false, bool Archived = false)
{
    public int ReadyRulesVersion { get; init; }
    public bool Compatible => Family.Length > 0 && Reason.Length == 0;
    public CharacterReference? Reference => Builtin ? null : new(Id, Name, ModelPath, Sha256);
    public RetargetOptions Options() => Compatible ? new() { TargetRig = Family, Character = Reference }
        : throw new NotSupportedException(Reason);
}

/// <summary>Shared across animation libraries. Imported characters own a standalone, rest-pose GLB.</summary>
public sealed class CharacterLibrary(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private sealed record Selection(string Id);
    private string EntryPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid character ID.");
        return Path.Combine(Root, "entries", id + ".json");
    }
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static string DefaultRoot()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--characters-dir="));
        return arg == null ? ProjectSettings.GlobalizePath("user://Characters") : RuntimePaths.Expand(arg[17..]);
    }
    public CharacterEntry[] List(bool includeArchived = false)
    {
        var builtin = RigCatalog.BodyIds.Select((id, i) => new CharacterEntry(id,
            new[] { "UE4 男体 · Mannequin", "UE5 男体 · Manny", "UE4 女体 · Mannequin", "UE5 女体 · Quinn" }[i], id,
            RigCatalog.ModelPath(id), RigCatalog.Hash(id), RigCatalog.Family(id) == RigCatalog.Ids[0] ? 68 : 161, 1, "", true));
        string entries = Path.Combine(Root, "entries");
        IEnumerable<CharacterEntry> custom = Directory.Exists(entries) ? Directory.GetFiles(entries, "*.json").Select(Read).Where(c => includeArchived || !c.Archived).OrderBy(c => c.Name) : [];
        return builtin.Concat(custom).ToArray();
    }
    private CharacterEntry Read(string path)
    {
        var entry = MotionJson.Read<CharacterEntry>(path);
        _ = EntryPath(entry.Id);
        if (entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new IOException("角色模型哈希无效。");
        // Metadata stores a relative filename, keeping the character library movable as a unit.
        entry = entry with { ModelPath = Path.Combine(Root, "models", entry.Sha256 + ".glb") };
        if (entry.ReadyRulesVersion != 1)
        {
            try
            {
                var model = SceneAsset.Load(entry.Reference!.VerifiedPath(), out _);
                try
                {
                    var report = AssetReadiness.Inspect(model, false);
                    var skeleton = Preview3DViewport.Descendants(model).OfType<Skeleton3D>().FirstOrDefault();
                    var rig = skeleton == null ? null : new RigDescriptor(skeleton, model);
                    if (rig != null) HumanoidMap.Prepare(rig);
                    entry = entry with { Reason = report.Ready ? "" : report.Summary,
                        Family = report.Ready ? (rig!.Indices.ContainsKey("spine_05") ? "ue5-mannequin" : "ue4-mannequin") : "" };
                }
                finally { model.Free(); }
            }
            catch (Exception e) { entry = entry with { Reason = e.Message, Family = "" }; }
            entry = entry with { ReadyRulesVersion = 1 }; Save(entry);
        }
        return entry;
    }
    private void Save(CharacterEntry entry) => MotionJson.Atomic(EntryPath(entry.Id), entry with { ModelPath = "" });
    public CharacterEntry Find(string id) => List().FirstOrDefault(c => c.Id == id) ?? throw new IOException("角色不在库中：" + id);
    public bool HasSelection => File.Exists(Path.Combine(Root, "selection.json"));
    public string SelectedId => HasSelection
        ? MotionJson.Read<Selection>(Path.Combine(Root, "selection.json")).Id : "";
    public void Select(string id)
    {
        if (id.Length > 0 && !Find(id).Compatible) throw new NotSupportedException("此角色尚未配置可用映射。");
        MotionJson.Atomic(Path.Combine(Root, "selection.json"), new Selection(id));
    }
    public CharacterEntry Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new ArgumentException("名称需要 1–100 个字符。");
        var entry = Find(id);
        if (entry.Builtin) throw new InvalidOperationException("内置角色使用固定名称。");
        entry = entry with { Name = name.Trim() }; Save(entry); return entry;
    }
    public void Archive(string id)
    {
        var entry = Find(id);
        if (entry.Builtin) throw new InvalidOperationException("内置角色不能移出。");
        Save(entry with { Archived = true });
        if (SelectedId == id) Select("");
    }
    public Node3D Load(CharacterEntry entry)
    {
        if (entry.Builtin) return RigCatalog.Load(entry.Family, out _);
        return SceneAsset.Load(entry.Reference!.VerifiedPath(), out _);
    }
    public CharacterEntry Import(string source, string? name = null)
    {
        if (!new[] { ".glb", ".gltf", ".fbx" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
            throw new NotSupportedException("请选择 FBX、GLB 或 glTF 角色文件。");
        // Already-normalized character packages must not be re-exported (which can reorder GLB data).
        string inputHash = Hash(source);
        var known = List(true).FirstOrDefault(c => !c.Builtin && c.Sha256 == inputHash);
        if (known != null) { known = known with { Archived = false }; Save(known); return known; }
        var scene = SceneAsset.Load(Path.GetFullPath(source), out _, forExport: true);
        string temporary = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N") + ".glb");
        try
        {
            var nodes = Preview3DViewport.Descendants(scene).ToArray();
            var skeletons = nodes.OfType<Skeleton3D>().ToArray();
            var meshes = nodes.OfType<MeshInstance3D>().Where(m => m.Mesh != null).ToArray();
            if (meshes.Length == 0) throw new NotSupportedException("角色文件中没有网格。");
            foreach (var player in nodes.OfType<AnimationPlayer>()) { player.GetParent().RemoveChild(player); player.Free(); }
            foreach (var skeleton in skeletons) skeleton.ResetBonePoses();
            string family = "", reason = "";
            if (skeletons.Length != 1) reason = "需要一个人体骨架；当前骨架数量：" + skeletons.Length;
            else if (!meshes.Any(m => m.Skin?.GetBindCount() > 0 && m.GetNodeOrNull<Skeleton3D>(m.Skeleton) == skeletons[0]))
                reason = "网格缺少绑定到人体骨架的蒙皮。";
            else
            {
                try
                {
                    var rig = new RigDescriptor(skeletons[0], scene);
                    HumanoidMap.RequireReady(rig);
                    var skinErrors = AssetReadiness.SkinErrors(scene, skeletons[0]);
                    if (skinErrors.Length > 0) throw new NotSupportedException(string.Join("\n", skinErrors));
                    family = rig.Indices.ContainsKey("spine_05") ? "ue5-mannequin" : "ue4-mannequin";
                }
                catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { reason = e.Message; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            ExportService.WriteScene(temporary, scene, null, false, 0);
            string hash = Hash(temporary);
            var duplicate = List(true).FirstOrDefault(c => !c.Builtin && c.Sha256 == hash);
            if (duplicate != null)
            {
                duplicate = duplicate with { Archived = false, Family = family, Reason = reason }; Save(duplicate); return duplicate;
            }
            string model = Path.Combine(Root, "models", hash + ".glb"); Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            if (!File.Exists(model)) File.Move(temporary, model);
            var entry = new CharacterEntry(Guid.NewGuid().ToString("N"), name ?? Path.GetFileNameWithoutExtension(source), family, model, hash,
                skeletons.Sum(s => s.GetBoneCount()), meshes.Length, reason) { ReadyRulesVersion = 1 };
            Save(entry); return entry;
        }
        finally { scene.Free(); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
