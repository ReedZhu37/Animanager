using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

public static class CharacterLibraryTests
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands)
    {
        string root = api.Storage.Root, folder = Path.Combine(root, "character-validation"); Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); GD.Print("PASS " + message); }
        try
        {
            var store = owner.Characters;
            Check(store.List().Select(c => c.Id).SequenceEqual(RigCatalog.BodyIds), "built-in UE4 and UE5 available without importing animation assets");
            string target = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--character-file="))?[17..]
                ?? RuntimePaths.Bundled(".runtime/mechanicgirl-probe/input/FBX/SK_MechanicGirl_01.fbx");
            string inputHash = CharacterLibrary.Hash(target);
            var character = store.Import(target, "MechanicGirl");
            Check(character.Compatible && character.Family == "ue5-mannequin" && character.Bones == 115 && character.Meshes == 17,
                "MechanicGirl imports as compatible custom UE5 target with 115 bones and 17 meshes");
            var saved = store.Load(character);
            try
            {
                Check(!Preview3DViewport.Descendants(saved).OfType<AnimationPlayer>().Any(), "character package contains rest model without source animation clips");
                Check(Preview3DViewport.Descendants(saved).OfType<Skeleton3D>().Single().GetBoneCount() == 115, "standalone GLB reload retains full custom skeleton");
                var original = SceneAsset.Load(target, out _, forExport: true);
                try { Check(EquivalentSkin(original, saved), "normalized character preserves vertices, weights, topology and bind poses within format precision"); }
                finally { original.Free(); }
            }
            finally { saved.Free(); }
            Check(store.Import(target).Id == character.Id && store.List().Length == 5, "reimport deduplicates character");
            store.Rename(character.Id, "MechanicGirl Test");
            Check(new CharacterLibrary(store.Root).Find(character.Id).Name == "MechanicGirl Test", "character name persists across store reload");
            store.Rename(character.Id, "MechanicGirl"); store.Select(character.Id); store.Archive(character.Id);
            Check(store.List().Length == 4 && File.Exists(character.ModelPath) && store.SelectedId == "", "archive preserves files for queued exports and resets removed selection");
            character = store.Import(target);
            Check(character.Id.Length > 0 && store.List().Length == 5, "reimport restores archived character");
            var cube = new Node3D(); cube.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh() });
            string cubeFile = Path.Combine(folder, "unrigged.glb");
            try { ExportService.WriteScene(cubeFile, cube, null, false, 0); } finally { cube.Free(); }
            var unsupported = store.Import(cubeFile);
            bool refused = false; try { store.Select(unsupported.Id); } catch (NotSupportedException) { refused = true; }
            Check(!unsupported.Compatible && refused && unsupported.Reason.Contains("骨架"), "unrigged mesh is viewable but cannot be selected for retarget");
            foreach (var id in RigCatalog.Ids) await api.ImportAsync([RetargetTests.Fixture(Path.Combine(folder, id + ".glb"), id)]);
            int count = api.Search().Total;
            commands.Load("ue4-mannequin"); await commands.PendingTask;
            foreach (var entry in store.List().Where(c => c.Compatible))
            {
                await owner.SelectPreviewCharacterAsync(entry.Id);
                Check(preview.AppliedRetarget != null && preview.BoneCount == entry.Bones && owner.PreviewCharacterId == entry.Id,
                    "UI character selection bakes target: " + entry.Name);
                var skeleton = Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
                preview.Seek(.1); var first = skeleton.GetBoneGlobalPose(skeleton.FindBone("head")); preview.Seek(.8);
                Check(!first.IsEqualApprox(skeleton.GetBoneGlobalPose(skeleton.FindBone("head"))), "target actually animates: " + entry.Name);
            }
            var picker = (OptionButton)owner.Workspace.Panel("preview").FindChild("PreviewCharacter", true, false)!;
            Check(picker.ItemCount == 7 && picker.GetPopup().IsItemDisabled(6), "preview picker includes all roles and disables incompatible mesh");
            await owner.SelectPreviewCharacterAsync(character.Id);
            int hits = owner.CharacterCacheHits;
            await owner.SelectPreviewCharacterAsync("ue4-mannequin"); await owner.SelectPreviewCharacterAsync(character.Id);
            Check(owner.CharacterCacheHits > hits, "switching back reuses cached bake with separate viewport nodes");
            var applied = preview.AppliedRetarget!;
            Check(applied.Character?.Id == character.Id && applied.Character.Sha256 == character.Sha256, "active configuration contains immutable custom character identity");
            string fbx = await api.ExportAsync("ue4-mannequin", Path.Combine(folder, "fbx"), JobKind.ExportFbx,
                new() { Retarget = applied, SampleFps = 60 });
            var fbxRoot = SceneAsset.Load(fbx, out _, forExport: true, sampleFps: 60);
            try { Check(Preview3DViewport.Descendants(fbxRoot).OfType<Skeleton3D>().Single().GetBoneCount() == 115,
                "normal FBX export uses selected custom character"); } finally { fbxRoot.Free(); }
            var job = api.Planner.ExportSingle(api.Find("ue4-mannequin").Id, Path.Combine(folder, "queue"), JobKind.ExportGlb, new() { Retarget = applied });
            string jobId = api.Jobs.Enqueue(job);
            await owner.SelectPreviewCharacterAsync("ue5-mannequin");
            var outcome = await api.Jobs.WaitAsync(jobId);
            Check(outcome.Succeeded == 1 && outcome.ExportOptions?.Retarget?.Character?.Id == character.Id,
                "queued export keeps chosen character while preview selection changes");
            var slow = owner.SelectPreviewCharacterAsync(character.Id);
            var newer = owner.SelectPreviewCharacterAsync("ue4-mannequin");
            await Task.WhenAll(slow, newer);
            Check(owner.PreviewCharacterId == "ue4-mannequin" && preview.BoneCount == 68, "latest character wins during overlapping requests");
            await owner.SelectPreviewCharacterAsync(character.Id);
            commands.Load("ue5-mannequin"); await commands.PendingTask; await owner.PendingRetargetPreview;
            Check(owner.PreviewCharacterId == character.Id && preview.AppliedRetarget?.SourceRig == "ue5-mannequin" && preview.BoneCount == 115,
                "changing source animation automatically retains character and recognizes new source family");
            Check(new CharacterLibrary(store.Root).SelectedId == character.Id, "preview character selection survives app settings reload");
            await owner.SelectPreviewCharacterAsync("");
            Check(preview.AppliedRetarget == null && preview.BoneCount == 161, "original character restores source and clears retarget");
            string corrupt = Path.Combine(folder, "changed.glb"); File.Copy(character.ModelPath, corrupt); File.AppendAllText(corrupt, "changed");
            bool changedRejected = false;
            try { var result = await RetargetBaker.BakeAsync(api.Storage.Resolve(api.Find("ue4-mannequin").VirtualPath), new() { Retarget = applied with { Character = applied.Character! with { ModelPath = corrupt } } }); result.Root.Free(); }
            catch (IOException) { changedRejected = true; }
            Check(changedRejected, "modified character content rejected before bake");
            Check(api.Search().Total == count && CharacterLibrary.Hash(target) == inputHash, "role switching adds no animation assets and leaves source FBX unchanged");
            await owner.SelectPreviewCharacterAsync(character.Id);
            var next = owner.Libraries.CreateAndOpen(Path.Combine(folder, "second-library"), "Character switch test"); owner.ActivateLibrary(next);
            await next.Api.ImportAsync([Path.Combine(folder, "ue4-mannequin.glb")]);
            commands.Load("ue4-mannequin"); await commands.PendingTask; await owner.PendingRetargetPreview;
            Check(owner.PreviewCharacterId == character.Id && preview.BoneCount == 115, "character library and selected role persist when animation library changes");
            File.WriteAllLines(Path.Combine(folder, "results.txt"), checks.Select(s => "PASS " + s));
            GD.Print("CHARACTER_LIBRARY_TESTS_OK " + checks.Count); owner.GetTree().Quit();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "results.txt"), string.Join("\n", checks) + "\nFAIL " + e); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
    }
    private static bool EquivalentSkin(Node3D original, Node3D copy)
    {
        var a = Preview3DViewport.Descendants(original).OfType<MeshInstance3D>().ToArray();
        var b = Preview3DViewport.Descendants(copy).OfType<MeshInstance3D>().ToArray();
        float position = 0, weight = 0, bind = 0; bool topology = a.Length == b.Length;
        foreach (var mesh in a)
        {
            var other = b.Single(m => m.Name == mesh.Name);
            topology &= mesh.Mesh.GetSurfaceCount() == other.Mesh.GetSurfaceCount();
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var src = mesh.Mesh.SurfaceGetArrays(surface); var dst = other.Mesh.SurfaceGetArrays(surface);
                var vertices = src[(int)Mesh.ArrayType.Vertex].AsVector3Array(); var targetVertices = dst[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                string[] Triangles(int[] indices) => indices.Chunk(3).Select(t =>
                {
                    int start = Array.IndexOf(t, t.Min()); return string.Join(",", Enumerable.Range(0, t.Length).Select(j => t[(start + j) % t.Length]));
                }).Order(StringComparer.Ordinal).ToArray();
                bool sameTopology = vertices.Length == targetVertices.Length && Triangles(src[(int)Mesh.ArrayType.Index].AsInt32Array()).SequenceEqual(Triangles(dst[(int)Mesh.ArrayType.Index].AsInt32Array()));
                if (!sameTopology) GD.Print($"CHARACTER_TOPOLOGY {mesh.Name}: vertices {vertices.Length}/{targetVertices.Length}, indices {src[(int)Mesh.ArrayType.Index].AsInt32Array().Length}/{dst[(int)Mesh.ArrayType.Index].AsInt32Array().Length}");
                topology &= sameTopology;
                foreach (var pair in vertices.Zip(targetVertices)) position = Math.Max(position, pair.First.DistanceTo(pair.Second));
                var weights = src[(int)Mesh.ArrayType.Weights].AsFloat32Array(); var otherWeights = dst[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                var bones = src[(int)Mesh.ArrayType.Bones].AsInt32Array(); var otherBones = dst[(int)Mesh.ArrayType.Bones].AsInt32Array();
                for (int i = 0; i < Math.Min(vertices.Length, targetVertices.Length); i++)
                {
                    Dictionary<string, float> Influences(Skin skin, int[] joints, float[] values, int stride)
                    {
                        var result = new Dictionary<string, float>();
                        for (int slot = 0; slot < stride; slot++)
                        {
                            int index = i * stride + slot; string name = skin.GetBindName(joints[index]);
                            result[name] = result.GetValueOrDefault(name) + values[index];
                        }
                        return result;
                    }
                    var left = Influences(mesh.Skin!, bones, weights, weights.Length / vertices.Length);
                    var right = Influences(other.Skin!, otherBones, otherWeights, otherWeights.Length / targetVertices.Length);
                    foreach (var name in left.Keys.Union(right.Keys)) weight = Math.Max(weight, Math.Abs(left.GetValueOrDefault(name) - right.GetValueOrDefault(name)));
                }
            }
            if (mesh.Skin != null)
                for (int i = 0; i < mesh.Skin.GetBindCount(); i++)
                {
                    int j = Enumerable.Range(0, other.Skin!.GetBindCount()).Single(k => other.Skin.GetBindName(k) == mesh.Skin.GetBindName(i));
                    var x = mesh.Skin.GetBindPose(i); var y = other.Skin.GetBindPose(j);
                    bind = Math.Max(bind, x.Origin.DistanceTo(y.Origin));
                    for (int axis = 0; axis < 3; axis++) bind = Math.Max(bind, x.Basis[axis].DistanceTo(y.Basis[axis]));
                }
        }
        GD.Print($"CHARACTER_SKIN_ERRORS position={position} weight={weight} bind={bind} topology={topology}");
        return topology && position < .00001f && weight < .0001f && bind < .00001f;
    }
}
