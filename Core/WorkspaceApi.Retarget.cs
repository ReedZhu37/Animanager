using Animanager.Core.Pipeline;
using Animanager.Core.Retarget;
using Animanager.UI.Components;

namespace Animanager.Core;

public sealed partial class WorkspaceApi
{
    public RetargetPresetStore RetargetPresets => new(Storage.Root);
    public async Task<RetargetResult> PreviewRetargetAsync(string assetId, RetargetOptions options, CancellationToken ct = default)
    {
        using var activity = BeginActivity();
        var asset = Find(assetId);
        var path = await Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
        return await RetargetBaker.BakeAsync(path, new ExportOptions { Retarget = options, IncludeMesh = true }, null, ct);
    }
    public RetargetOptions InspectRetarget(string assetId, RetargetOptions options)
    {
        options = options.Snapshot();
        var asset = Find(assetId);
        var path = Storage.Resolve(asset.VirtualPath);
        RetargetBaker.RejectAdditive(path);
        var root = Motion.MotionJson.IsMotion(path) ? new Motion.MotionPlayback(path).Root : SceneAsset.Load(path, out _);
        try
        {
            var readiness = AssetReadiness.Inspect(root, true);
            if (!readiness.Ready) throw new NotSupportedException(readiness.Summary);
            var skeletons = Preview3DViewport.Descendants(root).OfType<Godot.Skeleton3D>().ToArray();
            if (skeletons.Length != 1) throw new NotSupportedException("Retarget 需要一个人体 Skeleton。");
            var rig = HumanoidMap.Describe(skeletons[0], root);
            if (options.SourceRig.Length > 0 && options.SourceRig != rig.Id) throw new InvalidOperationException("保存的预设属于另一种源骨架，请选择匹配的预设。");
            return (options with { SourceRig = rig.Id, SourceHash = RigCatalog.Ids.Contains(rig.Id) ? RigCatalog.Hash(rig.Id) : rig.Fingerprint }).Snapshot();
        }
        finally { root.Free(); }
    }
}
