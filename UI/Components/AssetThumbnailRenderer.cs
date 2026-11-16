using Animanager.Core.Browse;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Godot;

namespace Animanager.UI.Components;

/// <summary>Owns the scene, camera and pose used to draw cached asset thumbnails.</summary>
internal partial class AssetThumbnailRenderer : SubViewport
{
    private Camera3D _camera = null!;

    public override void _Ready()
    {
        Size = new(AssetBrowseCache.ThumbnailWidth, AssetBrowseCache.ThumbnailHeight);
        OwnWorld3D = true;
        RenderTargetUpdateMode = UpdateMode.Disabled;
        Msaa3D = Viewport.Msaa.Msaa2X;
        AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("242b34"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("e9efff"), AmbientLightEnergy = .75f
        } });
        AddChild(new DirectionalLight3D { RotationDegrees = new(-38, -30, 0), LightEnergy = 1.5f });
        AddChild(new DirectionalLight3D { RotationDegrees = new(-24, 135, 0), LightEnergy = .65f, LightColor = new Color("b9cde8") });
        _camera = new Camera3D { Current = true, Projection = Camera3D.ProjectionType.Orthogonal };
        AddChild(_camera);
    }

    internal async Task<Image?> RenderAsync(string path, Func<bool> current)
    {
        Node3D? model = null;
        MeshInstance3D? pose = null;
        try
        {
            model = MotionJson.IsMotion(path) ? new MotionPlayback(path, verifySourceObjects: false).Root : SceneAsset.Load(path, out _);
            Preview3DViewport.ApplyClay(model, enabled: true);
            AddChild(model);
            // Imported cameras belong to their original scenes and must not replace the thumbnail camera.
            _camera.MakeCurrent();
            var nodes = Preview3DViewport.Descendants(model).ToArray();
            var player = nodes.OfType<AnimationPlayer>().FirstOrDefault();
            SetPose(player);
            foreach (var attachment in nodes.OfType<BoneAttachment3D>()) attachment.OnSkeletonUpdate();

            Aabb? bounds = null;
            if (nodes.OfType<MeshInstance3D>().Any(mesh => mesh.Mesh != null && mesh.IsVisibleInTree()))
                bounds = Preview3DViewport.CalculateWorldBounds(model);
            if (bounds == null)
            {
                var lines = PoseLines(nodes).ToArray();
                if (lines.Length == 0) return null;
                (pose, bounds) = DrawPose(lines);
                AddChild(pose);
            }
            if (bounds is not { } box || !box.Position.IsFinite() || !box.Size.IsFinite()) return null;
            float span = Math.Max(.04f, box.Size.Length());
            var center = box.GetCenter();
            _camera.Size = span * 1.1f;
            _camera.Near = Math.Max(.001f, span / 1000);
            _camera.Far = Math.Max(10, span * 5);
            _camera.Position = center + new Vector3(.53f, .20f, .82f).Normalized() * Math.Max(.1f, span * 2);
            _camera.LookAt(center);
            RenderTargetUpdateMode = UpdateMode.Once;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!current() || !IsInstanceValid(this) || !IsInsideTree()) return null;
            return GetTexture().GetImage();
        }
        finally
        {
            foreach (var node in new Node3D?[] { model, pose })
            {
                if (!IsInstanceValid(node)) continue;
                if (IsInstanceValid(this) && node!.GetParent() == this) RemoveChild(node);
                node!.QueueFree();
            }
        }
    }

    private static void SetPose(AnimationPlayer? player)
    {
        if (player == null) return;
        var clip = player.GetAnimationList().FirstOrDefault(name => name != "RESET");
        if (clip == null) return;
        player.Play(clip);
        player.Advance(0);
        player.Pause();
        player.Seek(player.GetAnimation(clip).Length * .35, update: true);
    }

    private static IEnumerable<(Vector3 From, Vector3 To)> PoseLines(Node[] nodes)
    {
        foreach (var skeleton in nodes.OfType<Skeleton3D>())
        {
            for (int bone = 0; bone < skeleton.GetBoneCount(); bone++)
            {
                int parent = skeleton.GetBoneParent(bone);
                if (parent < 0) continue;
                var from = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(parent).Origin;
                var to = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone).Origin;
                if (Drawable(from, to)) yield return (from, to);
            }
        }

        // Track-only glTF can preserve its joint positions as ordinary scene nodes.
        if (nodes.Any(node => node is Skeleton3D)) yield break;
        var modelNodes = nodes.ToHashSet();
        foreach (var node in nodes.OfType<Node3D>())
            if (node.GetParent() is Node3D parent && modelNodes.Contains(parent) && Drawable(parent.GlobalPosition, node.GlobalPosition))
                yield return (parent.GlobalPosition, node.GlobalPosition);
    }

    private static bool Drawable(Vector3 from, Vector3 to) => from.IsFinite() && to.IsFinite() && from.DistanceSquaredTo(to) > 1e-10f;

    private static (MeshInstance3D Node, Aabb Bounds) DrawPose((Vector3 From, Vector3 To)[] lines)
    {
        var bounds = new Aabb(lines[0].From, Vector3.Zero);
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (from, to) in lines)
        {
            mesh.SurfaceAddVertex(from);
            mesh.SurfaceAddVertex(to);
            bounds = bounds.Expand(from).Expand(to);
        }
        mesh.SurfaceEnd();
        return (new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("c5def7")
        } }, bounds);
    }
}
