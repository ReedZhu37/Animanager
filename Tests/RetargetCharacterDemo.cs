using System.Text.Json;
using Animanager.Core;
using Animanager.Core.Console;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI;
using Animanager.UI.Components;
using Godot;

namespace Animanager.Tests;

/// <summary>Explicit target-character trial, baking and checking several real clips before opening a playable preview.</summary>
public static class RetargetCharacterDemo
{
    public sealed record Request(string Target, string Family, Clip[] Clips);
    public sealed record Clip(string Name, string Source);
    public static async Task Run(MainShell owner, Preview3DViewport preview, WorkspaceApi api, ConsoleCommands commands, string requestFile)
    {
        string folder = Path.Combine(api.Storage.Root, "character-retarget"); Directory.CreateDirectory(folder);
        Node3D? combined = null; var rows = new List<object>();
        try
        {
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestFile))!;
            var hashes = request.Clips.Select(c => c.Source).Append(request.Target).Distinct().ToDictionary(p => p, p => MotionJson.Hash(File.ReadAllBytes(p)));
            var original = SceneAsset.Load(request.Target, out _, forExport: true);
            string meshHash = MotionTests.MeshHash(original), rigHash = MotionTests.RigHash(Preview3DViewport.Descendants(original).OfType<Skeleton3D>().Single()); original.Free();
            foreach (var entry in request.Clips)
            {
                var result = await RetargetBaker.BakeToTargetAsync(entry.Source, request.Target, new() {
                    SampleFps = 60, Retarget = new() { TargetRig = request.Family, InPlace = true, IncludeProps = false } });
                owner.AddChild(result.Root);
                try
                {
                    var sk = Preview3DViewport.Descendants(result.Root).OfType<Skeleton3D>().Single();
                    var player = Preview3DViewport.Descendants(result.Root).OfType<AnimationPlayer>().Single();
                    if (MotionTests.MeshHash(result.Root) != meshHash || MotionTests.RigHash(sk) != rigHash)
                        throw new Exception("Target geometry, skin or reference skeleton changed.");
                    var animation = (Animation)player.GetAnimation(player.GetAnimationList().Single()).Duplicate(true);
                    animation.LoopMode = Animation.LoopModeEnum.None;
                    foreach (var lib in player.GetAnimationLibraryList()) player.RemoveAnimationLibrary(lib);
                    RetargetBaker.AddAnimation(player, entry.Name, animation);
                    string glb = Path.Combine(folder, entry.Name + ".glb"), fbx = Path.ChangeExtension(glb, ".fbx");
                    ExportService.WriteScene(glb, result.Root, result.SampleFps, false, 1);
                    await api.Blender.ConvertAsync(glb, fbx);
                    var copy = SceneAsset.Load(fbx, out _, forExport: true, sampleFps: 60); owner.AddChild(copy);
                    float maxP = 0, maxA = 0, movement = 0;
                    try
                    {
                        var check = Preview3DViewport.Descendants(copy).OfType<Skeleton3D>().Single();
                        if (check.GetBoneCount() != sk.GetBoneCount() || result.Target.Names.Any(n => check.FindBone(n) < 0)) throw new Exception("FBX lost target bones.");
                        var cp = Preview3DViewport.Descendants(copy).OfType<AnimationPlayer>().Single();
                        cp.Play(cp.GetAnimationList().First(c => c != "RESET")); cp.Advance(0); cp.Pause();
                        player.Play(entry.Name); player.Advance(0); player.Pause();
                        var first = Enumerable.Range(0, sk.GetBoneCount()).Select(sk.GetBoneGlobalPose).ToArray();
                        for (int sample = 0; sample <= 12; sample++)
                        {
                            double t = Math.Min(animation.Length, Math.Round(animation.Length * sample / 12 * 60) / 60);
                            player.Seek(t, true, true); cp.Seek(t, true, true);
                            for (int i = 0; i < sk.GetBoneCount(); i++)
                            {
                                var a = sk.GlobalTransform * sk.GetBoneGlobalPose(i);
                                var b = check.GlobalTransform * check.GetBoneGlobalPose(check.FindBone(sk.GetBoneName(i)));
                                maxP = Math.Max(maxP, a.Origin.DistanceTo(b.Origin)); maxA = Math.Max(maxA, Angle(a, b));
                                movement = Math.Max(movement, sk.GetBoneGlobalPose(i).Origin.DistanceTo(first[i].Origin));
                                if (result.Diagnostics.Any(d => d.StartsWith(sk.GetBoneName(i) + ":", StringComparison.Ordinal)) && !sk.GetBonePose(i).IsEqualApprox(sk.GetBoneRest(i)))
                                    throw new Exception("Extra character bone did not retain local reference pose: " + sk.GetBoneName(i));
                            }
                        }
                        if (maxP > .001 || maxA > .1 || movement < .001) throw new Exception($"Invalid animated FBX result: {maxP}m / {maxA}deg / motion {movement}m");
                    }
                    finally { copy.GetParent()?.RemoveChild(copy); copy.Free(); }
                    player.Stop(); sk.ResetBonePoses(); animation.LoopMode = Animation.LoopModeEnum.Linear;
                    rows.Add(new { entry.Name, entry.Source, glb, fbx, bones = sk.GetBoneCount(), duration = animation.Length,
                        maxPositionErrorMetres = maxP, maxAngleErrorDegrees = maxA, movementMetres = movement,
                        meshSkinAndRestPreserved = true, extraBoneLocalPosesPreserved = true, diagnostics = result.Diagnostics });
                    if (combined == null) combined = result.Root;
                    else RetargetBaker.AddAnimation(Preview3DViewport.Descendants(combined).OfType<AnimationPlayer>().Single(), entry.Name, (Animation)animation.Duplicate(true));
                    GD.Print($"CHARACTER_RETARGET {entry.Name} bones={sk.GetBoneCount()} roundtrip={maxP}m/{maxA}deg motion={movement}m");
                }
                finally { if (combined != result.Root) { result.Root.GetParent()?.RemoveChild(result.Root); result.Root.Free(); } }
            }
            string merged = Path.Combine(folder, "MechanicGirl_Retarget_4Clips.glb");
            ExportService.WriteScene(merged, combined!, 60, false, request.Clips.Length);
            combined!.GetParent()?.RemoveChild(combined); combined.Free(); combined = null;
            var imported = await api.ImportAsync([merged]);
            if (imported.Errors.Count > 0) throw new Exception(string.Join("; ", imported.Errors));
            var asset = api.Find(Path.GetFileNameWithoutExtension(merged)); commands.Load(asset.Id); await commands.PendingTask;
            owner.Workspace.ShowPanel("preview"); owner.Workspace.ShowPanel("preview");
            owner.DisplayScale.SetScale(1); owner.GetWindow().Size = new(1440, 1000);
            if (DisplayServer.GetName() != "headless")
                foreach (var entry in request.Clips)
                {
                    preview.SelectClip(entry.Name); preview.Seek(preview.Duration * .3); preview.FrameModel();
                    for (int i = 0; i < 4; i++) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                    preview.Redraw(); await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var shot = preview.View.GetTexture().GetImage(); shot.SavePng(Path.Combine(folder, entry.Name + ".png"));
                }
            bool unchanged = hashes.All(p => MotionJson.Hash(File.ReadAllBytes(p.Key)) == p.Value);
            if (!unchanged) throw new Exception("Input file changed.");
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { request.Target, request.Family, merged, asset.Id,
                sourceFilesUnchanged = unchanged, contactIK = false, clips = rows }, new JsonSerializerOptions { WriteIndented = true }));
            preview.SelectClip(request.Clips[0].Name); preview.Seek(0); preview.FrameModel(); preview.TogglePlay();
            GD.Print("CHARACTER_DEMO_READY " + folder);
            if (!OS.GetCmdlineUserArgs().Contains("--keep-open")) owner.GetTree().Quit();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "failure.txt"), e.ToString()); GD.PushError(e.ToString()); owner.GetTree().Quit(1); }
        finally { if (combined != null) { combined.GetParent()?.RemoveChild(combined); combined.Free(); } }
    }
    private static float Angle(Transform3D a, Transform3D b) => Mathf.RadToDeg(2 * Mathf.Acos(Mathf.Clamp(Math.Abs(
        a.Basis.Orthonormalized().GetRotationQuaternion().Dot(b.Basis.Orthonormalized().GetRotationQuaternion())), 0, 1)));
}
