using Godot;
using Animanager.Core.Browse;

namespace Animanager.Core.Pipeline;

internal static class SceneAsset
{
    internal static Node3D Load(string path, out float? sourceFps, bool forExport = false, double? sampleFps = null,
        CancellationToken ct = default)
    {
        using GltfDocument document = Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase)
            ? new FbxDocument() : new GltfDocument();
        using GltfState state = document is FbxDocument ? new FbxState() : new GltfState();
        var error = document.AppendFromFile(path, state, (uint)GltfDocument.ImportFlags.UseNamedSkinBinds);
        if (error != Error.Ok) throw new IOException("模型解析失败：" + error);

        sourceFps = null;
        if (state.Json.TryGetValue("extras", out var extras) && extras.VariantType == Variant.Type.Dictionary &&
            extras.AsGodotDictionary().TryGetValue("animanagerSourceFps", out var rate) &&
            rate.VariantType is Variant.Type.Float or Variant.Type.Int)
        {
            var value = (float)rate.AsDouble();
            if (float.IsFinite(value) && value is >= 1 and <= 600) sourceFps = value;
        }

        if (document is FbxDocument)
        {
            // FBX metadata may be absent (including in ASCII input), while scene import can still succeed.
            try
            {
                if (FbxAttributeReader.Read(path, ct).SourceFps is double frameRate && frameRate is >= 1 and <= 600)
                    sourceFps = (float)frameRate;
            }
            catch (Exception metadataError) when (metadataError is IOException or InvalidDataException or ArgumentException or OverflowException) { }
        }

        float bakeFps = Math.Max(sourceFps ?? 30, (float)(sampleFps ?? 0));
        var scene = document.GenerateScene(state, bakeFps, false, !forExport);
        if (scene is Node3D root) return root;
        scene?.Free();
        throw new IOException("文件未生成有效的 3D 场景。");
    }
}
