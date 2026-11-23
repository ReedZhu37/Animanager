using System.Text;

namespace Animanager.Core.Browse;

/// <summary>Reads the rig and declared take metadata in binary FBX without loading meshes or baking tracks.</summary>
internal sealed class FbxAttributeReader(BinaryReader reader, bool wide, CancellationToken cancellationToken)
{
    private readonly record struct Node(string Name, long Properties, long Children, long End, ulong PropertyCount);

    internal static AssetAttributes Read(string path, CancellationToken cancellationToken)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var fbx = Open(reader, cancellationToken, out uint version);
        return fbx?.ReadAttributes(version) ?? AssetAttributes.Unknown;
    }

    internal static bool? HasAnimationKeys(string path, CancellationToken cancellationToken, out double? sourceFps)
    {
        sourceFps = null;
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        var fbx = Open(reader, cancellationToken, out _);
        if (fbx == null) return null;
        var nodes = fbx.Nodes(reader.BaseStream.Position, reader.BaseStream.Length).ToArray();
        var objects = nodes.FirstOrDefault(node => node.Name == "Objects");
        if (objects.Name == null) return null;
        sourceFps = SourceRate(fbx.Properties(nodes.FirstOrDefault(node => node.Name == "GlobalSettings")));
        return fbx.Children(objects).Any(node => node.Name == "AnimationCurve" && fbx.HasKeys(node));
    }

    internal static long[] MissingTextureFilenameOffsets(Stream source, string path, CancellationToken ct)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        var fbx = Open(reader, ct, out _);
        if (fbx == null) return [];

        var top = fbx.Nodes(source.Position, source.Length).ToArray();
        var objects = fbx.Children(top.FirstOrDefault(node => node.Name == "Objects"))
            .Where(node => node.Name is "Video" or "Texture").ToArray();
        var textures = objects.Where(node => node.Name == "Texture").ToArray();
        if (textures.Length == 0) return [];

        var embeddedVideos = new HashSet<long>();
        var embeddedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var video in objects.Where(node => node.Name == "Video"))
        {
            if (!fbx.HasEmbeddedContent(fbx.Child(video, "Content"))) continue;
            if (fbx.Values(video).FirstOrDefault() is long id) embeddedVideos.Add(id);
            foreach (var file in fbx.Children(video).Where(node => node.Name is "FileName" or "Filename")
                .SelectMany(fbx.Values).OfType<string>())
                embeddedFiles.Add(file);
        }

        var embeddedTextures = new HashSet<long>();
        if (embeddedVideos.Count > 0)
        {
            foreach (var connection in fbx.Children(top.FirstOrDefault(node => node.Name == "Connections")))
                if (connection.Name == "C" && fbx.Values(connection) is ["OO", long from, long to] &&
                    embeddedVideos.Contains(from))
                    embeddedTextures.Add(to);
        }

        var missing = new List<long>();
        var directory = Path.GetDirectoryName(path)!;
        foreach (var texture in textures)
        {
            if (fbx.Values(texture).FirstOrDefault() is long id && embeddedTextures.Contains(id)) continue;
            var fields = fbx.Children(texture)
                .Where(node => node.Name is "FileName" or "Filename" or "RelativeFileName" or "RelativeFilename").ToArray();
            var filenames = fields.SelectMany(fbx.Values).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            if (filenames.Length > 0 && !filenames.Any(name => embeddedFiles.Contains(name) || TextureExists(directory, name)))
                missing.AddRange(fields.Select(node => node.Properties - node.Name.Length));
        }
        return missing.ToArray();
    }

    private bool HasEmbeddedContent(Node node)
    {
        if (node.PropertyCount == 0 || node.Children - node.Properties <= 5) return false;
        reader.BaseStream.Position = node.Properties;
        if (reader.ReadByte() is not ((byte)'R' or (byte)'S')) return false;
        uint length = reader.ReadUInt32();
        return length > 0 && length <= node.Children - reader.BaseStream.Position;
    }

    // Godot also searches common texture directories above the model.
    private static bool TextureExists(string directory, string filename)
    {
        filename = filename.Trim().Replace('\\', '/');
        if (File.Exists(Path.Combine(directory, filename))) return true;
        var basename = Path.GetFileName(filename);
        string[] folders = ["", "textures", "Textures", "images", "Images", "materials", "Materials", "maps", "Maps", "tex", "Tex"];
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            if (folders.Any(folder => File.Exists(Path.Combine(current.FullName, folder, basename)))) return true;
        return false;
    }

    private static FbxAttributeReader? Open(BinaryReader reader, CancellationToken cancellationToken, out uint version)
    {
        version = 0;
        var signature = "Kaydara FBX Binary  \0\x1a\0"u8;
        if (!reader.ReadBytes(signature.Length).AsSpan().SequenceEqual(signature)) return null;
        version = reader.ReadUInt32();
        return version < 7000 ? null : new FbxAttributeReader(reader, version >= 7500, cancellationToken);
    }

    private AssetAttributes ReadAttributes(uint version)
    {
        var top = Nodes(reader.BaseStream.Position, reader.BaseStream.Length).ToDictionary(n => n.Name);
        double ticksPerSecond = version >= 8000 ? 141120000 : 46186158000;
        if (top.TryGetValue("FBXHeaderExtension", out var header) &&
            Values(Child(header, "FBXHeaderVersion")) is [long headerVersion] && headerVersion >= 1004 &&
            Values(Child(Child(header, "OtherFlags"), "TCDefinition")) is [long definition])
            ticksPerSecond = definition == 127 ? 46186158000 : 141120000;

        var settings = Properties(top.GetValueOrDefault("GlobalSettings"));
        var bones = new Dictionary<long, string>();
        var takes = new List<double>();
        var hasAnimation = false;
        var hasKeys = false;
        foreach (var node in Children(top.GetValueOrDefault("Objects")))
        {
            if (node.Name == "AnimationStack")
            {
                hasAnimation = true;
                var properties = Properties(node);
                if (TakeDuration(properties, "LocalStart", "LocalStop", ticksPerSecond) is { } local) takes.Add(local);
                else if (TakeDuration(properties, "ReferenceStart", "ReferenceStop", ticksPerSecond) is { } reference) takes.Add(reference);
            }
            else if (node.Name == "Model" && Values(node) is [long id, string name, "LimbNode" or "Limb" or "Root"])
            {
                int suffix = name.IndexOf('\0');
                bones.Add(id, suffix < 0 ? name : name[..suffix]);
            }
            else if (node.Name == "AnimationCurve" && !hasKeys) hasKeys = HasKeys(node);
        }

        if (takes.Count == 0)
        {
            foreach (var take in Children(top.GetValueOrDefault("Takes")).Where(n => n.Name == "Take"))
            {
                hasAnimation = true;
                if (Values(Child(take, "LocalTime")) is [long start, long stop] && stop >= start)
                    takes.Add(((double)stop - start) / ticksPerSecond);
            }
        }

        var parents = new Dictionary<long, long>();
        foreach (var node in Children(top.GetValueOrDefault("Connections")))
            if (node.Name == "C" && Values(node) is ["OO", long child, long parent] && bones.ContainsKey(child) && bones.ContainsKey(parent))
                parents.TryAdd(child, parent);
        var skeletons = bones.Keys.GroupBy(id => Root(id, parents, bones.Count)).Select(group =>
            AssetAttributeReader.SkeletonOf(group.Select(id => (bones[id], parents.TryGetValue(id, out var parent) ? bones[parent] : ""))))
            .DistinctBy(s => s.Id).ToArray();
        return new(skeletons, hasKeys && takes.Count > 0 ? takes.Max() : null, hasKeys && hasAnimation ? SourceRate(settings) : null);
    }

    private bool HasKeys(Node curve)
    {
        var times = Child(curve, "KeyTime");
        if (times.PropertyCount != 1 || times.Children - times.Properties < 13) return false;
        reader.BaseStream.Position = times.Properties;
        return reader.ReadByte() == 'l' && reader.ReadUInt32() > 0;
    }

    private static long Root(long bone, Dictionary<long, long> parents, int count)
    {
        int hops = 0;
        while (parents.TryGetValue(bone, out var parent))
        {
            if (++hops >= count) throw new InvalidDataException("Cyclic FBX bone hierarchy.");
            bone = parent;
        }
        return bone;
    }

    private static double? TakeDuration(Dictionary<string, object> properties, string first, string last, double ticksPerSecond)
    {
        if (properties.GetValueOrDefault(last) is not long stop) return null;
        var start = properties.GetValueOrDefault(first) is long value ? value : 0;
        return stop >= start ? ((double)stop - start) / ticksPerSecond : null;
    }

    private static double? SourceRate(Dictionary<string, object> settings)
    {
        // Default time mode does not declare a source rate. Do not use a playback or baking fallback.
        return settings.GetValueOrDefault("TimeMode") switch
        {
            1L => 120, 2L => 100, 3L => 60, 4L => 50, 5L => 48, 6L or 7L => 30,
            8L or 9L => 29.97, 10L => 25, 11L => 24, 12L => 1000, 13L => 23.976,
            14L when Number(settings.GetValueOrDefault("CustomFrameRate")) is { } rate && double.IsFinite(rate) && rate > 0 => rate,
            15L => 96, 16L => 72, 17L => 59.94,
            _ => null
        };
    }

    private static double? Number(object? value) => value switch { double number => number, long integer => integer, _ => null };

    private Dictionary<string, object> Properties(Node node)
    {
        var fields = new Dictionary<string, object>();
        var container = Children(node).FirstOrDefault(n => n.Name is "Properties70" or "Properties60");
        foreach (var property in Children(container))
        {
            if (property.Name is not ("P" or "Property")) continue;
            var values = Values(property);
            if (values.Length >= 4 && values[0] is string name && values[^1] is long or double)
                fields[name] = values[^1];
        }
        return fields;
    }

    private Node Child(Node node, string name) => Children(node).FirstOrDefault(n => n.Name == name);
    private IEnumerable<Node> Children(Node node) => Nodes(node.Children, node.End);

    private IEnumerable<Node> Nodes(long start, long end)
    {
        var stream = reader.BaseStream;
        int headerSize = wide ? 25 : 13;
        while (start <= end - headerSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = start;
            var next = checked((long)Offset());
            var count = Offset();
            var bytes = checked((long)Offset());
            var nameLength = reader.ReadByte();
            if (next == 0 && count == 0 && bytes == 0 && nameLength == 0) yield break;
            long properties = stream.Position + nameLength;
            if (next < properties || next > end || bytes > next - properties || nameLength == 0)
                throw new InvalidDataException("Invalid FBX node bounds.");
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            yield return new(name, properties, properties + bytes, next, count);
            start = next;
        }
    }

    private ulong Offset() => wide ? reader.ReadUInt64() : reader.ReadUInt32();

    private object[] Values(Node node)
    {
        // Only short scalar property lists are needed for model IDs, timing, and hierarchy connections.
        if (node.PropertyCount == 0 || node.PropertyCount > 10 || node.Children - node.Properties > 4096) return [];
        var stream = reader.BaseStream;
        stream.Position = node.Properties;
        var values = new object[(int)node.PropertyCount];
        for (int i = 0; i < values.Length; i++)
        {
            if (stream.Position >= node.Children) throw new InvalidDataException("Truncated FBX property.");
            switch (reader.ReadByte())
            {
                case (byte)'Y': values[i] = (long)reader.ReadInt16(); break;
                case (byte)'I': values[i] = (long)reader.ReadInt32(); break;
                case (byte)'L': values[i] = reader.ReadInt64(); break;
                case (byte)'F': values[i] = (double)reader.ReadSingle(); break;
                case (byte)'D': values[i] = reader.ReadDouble(); break;
                case (byte)'C': values[i] = reader.ReadBoolean(); break;
                case (byte)'S':
                    var length = reader.ReadInt32();
                    if (length < 0 || length > node.Children - stream.Position) throw new InvalidDataException("Invalid FBX string bounds.");
                    values[i] = Encoding.UTF8.GetString(reader.ReadBytes(length));
                    break;
                default: return [];
            }
            if (stream.Position > node.Children) throw new InvalidDataException("Invalid FBX property bounds.");
        }
        return values;
    }
}
