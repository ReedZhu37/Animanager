using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;

namespace Animanager.Core.Browse;

internal sealed record AssetThumbnailRequest(string SourcePath, string CachePath);

internal sealed class AssetBrowseCache(LocalStorageProvider storage, Action<Action> writeWhenAvailable)
{
    public const string DirectoryName = "preview-cache";
    private const string VersionDirectory = DirectoryName + "/v1/";
    private const string AttributesDirectory = VersionDirectory + "attributes/";
    private const string ThumbnailsDirectory = DirectoryName + "/v2/thumbnails/";
    private const string MotionDirectory = VersionDirectory + "motion-samples/";
    internal const int ThumbnailWidth = 240, ThumbnailHeight = 168;
    private const int MaxCacheFileSize = 1024 * 1024;
    private sealed record CachedAttributes(string SourceStamp, AssetAttributes Attributes);
    private ConcurrentDictionary<string, CachedAttributes> _attributes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Path, string Stamp), AssetSkeleton[]> _rigs = new();

    public static bool IsCacheFile(string path)
    {
        path = path.Replace('\\', '/');
        return OwnedFile(AttributesDirectory, ".json") || OwnedFile(ThumbnailsDirectory, ".png") ||
            OwnedFile(MotionDirectory, ".bin") || OwnedFile(VersionDirectory + "thumbnails/", ".png");

        bool OwnedFile(string folder, string extension)
        {
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                path.Length != folder.Length + 64 + extension.Length) return false;
            return path.AsSpan(folder.Length, 64).IndexOfAnyExcept("0123456789abcdef") < 0;
        }
    }

    internal void Invalidate()
    {
        Interlocked.Exchange(ref _attributes, new(StringComparer.Ordinal));
        _rigs.Clear();
    }

    internal AssetAttributes ReadAttributes(AssetItem asset, CancellationToken cancellationToken)
    {
        try { return ReadCheckedAttributes(asset, cancellationToken); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return AssetAttributes.Unknown;
        }
    }

    internal AssetAttributes? ReadCachedAttributes(AssetItem asset)
    {
        try
        {
            var bytes = ReadCache(AttributesDirectory + Hash(asset.FileHash + "\n" + asset.SourceFormat) + ".json");
            var cached = bytes == null ? null : JsonSerializer.Deserialize<CachedAttributes>(bytes, MotionJson.Options);
            return cached != null && !string.IsNullOrEmpty(cached.SourceStamp) && Valid(cached, cached.SourceStamp) ? cached.Attributes : null;
        }
        catch (JsonException) { return null; }
    }

    internal AssetAttributes ReadCheckedAttributes(AssetItem asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = storage.Resolve(asset.VirtualPath);
        var stamp = Stamp(path);
        MotionManifest? motion = null;
        (string Path, string Stamp)? rig = null;
        if (MotionJson.IsMotion(path))
        {
            motion = MotionJson.ReadManifest(path);
            try
            {
                var rigPath = MotionJson.ResolveObject(path, motion.Objects["rig"], verify: false);
                rig = (rigPath, Stamp(rigPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            stamp += "\n" + (rig?.Stamp ?? "missing-rig");
        }
        var key = asset.FileHash + "\n" + asset.SourceFormat;
        var memory = _attributes;
        if (memory.TryGetValue(key, out var info) && Valid(info, stamp)) return info.Attributes;
        var relative = AttributesDirectory + Hash(key) + ".json";
        try
        {
            var bytes = ReadCache(relative);
            var fromDisk = bytes == null ? null : JsonSerializer.Deserialize<CachedAttributes>(bytes, MotionJson.Options);
            if (Valid(fromDisk, stamp))
            {
                memory[key] = fromDisk!;
                return fromDisk!.Attributes;
            }
        }
        catch (JsonException) { }

        var attributes = motion == null ? AssetAttributeReader.Read(path, asset.SourceFormat, cancellationToken) :
            new AssetAttributes(rig is { } rigSource ? _rigs.GetOrAdd(rigSource, source => ReadRig(source.Path)) : [],
                motion.Duration, motion.SourceFps);
        cancellationToken.ThrowIfCancellationRequested();
        var cached = new CachedAttributes(stamp, attributes);
        memory[key] = cached;
        WriteCache(relative, MotionJson.Bytes(cached));
        return attributes;
    }

    private (string Source, string Hash) SourceIdentity(AssetItem asset)
    {
        var source = storage.Resolve(asset.VirtualPath);
        var fingerprint = new StringBuilder(asset.FileHash).Append('\n').Append(Stamp(source));
        if (MotionJson.IsMotion(source))
        {
            var manifest = MotionJson.ReadManifest(source);
            foreach (var role in new[] { "preset", "rig", "body", "prop" })
                fingerprint.Append('\n').Append(Stamp(MotionJson.ResolveObject(source, manifest.Objects[role], verify: false)));
            var presentation = MotionJson.PresentationPath(source);
            LocalStorageProvider.RequireLocalPath(presentation);
            if (File.Exists(presentation)) fingerprint.Append('\n').Append(MotionJson.Hash(File.ReadAllBytes(presentation)));
        }
        else if (asset.SourceFormat == ".gltf")
        {
            foreach (var dependency in ImportService.ReadDependencies(source, ".gltf"))
                fingerprint.Append('\n').Append(dependency.Relative).Append('\n').Append(Stamp(dependency.Source));
        }
        return (source, Hash(fingerprint.ToString()));
    }

    internal AssetThumbnailRequest PrepareThumbnail(AssetItem asset)
    {
        var id = SourceIdentity(asset);
        return new(id.Source, ThumbnailsDirectory + id.Hash + ".png");
    }

    internal (string Source, string Cache) PrepareMotion(AssetItem asset)
    {
        var id = SourceIdentity(asset);
        return (id.Source, MotionDirectory + id.Hash + ".bin");
    }

    internal MotionSamples[]? ReadMotion(string key)
    {
        var bytes = ReadCache(key, MotionSampleCodec.MaxBytes);
        return bytes == null ? null : MotionSampleCodec.Decode(bytes);
    }

    internal void SaveMotion(string key, MotionSamples[] samples)
    {
        if (MotionSampleCodec.Encode(samples) is { } bytes) WriteCache(key, bytes);
    }

    internal byte[]? ReadThumbnail(AssetThumbnailRequest request)
    {
        var bytes = ReadCache(request.CachePath);
        return bytes is { Length: >= 33 } && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) == ThumbnailWidth &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) == ThumbnailHeight ? bytes : null;
    }

    internal void SaveThumbnail(AssetThumbnailRequest request, byte[] bytes) => WriteCache(request.CachePath, bytes);

    private byte[]? ReadCache(string relative, int maxBytes = MaxCacheFileSize)
    {
        try
        {
            using var stream = File.Open(storage.Resolve(relative), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > maxBytes) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteCache(string relative, byte[] bytes)
    {
        try
        {
            writeWhenAvailable(() =>
            {
                var target = storage.Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew)) stream.Write(bytes);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Valid(CachedAttributes? cached, string sourceStamp) => cached?.SourceStamp == sourceStamp &&
        cached.Attributes is { Skeletons: not null } attributes &&
        attributes.Skeletons.All(s => s != null && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name)) &&
        (attributes.Duration == null || double.IsFinite(attributes.Duration.Value) && attributes.Duration.Value >= 0) &&
        (attributes.SourceFps == null || double.IsFinite(attributes.SourceFps.Value) && attributes.SourceFps.Value > 0);

    private static AssetSkeleton[] ReadRig(string path)
    {
        try { return AssetAttributeReader.ReadMotionSkeleton(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
