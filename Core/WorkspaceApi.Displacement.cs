using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;

namespace Animanager.Core;

public sealed record AnimationOrganizationResult(Dictionary<string, int> Tagged, int MovedToTrash, List<string> Errors);

public sealed partial class WorkspaceApi
{
    internal async Task<(AssetItem[] Assets, string[] Errors)> FindAssetsWithoutAnimationAsync(CancellationToken ct = default)
    {
        using var maintenance = BeginMaintenance();
        return await Task.Run(async () =>
        {
            var assets = new List<AssetItem>();
            var errors = new List<string>();
            foreach (var asset in Database.Snapshot().Assets.Where(asset => !asset.Trashed))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var path = await Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
                    bool? hasAnimation = MotionJson.IsMotion(path)
                        ? AnimationDisplacement.InspectMotion(path, MotionJson.ReadManifest(path), ct) != null
                        : Path.GetExtension(path).ToLowerInvariant() is ".fbx" or ".glb" or ".gltf"
                            ? AnimationDisplacement.InspectScene(path, ct, measureDisplacement: false).HasAnimation
                            : null;
                    if (hasAnimation == false) assets.Add(asset);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    errors.Add(asset.Name + " · " + error.Message);
                }
            }
            return (assets.ToArray(), errors.ToArray());
        }, ct);
    }

    public async Task<AnimationOrganizationResult> OrganizeAnimationsAsync(CancellationToken ct = default)
    {
        using var maintenance = BeginMaintenance();
        return await Task.Run(async () =>
        {
            var updates = new Dictionary<string, AnimationDisplacement.Inspection>();
            var counts = new Dictionary<string, int>();
            var errors = new List<string>();
            int trashed = 0;
            foreach (var asset in Database.Snapshot().Assets.Where(asset => !asset.Trashed))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var motion = await Inspect(asset, ct);
                    if (motion.Warning is { } warning) throw new InvalidDataException(warning);
                    if (motion.InPlace) trashed++;
                    else if (motion.Tag is { } tag) counts[tag] = counts.GetValueOrDefault(tag) + 1;
                    else if (asset.Type != "Animation") continue;
                    updates.Add(asset.Id, motion);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    errors.Add(asset.Name + " · " + error.Message);
                }
            }

            ct.ThrowIfCancellationRequested();
            if (updates.Count > 0)
                Database.Edit(updates.Keys.ToArray(), asset =>
                {
                    var motion = updates[asset.Id];
                    if (motion.InPlace) return asset with { Trashed = true };
                    return motion.Tag is { } tag ? asset with { Type = "Animation", Tags = AnimationDisplacement.WithTag(asset.Tags, tag) }
                        : asset with { Type = "Model" };
                });
            return new AnimationOrganizationResult(counts, trashed, errors);
        }, ct);
    }

    private async Task<AnimationDisplacement.Inspection> Inspect(AssetItem asset, CancellationToken ct)
    {
        bool namedInPlace = AnimationDisplacement.IsInPlace(asset.Name)
            || AnimationDisplacement.IsInPlace(asset.VirtualPath)
            || ParseTags(asset.Tags).Any(AnimationDisplacement.IsInPlace);
        if (namedInPlace && asset.Type == "Animation") return new(null, true);

        var path = await Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
        if (MotionJson.IsMotion(path))
        {
            var manifest = MotionJson.ReadManifest(path);
            return namedInPlace || AnimationDisplacement.IsInPlace(manifest) ? new(null, true)
                : new(AnimationDisplacement.InspectMotion(path, manifest, ct), false);
        }

        return AnimationDisplacement.InspectScene(path, ct, namedInPlace);
    }
}
