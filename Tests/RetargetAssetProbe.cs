using System.Text.Json;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Jobs;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

/// <summary>Opt-in inspection of external FBX fixtures, using a separate scratch library.</summary>
public static class RetargetAssetProbe
{
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands, string input)
    {
        string output = Path.Combine(api.Storage.Root, "retarget-probe"); Directory.CreateDirectory(output);
        var rows = new List<object>();
        var files = Directory.Exists(input) ? Directory.GetFiles(input, "*.fbx", SearchOption.AllDirectories).Order().ToArray() : [input];
        var hashes = files.ToDictionary(p => p, p => MotionJson.Hash(File.ReadAllBytes(p)));
        try
        {
            foreach (string file in files)
            {
                try
                {
                    var root = SceneAsset.Load(file, out var fps, forExport: true); owner.AddChild(root);
                    try
                    {
                        var skeletons = Preview3DViewport.Descendants(root).OfType<Skeleton3D>().ToArray();
                        var meshes = Preview3DViewport.Descendants(root).OfType<MeshInstance3D>().ToArray();
                        var player = Preview3DViewport.Descendants(root).OfType<AnimationPlayer>().FirstOrDefault();
                        var clips = player?.GetAnimationList().Where(c => c != "RESET").Select(c =>
                        {
                            var animation = player.GetAnimation(c);
                            return new { name = c, duration = animation.Length, tracks = animation.GetTrackCount(),
                                varyingTransformTracks = Enumerable.Range(0, animation.GetTrackCount()).Count(t => Varies(animation, t)) };
                        }).ToArray();
                        string recognized = "", reason = ""; object[] comparisons = [];
                        if (skeletons.Length == 1)
                        {
                            try { recognized = RigCatalog.Identify(skeletons[0], root).Id; }
                            catch (Exception e) { reason = e.Message; }
                            var actual = new RigDescriptor(skeletons[0], root);
                            comparisons = RigCatalog.Ids.Select(id => Compare(actual, id)).ToArray();
                        }
                        var row = new { file, fps, meshes = meshes.Length, skeletons = skeletons.Select(sk => new {
                            count = sk.GetBoneCount(), bones = Enumerable.Range(0, sk.GetBoneCount()).Select(i => new { name = sk.GetBoneName(i).ToString(), parent = sk.GetBoneParent(i) }).ToArray()
                        }).ToArray(), clips, recognized, reason, comparisons };
                        rows.Add(row); GD.Print($"PROBE {Path.GetFileName(file)} meshes={meshes.Length} bones={skeletons.Sum(s => s.GetBoneCount())} clips={clips?.Length ?? 0} rig={recognized} {reason}");
                    }
                    finally { root.GetParent()?.RemoveChild(root); root.Free(); }
                }
                catch (Exception e) { rows.Add(new { file, error = e.Message }); GD.Print("PROBE_ERROR " + file + ": " + e.Message); }
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            string representative = files.FirstOrDefault(p => Path.GetFileName(p).Equals("SK_MechanicGirl_01.fbx", StringComparison.OrdinalIgnoreCase)) ?? files[0];
            var imported = await api.ImportAsync([representative]);
            if (imported.Errors.Count > 0) throw new IOException(string.Join("; ", imported.Errors));
            var asset = api.Find(Path.GetFileNameWithoutExtension(representative)); commands.Load(asset.Id); await commands.PendingTask;
            string bakeOutcome;
            try
            {
                var result = await api.PreviewRetargetAsync(asset.Id, new() { TargetRig = "ue5-mannequin" });
                bakeOutcome = "Succeeded"; result.Root.Free();
            }
            catch (Exception e) { bakeOutcome = e.Message; }
            var baseline = Preview3DViewport.Descendants(preview.Model!).OfType<Skeleton3D>().Single();
            var expected = new RigDescriptor(baseline, preview.Model!);
            var exports = new List<object>();
            foreach (var kind in new[] { JobKind.ExportGlb, JobKind.ExportFbx })
            {
                try
                {
                    string path = await api.ExportAsync(asset.Id, output, kind, new() { IncludeMesh = true });
                    var copy = SceneAsset.Load(path, out _, forExport: true);
                    try
                    {
                        var sk = Preview3DViewport.Descendants(copy).OfType<Skeleton3D>().Single(); var actual = new RigDescriptor(sk, copy);
                        var common = expected.Names.Where(actual.Indices.ContainsKey).ToArray();
                        exports.Add(new { kind = kind.ToString(), path, bones = sk.GetBoneCount(), meshes = Preview3DViewport.Descendants(copy).OfType<MeshInstance3D>().Count(),
                            missingBones = expected.Names.Except(actual.Names).ToArray(), maxRestPositionErrorMetres = common.Max(n => expected.GlobalRest[expected.Indices[n]].Origin.DistanceTo(actual.GlobalRest[actual.Indices[n]].Origin)) });
                    }
                    finally { copy.Free(); }
                }
                catch (Exception e) { exports.Add(new { kind = kind.ToString(), error = e.Message }); }
            }
            var panel = owner.Workspace.Panel("retarget");
            ((Button)panel.FindChild("RetargetApply", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingRetargetPreview;
            if (DisplayServer.GetName() != "headless")
            {
                owner.DisplayScale.SetScale(1); owner.GetWindow().Size = new(1440, 1000);
                owner.Workspace.ShowPanel("preview"); owner.Workspace.ShowPanel("retarget"); owner.Workspace.ShowPanel("preview");
                for (int i = 0; i < 15; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                preview.Redraw(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = owner.GetViewport().GetTexture().GetImage(); image.SavePng(Path.Combine(output, "preview.png"));
            }
            bool unchanged = hashes.All(p => MotionJson.Hash(File.ReadAllBytes(p.Key)) == p.Value);
            var report = new { input, representative, sourceFilesUnchanged = unchanged, configuredTargets = RigCatalog.Ids, customTargetSelectionSupported = false,
                currentRetargetOutcome = bakeOutcome, files = rows, exports };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print("PROBE_REPORT " + Path.Combine(output, "report.json")); owner.GetTree().Quit(unchanged ? 0 : 1);
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(output, "failure.txt"), e.ToString()); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
    }
    private static bool Varies(Animation a, int track)
    {
        if (a.TrackGetKeyCount(track) < 2) return false;
        var first = a.TrackGetKeyValue(track, 0);
        return Enumerable.Range(1, a.TrackGetKeyCount(track) - 1).Any(k => a.TrackGetType(track) switch {
            Animation.TrackType.Position3D or Animation.TrackType.Scale3D => first.AsVector3().DistanceTo(a.TrackGetKeyValue(track, k).AsVector3()) > .0001f,
            Animation.TrackType.Rotation3D => Math.Abs(first.AsQuaternion().Normalized().Dot(a.TrackGetKeyValue(track, k).AsQuaternion().Normalized())) < .999999f,
            _ => false });
    }
    private static object Compare(RigDescriptor actual, string id)
    {
        var root = RigCatalog.Load(id, out var sk);
        try
        {
            var expected = new RigDescriptor(sk, root); var common = actual.Names.Where(expected.Indices.ContainsKey).ToArray();
            string Parent(RigDescriptor rig, string bone) { int i = rig.Parents[rig.Indices[bone]]; return i < 0 ? "" : rig.Names[i]; }
            var offsets = common.Select(n => new { bone = n, metres = actual.GlobalRest[actual.Indices[n]].Origin.DistanceTo(expected.GlobalRest[expected.Indices[n]].Origin) }).OrderByDescending(p => p.metres).ToArray();
            return new { id, common = common.Length, extraBones = actual.Names.Except(expected.Names).ToArray(), missingBones = expected.Names.Except(actual.Names).ToArray(),
                parentDifferences = common.Where(n => Parent(actual, n) != Parent(expected, n)).Select(n => new { bone = n, actual = Parent(actual, n), expected = Parent(expected, n) }).ToArray(),
                maxRestPositionErrorMetres = offsets.FirstOrDefault()?.metres ?? 0, largestReferenceDifferences = offsets.Take(12).ToArray(),
                keyJointPositions = new[] { "root", "pelvis", "head", "hand_l", "hand_r", "foot_l", "foot_r" }.Where(actual.Indices.ContainsKey).Select(n => new {
                    bone = n, actual = V(actual.GlobalRest[actual.Indices[n]].Origin), expected = V(expected.GlobalRest[expected.Indices[n]].Origin)
                }).ToArray() };
        }
        finally { root.Free(); }
        static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
    }
}
