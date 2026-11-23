using Godot;
using Animanager.Core.Browse;

namespace Animanager.Core.Pipeline;

internal static class SceneAsset
{
    private static readonly SemaphoreSlim PreviewParser = new(1);
    private static readonly SemaphoreSlim ThumbnailParser = new(1);

    internal static Node3D Load(string path, out float? sourceFps, bool forExport = false, double? sampleFps = null,
        CancellationToken ct = default)
    {
        using var parsed = new ParsedScene(path, ct);
        sourceFps = parsed.SourceFps;
        return parsed.Generate(forExport, sampleFps);
    }

    internal static async Task<ParsedScene> ParsePreviewAsync(string path, CancellationToken ct, bool thumbnail = false)
    {
        var parser = thumbnail ? ThumbnailParser : PreviewParser;
        await parser.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var parsed = new ParsedScene(path, ct);
                if (!ct.IsCancellationRequested) return parsed;
                parsed.Dispose();
                throw new OperationCanceledException(ct);
            }, ct).ConfigureAwait(false);
        }
        finally { parser.Release(); }
    }

    internal static Error AppendFromFile(GltfDocument document, GltfState state, string path,
        GltfDocument.ImportFlags flags, CancellationToken ct)
    {
        if (document is not FbxDocument) return document.AppendFromFile(path, state, (uint)flags);

        string sourcePath = Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        using var source = File.Open(sourcePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read | FileShare.Delete);
        long[] offsets;
        try { offsets = FbxAttributeReader.MissingTextureFilenameOffsets(source, sourcePath, ct); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException) { offsets = []; }
        if (offsets.Length == 0) return document.AppendFromFile(path, state, (uint)flags);

        var directory = Directory.CreateTempSubdirectory("animanager-fbx-");
        string stagedPath = Path.Combine(directory.FullName, Path.GetFileName(sourcePath));
        try
        {
            using (var staged = File.Create(stagedPath))
            {
                source.Position = 0;
                source.CopyTo(staged);
                // Hide missing filename fields from the importer without shifting binary FBX node offsets.
                foreach (long offset in offsets)
                {
                    staged.Position = offset;
                    staged.WriteByte((byte)'_');
                }
            }
            ct.ThrowIfCancellationRequested();
            return document.AppendFromFile(stagedPath, state, (uint)flags, Path.GetDirectoryName(sourcePath)!);
        }
        finally
        {
            File.Delete(stagedPath);
            directory.Delete();
        }
    }

    // Parsing owns a private import state. Scene generation still runs on the Godot thread.
    internal sealed class ParsedScene : IDisposable
    {
        private readonly GltfDocument _document;
        private readonly GltfState _state;
        private bool _generated, _disposed;
        internal float? SourceFps { get; }

        internal ParsedScene(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _document = Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase)
                ? new FbxDocument() : new GltfDocument();
            _state = _document is FbxDocument ? new FbxState() : new GltfState();
            try
            {
                var error = AppendFromFile(_document, _state, path, GltfDocument.ImportFlags.UseNamedSkinBinds, ct);
                if (error != Error.Ok) throw new IOException("模型解析失败：" + error);
                ct.ThrowIfCancellationRequested();
                SourceFps = SourceFrameRate(_state);
                if (_document is FbxDocument)
                {
                    // FBX metadata may be absent even when scene import succeeds.
                    try
                    {
                        if (FbxAttributeReader.Read(path, ct).SourceFps is double frameRate && frameRate is >= 1 and <= 600)
                            SourceFps = (float)frameRate;
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OverflowException) { }
                }
            }
            catch { Dispose(); throw; }
        }

        internal Node3D Generate(bool fullTracks, double? sampleFps = null)
        {
            float bakeFps = Math.Max(SourceFps ?? 30, (float)(sampleFps ?? 0));
            var scene = _document.GenerateScene(_state, bakeFps, false, !fullTracks);
            _generated = scene != null;
            if (scene is Node3D root) return root;
            scene?.Free();
            throw new IOException("文件未生成有效的 3D 场景。");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_generated) return;
                // FBX AppendFromFile creates detached import nodes before GenerateScene hands them to the caller.
                var roots = new HashSet<Node>();
                int nodeCount = _state.GetNodes().Count;
                for (int i = 0; i < nodeCount; i++)
                    CollectRoot(_state.GetSceneNode(i));
                foreach (var skeleton in _state.GetSkeletons())
                    CollectRoot(skeleton.GetGodotSkeleton());
                foreach (var root in roots) root.Free();

                void CollectRoot(Node? node)
                {
                    if (node == null) return;
                    while (node.GetParent() is { } parent) node = parent;
                    roots.Add(node);
                }
            }
            finally { _state.Dispose(); _document.Dispose(); }
        }
    }

    internal static float? SourceFrameRate(GltfState state)
    {
        if (state.Json.TryGetValue("extras", out var extras) && extras.VariantType == Variant.Type.Dictionary &&
            extras.AsGodotDictionary().TryGetValue("animanagerSourceFps", out var rate) &&
            rate.VariantType is Variant.Type.Float or Variant.Type.Int)
        {
            var value = (float)rate.AsDouble();
            if (value is >= 1 and <= 600) return value;
        }
        return null;
    }
}
