using Animanager.Core.Browse;
using Animanager.Core.Motion;
using Godot;
using System.Text.RegularExpressions;

namespace Animanager.Core.Pipeline;

internal static class AnimationDisplacement
{
    [Flags]
    private enum Axes { None = 0, Horizontal = 1, Vertical = 2, Both = Horizontal | Vertical }

    internal readonly record struct Inspection(string? Tag, bool InPlace, string? Warning = null, bool ContainsAnimation = false)
    {
        internal bool HasAnimation => Tag != null || InPlace || Warning != null || ContainsAnimation;
    }

    private const float Threshold = 0.01f; // Meters in Godot's Y-up scene.
    private const float KeyTolerance = 0.000001f; // Ignore numerical noise from baking a fixed pose.
    private static readonly string[] Tags = ["无位移", "水平位移", "垂直位移", "混合"];
    private static readonly Regex InPlaceMarker = new(
        @"(?<![a-z])in[\s_-]*place(?![a-z])|(?<=(?-i:[a-z]))(?-i:In[Pp]lace)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InPlaceAbbreviation = new(
        @"(?:^|__)(?:\d+_)?ip(?=__|$)|[_\s-]ip(?:_\d+)?(?=$|\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InPlaceFolder = new(
        @"^(?:in[\s_-]*place|(?:\d+_)?ip)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsInPlace(string name) => InPlaceMarker.IsMatch(name) || InPlaceAbbreviation.IsMatch(name);

    internal static bool IsInPlaceSource(string path) => IsInPlace(Path.GetFileName(path))
        || (Path.GetDirectoryName(path)?.Split(['/', '\\']).Any(InPlaceFolder.IsMatch) ?? false);

    internal static bool IsInPlace(MotionManifest manifest) => IsInPlace(manifest.Name) || IsInPlace(manifest.Clip)
        || (manifest.Provenance.TryGetValue("source", out var source) && IsInPlaceSource(source));

    internal static string WithTag(string existing, string tag)
    {
        if (WorkspaceApi.ParseTags(existing).Where(Tags.Contains).SequenceEqual([tag])) return existing;
        return string.Join(" ", WorkspaceApi.ParseTags(existing).Where(value => !Tags.Contains(value)).Append(tag));
    }

    internal static Inspection InspectScene(string path, CancellationToken ct, bool namedInPlace = false,
        bool measureDisplacement = true)
    {
        namedInPlace |= IsInPlace(Path.GetFileName(path));
        bool fbx = Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        double? fbxFps = null;
        // FBX exporters can emit an empty animation stack for a static reference rig.
        if (fbx)
        {
            var hasKeys = FbxAttributeReader.HasAnimationKeys(path, ct, out fbxFps);
            if (hasKeys == false) return default;
        }
        using GltfDocument document = fbx ? new FbxDocument() : new GltfDocument();
        using GltfState state = fbx ? new FbxState() : new GltfState();
        state.HandleBinaryImageMode = GltfState.HandleBinaryImageModeEnum.DiscardTextures;
        var flags = GltfDocument.ImportFlags.UseNamedSkinBinds;
        // The FBX importer creates mesh instances while parsing. Keep their geometry valid during scene generation.
        if (!fbx && measureDisplacement) flags |= GltfDocument.ImportFlags.DiscardMeshesAndMaterials;
        var error = SceneAsset.AppendFromFile(document, state, path, flags, ct);
        if (error != Error.Ok) throw new InvalidDataException("动画解析失败 · " + error);
        var sourceClips = state.GetAnimations().Select(clip => clip.OriginalName).Where(name => name != "RESET").ToArray();
        bool inPlace = sourceClips.Length > 0 && (namedInPlace || sourceClips.All(IsInPlace));
        if (!fbx && sourceClips.Length == 0) return default;
        var fps = fbxFps is >= 1 and <= 600 ? (float)fbxFps.Value : SceneAsset.SourceFrameRate(state) ?? 30;
        var scene = document.GenerateScene(state, Math.Max(30, fps), false, false);
        if (scene == null) throw new InvalidDataException("未能生成动画场景。");
        try
        {
            if (sourceClips.Length == 0) return default;
            var nodes = Descendants(scene).ToArray();
            var roots = nodes.OfType<Skeleton3D>().Select(skeleton => (skeleton, RootBone(skeleton)))
                .Where(root => root.Item2 >= 0).ToArray();
            Axes axes = Axes.None;
            bool hasClip = false;
            foreach (var player in nodes.OfType<AnimationPlayer>())
            {
                var origin = player.GetNodeOrNull(player.RootNode);
                if (origin == null) continue;
                foreach (var name in player.GetAnimationList())
                {
                    if (name == "RESET") continue;
                    ct.ThrowIfCancellationRequested();
                    var clip = player.GetAnimation(name);
                    if (!measureDisplacement)
                    {
                        // Library cleanup also preserves animated props and morph targets.
                        for (int track = 0; track < clip.GetTrackCount(); track++)
                        {
                            if (clip.TrackIsEnabled(track) && clip.TrackGetType(track) is
                                (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or
                                 Animation.TrackType.Scale3D or Animation.TrackType.BlendShape) &&
                                HasChangingKeys(clip, track, ct)) return new(null, false, ContainsAnimation: true);
                        }
                        continue;
                    }
                    var tracks = ReadTransformTracks(clip, origin);
                    // A take on a rigged scene must move a bone. Scenes without a skeleton can animate their nodes.
                    if (!tracks.Where(pair => roots.Length == 0 || pair.Key.Bone >= 0)
                        .SelectMany(pair => pair.Value.Indices).Any(track => HasChangingKeys(clip, track, ct))) continue;
                    if (inPlace) return new(null, true);
                    hasClip = true;
                    try { axes |= MeasureClip(clip, tracks, roots, ct); }
                    catch (InvalidDataException measurementError) { return new(null, false, measurementError.Message); }
                    if (axes == Axes.Both) return new(Tags[(int)axes], false);
                }
            }
            return hasClip ? new(Tags[(int)axes], false) : default;
        }
        finally { scene.Free(); }
    }

    internal static string? InspectMotion(string path, MotionManifest manifest, CancellationToken ct)
    {
        var body = MotionGlb.Read(MotionJson.ResolveObject(path, manifest.Objects["body"], false));
        var root = Array.FindIndex(body.Nodes, bone => bone.Name.Equals("root", StringComparison.OrdinalIgnoreCase));
        if (root < 0) throw new InvalidDataException("分离式动画缺少根骨骼。");
        var chain = new HashSet<int>();
        for (int node = root; node >= 0; node = body.Nodes[node].Parent)
            if (!chain.Add(node)) throw new InvalidDataException("根骨骼层级存在循环。");
        if (!body.Channels.Any(channel => HasChangingKeys(channel, ct)))
        {
            var prop = MotionGlb.Read(MotionJson.ResolveObject(path, manifest.Objects["prop"], false));
            if (!prop.Channels.Any(channel => HasChangingKeys(channel, ct))) return null;
        }
        // The body GLB contains the coordinate basis that converts the source Z-up poses to Y-up.
        var times = body.Channels.Where(channel => chain.Contains(channel.Node) &&
            (channel.Node != root || channel.Path == "translation")).SelectMany(channel => channel.Times.Select(t => (double)t));
        return Tags[(int)Measure(times, time =>
        {
            var position = body.WorldAt(root, time).Translation;
            return new Vector3(position.X, position.Y, position.Z);
        }, ct)];
    }

    private static bool HasChangingKeys(Animation clip, int track, CancellationToken ct)
    {
        if (track < 0) return false;
        int count = clip.TrackGetKeyCount(track);
        if (count < 2) return false;
        var type = clip.TrackGetType(track);
        var first = Key(0);
        for (int key = 1; key < count; key++)
        {
            ct.ThrowIfCancellationRequested();
            if (KeyChanged(first, Key(key), type == Animation.TrackType.Rotation3D)) return true;
        }
        return false;

        Vector4 Key(int key)
        {
            var value = clip.TrackGetKeyValue(track, key);
            if (type == Animation.TrackType.Rotation3D)
            {
                var q = value.AsQuaternion();
                return new(q.X, q.Y, q.Z, q.W);
            }
            if (type == Animation.TrackType.BlendShape) return new(value.AsSingle(), 0, 0, 0);
            var v = value.AsVector3();
            return new(v.X, v.Y, v.Z, 0);
        }
    }

    private static bool HasChangingKeys(MotionChannel channel, CancellationToken ct)
    {
        bool rotation = channel.Path == "rotation";
        var first = Key(channel.Values[0]);
        foreach (var value in channel.Values.AsSpan(1))
        {
            ct.ThrowIfCancellationRequested();
            if (KeyChanged(first, Key(value), rotation)) return true;
        }
        return false;

        Vector4 Key(float[] value) => new(value[0], value[1], value[2], rotation ? value[3] : 0);
    }

    private static bool KeyChanged(Vector4 first, Vector4 current, bool rotation)
    {
        if (!first.IsFinite() || !current.IsFinite()) throw new InvalidDataException("动画关键帧含无效数值。");
        // Opposite signs represent the same rotation.
        var delta = rotation && first.Dot(current) < 0 ? first + current : first - current;
        return delta.LengthSquared() > KeyTolerance * KeyTolerance;
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var node in Descendants(child))
                yield return node;
    }

    private static int RootBone(Skeleton3D skeleton)
    {
        var roots = skeleton.GetParentlessBones();
        foreach (int bone in roots)
            if (skeleton.GetBoneName(bone).ToString().Equals("root", StringComparison.OrdinalIgnoreCase)) return bone;
        // Rigs without a named root can have separate IK hierarchies. Use the principal bone tree.
        return roots.OrderByDescending(bone => BoneCount(skeleton, bone)).FirstOrDefault(-1);
    }

    private static int BoneCount(Skeleton3D skeleton, int bone) =>
        1 + skeleton.GetBoneChildren(bone).Sum(child => BoneCount(skeleton, child));

    private static Dictionary<(Node3D Node, int Bone), TransformTracks> ReadTransformTracks(Animation clip, Node origin)
    {
        var tracks = new Dictionary<(Node3D Node, int Bone), TransformTracks>();
        for (int track = 0; track < clip.GetTrackCount(); track++)
        {
            var type = clip.TrackGetType(track);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D)
                || !clip.TrackIsEnabled(track) || clip.TrackGetKeyCount(track) == 0) continue;
            using var path = clip.TrackGetPath(track);
            using var nodePath = new NodePath(path.GetConcatenatedNames().ToString());
            if (origin.GetNodeOrNull(nodePath) is not Node3D node) continue;
            int bone = -1;
            if (path.GetSubNameCount() > 0)
            {
                if (node is not Skeleton3D skeleton || (bone = skeleton.FindBone(path.GetSubName(0).ToString())) < 0) continue;
            }
            var key = (node, bone);
            if (!tracks.TryGetValue(key, out var transform)) tracks.Add(key, transform = new());
            transform.Set(type, track);
        }
        return tracks;
    }

    private static Axes MeasureClip(Animation clip, Dictionary<(Node3D Node, int Bone), TransformTracks> tracks,
        (Skeleton3D Skeleton, int Bone)[] roots, CancellationToken ct)
    {
        var sources = roots.Select(root => ((Node3D)root.Skeleton, root.Bone));
        if (roots.Length == 0)
        {
            var moving = tracks.Where(pair => pair.Value.Position >= 0).Select(pair => pair.Key.Node).ToHashSet();
            sources = moving.Where(node => !Ancestors(node.GetParentNode3D()).Any(moving.Contains)).Select(node => (node, -1));
        }
        Axes axes = Axes.None;
        foreach (var (node, bone) in sources)
        {
            var chain = Ancestors(node).Select(parent => (parent.Transform, Tracks: tracks.GetValueOrDefault((parent, -1)))).ToArray();
            int rootPosition = bone >= 0 ? tracks.GetValueOrDefault((node, bone))?.Position ?? -1 : -1;
            var rest = bone >= 0 ? ((Skeleton3D)node).GetBoneRest(bone).Origin : Vector3.Zero;
            var times = chain.SelectMany(link => link.Tracks?.Indices ?? []).Append(rootPosition)
                .Where(track => track >= 0).SelectMany(track =>
                    Enumerable.Range(0, clip.TrackGetKeyCount(track)).Select(key => clip.TrackGetKeyTime(track, key)));
            axes |= Measure(times, time =>
            {
                var position = rootPosition < 0 ? rest : clip.PositionTrackInterpolate(rootPosition, time, true);
                foreach (var link in chain)
                    position = (link.Tracks?.At(clip, link.Transform, time) ?? link.Transform) * position;
                return position;
            }, ct);
            if (axes == Axes.Both) break;
        }
        return axes;
    }

    private static IEnumerable<Node3D> Ancestors(Node3D? node)
    {
        for (var current = node; current != null; current = current.TopLevel ? null : current.GetParentNode3D())
            yield return current;
    }

    private static Axes Measure(IEnumerable<double> keyTimes, Func<double, Vector3> positionAt, CancellationToken ct)
    {
        var times = keyTimes.Append(0).Distinct().Order().ToArray();
        if (times.Length == 1) return Axes.None;
        var start = positionAt(0);
        Axes axes = Axes.None;
        for (int i = 1; i < times.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            axes |= MeasureAt(times[i]) | MeasureAt((times[i - 1] + times[i]) / 2);
            if (axes == Axes.Both) break;
        }
        return axes;

        Axes MeasureAt(double time)
        {
            var delta = positionAt(time) - start;
            if (!delta.IsFinite()) throw new InvalidDataException("根轨迹含无效坐标。");
            var horizontal = delta.X * delta.X + delta.Z * delta.Z >= Threshold * Threshold;
            var vertical = Math.Abs(delta.Y) >= Threshold;
            return (horizontal ? Axes.Horizontal : Axes.None) | (vertical ? Axes.Vertical : Axes.None);
        }
    }

    private sealed class TransformTracks
    {
        internal int Position { get; private set; } = -1;
        private int _rotation = -1, _scale = -1;
        internal int[] Indices => [Position, _rotation, _scale];

        internal void Set(Animation.TrackType type, int track)
        {
            switch (type)
            {
                case Animation.TrackType.Position3D: Position = track; break;
                case Animation.TrackType.Rotation3D: _rotation = track; break;
                case Animation.TrackType.Scale3D: _scale = track; break;
            }
        }

        internal Transform3D At(Animation clip, Transform3D rest, double time)
        {
            var basis = rest.Basis;
            if (_rotation >= 0 || _scale >= 0)
            {
                var rotation = _rotation < 0 ? basis.GetRotationQuaternion() : clip.RotationTrackInterpolate(_rotation, time, true);
                var scale = _scale < 0 ? basis.Scale : clip.ScaleTrackInterpolate(_scale, time, true);
                basis = new Basis(rotation).ScaledLocal(scale);
            }
            var position = Position < 0 ? rest.Origin : clip.PositionTrackInterpolate(Position, time, true);
            return new Transform3D(basis, position);
        }
    }
}
