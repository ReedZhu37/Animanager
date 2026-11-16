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
        var signature = "Kaydara FBX Binary  \0\x1a\0"u8;
        if (!reader.ReadBytes(signature.Length).AsSpan().SequenceEqual(signature)) return AssetAttributes.Unknown;
        var version = reader.ReadUInt32();
        if (version < 7000) return AssetAttributes.Unknown;
        return new FbxAttributeReader(reader, version >= 7500, cancellationToken).ReadAttributes(version);
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
