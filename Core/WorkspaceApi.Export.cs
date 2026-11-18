using Godot;
using Animanager.Core.Jobs;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;

namespace Animanager.Core;

public sealed partial class WorkspaceApi
{
    public async Task<string> ExportAsync(string idOrName, string folder, JobKind kind, ExportOptions? options,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!kind.IsExport()) throw new ArgumentException("请选择 FBX、GLB 或 RES 导出格式。");
        options = kind.SnapshotExportOptions(options);
        using var activity = BeginActivity();
        var asset = Find(idOrName);
        var source = await Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
        folder = BatchPlanner.ExportFolder(folder);
        // Yield a real frame between in-process exports, preserving main-thread scene operations and job cancellation.
        if (options != null && Engine.GetMainLoop() is SceneTree tree)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        ct.ThrowIfCancellationRequested();

        var extension = kind switch { JobKind.ExportFbx => ".fbx", JobKind.ExportGlb => ".glb", _ => ".res" };
        // Staging and publication are on the same volume, even when the asset library is elsewhere.
        var staging = Path.Combine(folder, ".animanager-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var output = Path.Combine(staging, "result" + extension);
        try
        {
            if (options?.Retarget != null)
            {
                var baked = await Retarget.RetargetBaker.BakeAsync(source, options, progress, ct);
                try
                {
                    var player = UI.Components.Preview3DViewport.Descendants(baked.Root).OfType<AnimationPlayer>().Single();
                    if (kind == JobKind.ExportRes)
                        ExportService.WriteLibrary(output, player.GetAnimationList().Select(c => new KeyValuePair<string, Animation>(c, player.GetAnimation(c))));
                    else
                    {
                        var scene = kind == JobKind.ExportFbx ? Path.Combine(staging, "retargeted.glb") : output;
                        ExportService.WriteScene(scene, baked.Root, baked.SampleFps, baked.HasProp, player.GetAnimationList().Length);
                        if (kind == JobKind.ExportFbx) await Blender.ConvertAsync(scene, output, progress, ct);
                    }
                }
                finally { baked.Root.Free(); }
            }
            else
            if (kind == JobKind.ExportFbx)
            {
                var motion = MotionJson.IsMotion(source);
                bool configured = options != null && (!options.IncludeMesh || options.Clips != null || options.HasTimeOptions);
                if (!configured && motion)
                    source = MotionExport.Glb(source, Path.Combine(staging, "assembled.glb"), true);
                else if (configured)
                {
                    var assembled = Path.Combine(staging, "assembled.glb");
                    ExportService.WriteConfigured(source, assembled, false, options!, ct, forBlender: true);
                    source = assembled;
                }
                await Blender.ConvertAsync(source, output, progress, ct);
            }
            else
            {
                ExportService.WriteConfigured(source, output, kind == JobKind.ExportRes, options!, ct);
            }

            ct.ThrowIfCancellationRequested();
            var name = string.Concat(asset.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
            if (name.Length == 0) name = asset.Id;
            for (int index = 1; ; index++)
            {
                var filename = index == 1 ? name : $"{name}_{index}";
                var destination = Path.Combine(folder, filename + extension);
                try { File.Move(output, destination, false); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
