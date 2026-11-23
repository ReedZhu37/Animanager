using System.Diagnostics;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;
using Animanager.Core.Preview;
using Animanager.Core.Retarget;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.UI.Components;

public partial class Preview3DViewport
{
    private sealed record WeaponAnchor(Node3D Node, int Bone, Transform3D HandMount, float GuardOffset = 0)
    {
        internal static WeaponAnchor ForHand(Skeleton3D skeleton, int bone, bool left)
        {
            var wrist = skeleton.GetBoneGlobalRest(bone);
            if (Math.Abs(wrist.Basis.Determinant()) < 1e-12f)
                return new(skeleton, bone, Transform3D.Identity);
            var wristSpace = wrist.AffineInverse();
            var knuckles = new Dictionary<string, Vector3>();
            string side = left ? "_l" : "_r";
            for (int i = 0; i < skeleton.GetBoneCount(); i++)
            {
                string role = HumanoidMap.Role(skeleton.GetBoneName(i).ToString());
                if (role != "index_01" + side && role != "middle_01" + side && role != "pinky_01" + side) continue;
                int parent = skeleton.GetBoneParent(i);
                while (parent >= 0 && parent != bone) parent = skeleton.GetBoneParent(parent);
                if (parent == bone) knuckles.TryAdd(role, wristSpace * skeleton.GetBoneGlobalRest(i).Origin);
            }

            if (knuckles.TryGetValue("index_01" + side, out var index) && knuckles.TryGetValue("pinky_01" + side, out var pinky))
            {
                var middle = knuckles.GetValueOrDefault("middle_01" + side, (index + pinky) * .5f);
                var fingers = middle.Normalized();
                var acrossKnuckles = index - pinky;
                var width = acrossKnuckles - fingers * acrossKnuckles.Dot(fingers);
                if (!fingers.IsZeroApprox() && !width.IsZeroApprox())
                {
                    var thumbward = width.Normalized();
                    var gripAcross = left ? -fingers : fingers;
                    var insidePalm = thumbward.Cross(gripAcross);
                    var palm = middle * .85f + insidePalm * (width.Length() * .3f);
                    return new(skeleton, bone, new(new Basis(gripAcross, insidePalm, thumbward), palm), .06f);
                }
            }

            // A wrist-only rig can still approximate the palm along the forearm direction.
            int forearm = skeleton.GetBoneParent(bone);
            var estimatedPalm = forearm < 0 ? Vector3.Zero : (wristSpace * skeleton.GetBoneGlobalRest(forearm).Origin) * -.22f;
            return new(skeleton, bone, new(Basis.Identity, estimatedPalm), .06f);
        }

        internal Transform3D Pose(Node3D node)
        {
            if (Bone < 0) return node.GlobalTransform;
            var grip = (node.GlobalTransform * ((Skeleton3D)node).GetBoneGlobalPose(Bone) * HandMount).Orthonormalized();
            // The marker origin is at the guard, so put part of the handle inside the palm.
            grip.Origin += grip.Basis.Z * GuardOffset;
            return grip;
        }
    }

    private sealed record WeaponVisual(PreviewWeapon Weapon, Node3D Root, Node3D Model, MeshInstance3D Box,
        ImmediateMesh Trail, ShaderMaterial Material)
    {
        internal WeaponAnchor? Anchor { get; set; }
        internal float SpeedCeiling { get; set; }
    }

    private sealed record WeaponBinding(Node3D Model, AnimationPlayer Player, PoseSnapshot Baseline,
        string Clip, WeaponVisual[] Visuals, Vector3 SamplingOffset)
    {
        internal double Duration { get; } = Player.GetAnimation(Clip).Length;
    }

    private const float MinimumHeatSpeed = .5f;
    private static readonly Color SlowTrailColor = new("1373ff"), MediumTrailColor = new("ffd000"), FastTrailColor = new("ff2457");
    private static Shader? _trailShader;
    private Button _weaponToggle = null!, _weaponHeatmapToggle = null!;
    private PanelContainer _weaponHeatLegend = null!;
    private Label _weaponHeatLimit = null!;
    private Node3D _weaponStage = null!;
    private WeaponVisual[] _weaponVisuals = [];
    private readonly List<WeaponVisual> _comparisonWeaponVisuals = [];
    private readonly List<WeaponBinding> _weaponBindings = [];
    private bool _showWeapons, _showWeaponBox, _weaponHeatmap;
    private readonly List<(MeshInstance3D Mesh, uint Layers)> _suppressedWeapon = [];
    private CancellationTokenSource? _weaponSampling;

    internal event Action? WeaponPreviewChanged;
    internal bool WeaponPreviewEnabled => _showWeapons;
    internal bool WeaponHeatmapEnabled => _weaponHeatmap;
    internal bool CanPreviewWeapons => _weaponToggle?.Visible == true;
    internal int AvailableWeaponCount => _weaponBindings.Select(binding => binding.Visuals.Length).DefaultIfEmpty().Max();

    private Node3D? WeaponModel => AssetComparison == null && AppliedRetarget != null && _retargetView == 0 ? _comparison : Model;

    private static Node3D? IndependentWeaponTrack(Node3D model) => Descendants(model).OfType<Node3D>().FirstOrDefault(node =>
        node.Name == RetargetBaker.PropNodeName && node.GetNodeOrNull<Node3D>("GripCalibration") != null);

    internal (WeaponSocket Socket, string Label)[] WeaponSockets()
    {
        var choices = new List<(WeaponSocket Socket, string Label)>
        {
            (WeaponSocket.RightPalm, "右手掌"),
            (WeaponSocket.LeftPalm, "左手掌")
        };
        if (WeaponModel is not { } model) return choices.ToArray();
        var motion = model == _comparison ? _comparisonMotion : Motion;
        var track = IndependentWeaponTrack(model);
        if (track != null)
            choices.Add((WeaponSocket.PrimaryTrack, $"{motion?.Manifest.Props[0].SourceBone ?? MotionProp.NodachiSourceBone} · 独立武器轨道"));
        foreach (var node in Descendants(model).OfType<Node3D>())
        {
            if (node == track) continue;
            string path = model.GetPathTo(node).ToString();
            if (node is Skeleton3D skeleton)
            {
                for (int i = 0; i < skeleton.GetBoneCount(); i++)
                {
                    string bone = skeleton.GetBoneName(i).ToString();
                    choices.Add((new(path, bone), $"{bone} · {path}"));
                }
            }
            else choices.Add((new(path), $"节点 · {path}"));
        }
        return choices.ToArray();
    }

    internal void ApplyWeaponProfile(WeaponProfile profile, Func<PreviewWeapon, string?>? modelPath, bool enable)
    {
        profile.Validate();
        var next = new List<WeaponVisual>();
        try
        {
            foreach (var weapon in profile.Weapons) next.Add(CreateWeaponVisual(weapon, modelPath?.Invoke(weapon)));
        }
        catch
        {
            foreach (var visual in next) visual.Root.Free();
            throw;
        }

        if (_weaponStage != null) ClearWeaponPreview(hideToggle: false);
        foreach (var visual in _weaponVisuals) visual.Root.Free();
        _weaponVisuals = next.ToArray();
        if (enable) _showWeapons = true;
        if (_weaponStage == null) return;
        foreach (var visual in _weaponVisuals) _weaponStage.AddChild(visual.Root);
        _weaponToggle.SetPressedNoSignal(_showWeapons);
        RefreshWeaponPreview();
    }

    internal void SetWeaponPreviewEnabled(bool enabled)
    {
        _showWeapons = enabled;
        if (_weaponToggle == null) return;
        _weaponToggle.SetPressedNoSignal(enabled);
        RefreshWeaponPreview();
    }

    internal void SetWeaponBoxVisible(bool visible)
    {
        _showWeaponBox = visible;
        if (visible && CanPreviewWeapons && !_showWeapons) SetWeaponPreviewEnabled(true);
        else Redraw();
    }

    internal void SetWeaponHeatmap(bool enabled)
    {
        _weaponHeatmap = enabled;
        foreach (var visual in _weaponVisuals.Concat(_comparisonWeaponVisuals))
            visual.Material.SetShaderParameter("heatmap", enabled);
        if (enabled && !_showWeapons)
        {
            SetWeaponPreviewEnabled(true);
            return;
        }
        UpdateWeaponHeatScale();
        Redraw();
        WeaponPreviewChanged?.Invoke();
    }

    private void BuildWeaponPreview(Control overlay)
    {
        _weaponToggle = new Button
        {
            Name = "WeaponPreview", Text = "武器轨迹", ToggleMode = true, ButtonPressed = _showWeapons,
            FocusMode = FocusModeEnum.None, Position = new(44, 8), Visible = false
        };
        _weaponToggle.AddThemeFontSizeOverride("font_size", 12);
        _weaponToggle.Toggled += SetWeaponPreviewEnabled;
        overlay.AddChild(_weaponToggle);
        _weaponHeatmapToggle = new Button
        {
            Name = "ViewportWeaponHeatmap", Text = "速度热力", ToggleMode = true, ButtonPressed = _weaponHeatmap,
            FocusMode = FocusModeEnum.None, Visible = false,
            TooltipText = "按武器尖端速度给轨迹着色"
        };
        _weaponHeatmapToggle.AddThemeFontSizeOverride("font_size", 12);
        _weaponHeatmapToggle.Toggled += SetWeaponHeatmap;
        overlay.AddChild(_weaponHeatmapToggle);
        void PlaceHeatToggle() => _weaponHeatmapToggle.Position = _weaponToggle.Position + new Vector2(_weaponToggle.Size.X + 5, 0);
        _weaponToggle.Resized += PlaceHeatToggle;
        PlaceHeatToggle();

        _weaponHeatLegend = new PanelContainer { Name = "WeaponHeatLegend", MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _weaponHeatLegend.AddThemeStyleboxOverride("panel", Surfaces.Fill(Tokens.Sunken with { A = .9f }, padX: 8, padY: 4));
        overlay.AddChild(_weaponHeatLegend);
        _weaponHeatLegend.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        _weaponHeatLegend.OffsetLeft = 8;
        _weaponHeatLegend.OffsetTop = -36;
        var legend = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _weaponHeatLegend.AddChild(legend);
        legend.AddChild(Shell.Type.CaptionLabel("慢", SlowTrailColor));
        foreach (var color in new[] { SlowTrailColor, MediumTrailColor, FastTrailColor })
            legend.AddChild(new ColorRect { Color = color, CustomMinimumSize = new(14, 6),
                SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        _weaponHeatLimit = Shell.Type.CaptionLabel("快", FastTrailColor);
        legend.AddChild(_weaponHeatLimit);

        // These nodes live beside the asset scene and stay out of its exports.
        _weaponStage = new Node3D { Name = "WeaponPreviewModels" };
        _stage.AddChild(_weaponStage);
        if (_weaponVisuals.Length == 0) _weaponVisuals = [CreateWeaponVisual(WeaponProfile.Reference.Weapons[0], null)];
        foreach (var visual in _weaponVisuals) _weaponStage.AddChild(visual.Root);
    }

    internal static Node3D CreateWeaponMesh(PreviewWeapon weapon, string? modelPath)
    {
        Node3D mesh;
        if (weapon.Builtin)
        {
            mesh = new Node3D { Name = "BladeModel" };
            MotionPlayback.AddMarker(mesh);
        }
        else
        {
            var imported = SceneAsset.Load(modelPath ?? throw new IOException("找不到武器模型：" + weapon.Name), out _);
            mesh = new Node3D { Name = "WeaponMesh" };
            mesh.AddChild(imported);
        }
        mesh.Transform = weapon.Mesh.Transform;
        return mesh;
    }

    private WeaponVisual CreateWeaponVisual(PreviewWeapon weapon, string? modelPath, Node3D? preparedMesh = null)
    {
        var root = new Node3D { Name = "Weapon", Visible = false };
        try
        {
            var model = new Node3D { Name = "ReferenceWeapon" };
            root.AddChild(model);
            model.AddChild(preparedMesh == null ? CreateWeaponMesh(weapon, modelPath) : (Node3D)preparedMesh.Duplicate(0));

            var box = new MeshInstance3D
            {
                Name = "WeaponTrailBox", Visible = false, Mesh = new BoxMesh { Size = weapon.Trail.Size.Value },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor = new Color(.8f, .91f, 1, .22f)
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            root.AddChild(box);
            _trailShader ??= new Shader { Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                uniform float playhead;
                uniform float trail_seconds;
                uniform bool heatmap;
                uniform float speed_ceiling;
                uniform vec4 slow_color : source_color;
                uniform vec4 medium_color : source_color;
                uniform vec4 fast_color : source_color;
                void fragment() {
                    float age = playhead - UV.x;
                    if (age < 0.0 || age > trail_seconds) discard;
                    float phase = age / trail_seconds;
                    if (heatmap) {
                        float speed = clamp(UV.y / speed_ceiling, 0.0, 1.0);
                        ALBEDO = speed < 0.5
                            ? mix(slow_color.rgb, medium_color.rgb, speed * 2.0)
                            : mix(medium_color.rgb, fast_color.rgb, speed * 2.0 - 1.0);
                        float edge = smoothstep(0.82, 0.97, UV2.x);
                        ALPHA = mix(0.84, 1.0, edge) * (1.0 - smoothstep(0.35, 1.0, phase));
                    } else {
                        ALBEDO = vec3(1.0);
                        ALPHA = 0.72 * (1.0 - smoothstep(0.0, 1.0, phase));
                    }
                }
                """ };
            var material = new ShaderMaterial { Shader = _trailShader };
            material.SetShaderParameter("trail_seconds", weapon.TrailSeconds);
            material.SetShaderParameter("heatmap", _weaponHeatmap);
            material.SetShaderParameter("speed_ceiling", MinimumHeatSpeed);
            material.SetShaderParameter("slow_color", SlowTrailColor);
            material.SetShaderParameter("medium_color", MediumTrailColor);
            material.SetShaderParameter("fast_color", FastTrailColor);
            var trail = new ImmediateMesh();
            root.AddChild(new MeshInstance3D
            {
                Name = "WeaponTrail", Mesh = trail, MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
            return new(weapon, root, model, box, trail, material);
        }
        catch
        {
            root.Free();
            throw;
        }
    }

    private void ClearWeaponPreview(bool hideToggle = true)
    {
        _weaponSampling?.Cancel();
        _weaponSampling = null;
        _weaponBindings.Clear();
        foreach (var visual in _weaponVisuals)
        {
            visual.Anchor = null;
            visual.SpeedCeiling = 0;
            visual.Root.Hide();
            visual.Model.Hide();
            visual.Box.Hide();
            visual.Trail.ClearSurfaces();
        }
        foreach (var (mesh, layers) in _suppressedWeapon)
            if (GodotObject.IsInstanceValid(mesh)) mesh.Layers = layers;
        _suppressedWeapon.Clear();
        foreach (var visual in _comparisonWeaponVisuals) visual.Root.Free();
        _comparisonWeaponVisuals.Clear();
        if (hideToggle)
        {
            _weaponToggle.Hide();
            UpdateWeaponHeatScale();
            WeaponPreviewChanged?.Invoke();
        }
    }

    private void RefreshWeaponPreview()
    {
        RebindWeapons();
        WeaponPreviewChanged?.Invoke();
    }

    private void RebindWeapons()
    {
        ClearWeaponPreview(hideToggle: false);
        bool canPreview = false;
        if (AssetComparison is { } comparison)
        {
            foreach (var entry in comparison.Entries)
                Bind(entry.Root, entry.Player, entry.Baseline, entry.Clip, entry.Slot);
        }
        else if (AppliedRetarget != null && _retargetView == 2)
        {
            Bind(Model, Player, _baseline, CurrentClip, _weaponStage);
            Bind(_comparison, _comparisonPlayer, _comparisonRest, CurrentClip, _weaponStage,
                extraVisuals: true, samplingOffset: Vector3.Left * 1.2f);
        }
        else
        {
            bool sourceView = AppliedRetarget != null && _retargetView == 0;
            Bind(WeaponModel, sourceView ? _comparisonPlayer : Player, sourceView ? _comparisonRest : _baseline,
                CurrentClip, _weaponStage);
        }

        _weaponToggle.Visible = canPreview;
        _weaponToggle.TooltipText = AvailableWeaponCount == 0 ? "当前 Profile 无可用挂点，可以在概览中更换或编辑。"
            : "显示武器和拖尾";
        if (_showWeapons && _weaponBindings.Count > 0)
        {
            var request = _weaponSampling = new CancellationTokenSource();
            _ = SampleWeaponTrails(_weaponBindings.ToArray(), request);
        }
        UpdateWeaponHeatScale();
        Redraw();

        void Bind(Node3D? model, AnimationPlayer? player, PoseSnapshot? baseline, string clip, Node3D parent,
            bool extraVisuals = false, Vector3 samplingOffset = default)
        {
            if (model == null || player == null || baseline == null || !player.HasAnimation(clip)) return;
            canPreview = true;
            var track = IndependentWeaponTrack(model);
            var grip = track?.GetNodeOrNull<Node3D>("GripCalibration");
            var bound = new List<WeaponVisual>();
            bool keepOriginalMarker = false;
            foreach (var template in _weaponVisuals)
            {
                var weapon = template.Weapon;
                var anchor = BindWeapon(model, track, grip, weapon);
                if (anchor == null) continue;
                var visual = template;
                if (_showWeapons && (extraVisuals || parent != _weaponStage))
                {
                    visual = CreateWeaponVisual(weapon, null, template.Model.GetChild<Node3D>(0));
                    parent.AddChild(visual.Root);
                    _comparisonWeaponVisuals.Add(visual);
                }
                bound.Add(visual);
                if (!_showWeapons) continue;
                visual.Anchor = anchor;
                bool usesOriginalMarker = !keepOriginalMarker && weapon.Builtin && weapon.Socket == null &&
                    weapon.Position == default && weapon.Rotation == default && weapon.Mesh == WeaponMeshSettings.Default &&
                    grip?.IsVisibleInTree() == true;
                keepOriginalMarker |= usesOriginalMarker;
                visual.Root.Show();
                visual.Model.Visible = !usesOriginalMarker;
            }

            if (bound.Count == 0) return;
            _weaponBindings.Add(new(model, player, baseline, clip, bound.ToArray(), samplingOffset));
            if (_showWeapons && !keepOriginalMarker && grip != null)
            {
                foreach (var mesh in Descendants(grip).OfType<MeshInstance3D>())
                {
                    _suppressedWeapon.Add((mesh, mesh.Layers));
                    mesh.Layers = 0;
                }
            }
        }
    }

    private static WeaponAnchor? BindWeapon(Node3D model, Node3D? track, Node3D? grip, PreviewWeapon weapon)
    {
        if (weapon.Socket is { Palm: null } socket) return ResolveWeaponSocket(model, track, socket);
        if (weapon.Socket == null && grip != null)
            return new(weapon.Builtin ? grip : track!, -1, Transform3D.Identity);
        string hand = weapon.Socket?.Palm ?? "hand_r";
        var matches = Descendants(model).OfType<Skeleton3D>()
            .SelectMany(skeleton => Enumerable.Range(0, skeleton.GetBoneCount())
                .Where(bone => HumanoidMap.Role(skeleton.GetBoneName(bone).ToString()) == hand)
                .Select(bone => (Skeleton: skeleton, Bone: bone))).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var anchor = WeaponAnchor.ForHand(matches[0].Skeleton, matches[0].Bone, hand == "hand_l");
        return weapon.Builtin ? anchor : anchor with { GuardOffset = 0 };
    }

    private static WeaponAnchor? ResolveWeaponSocket(Node3D model, Node3D? track, WeaponSocket socket)
    {
        if (track != null && socket.ResolveLegacyTrack() == WeaponSocket.PrimaryTrack)
            return new(track, -1, Transform3D.Identity);
        if (socket.SourceTrack != null) return null;
        var nodes = Descendants(model).OfType<Node3D>().ToArray();
        var node = nodes.FirstOrDefault(item => model.GetPathTo(item).ToString() == socket.NodePath);
        if (socket.Bone is not { } bone) return node == null ? null : new(node, -1, Transform3D.Identity);
        if (node is Skeleton3D skeleton && skeleton.FindBone(bone) is int index && index >= 0)
            return new(skeleton, index, Transform3D.Identity);
        var matches = nodes.OfType<Skeleton3D>().Where(item => item.FindBone(bone) >= 0).Take(2).ToArray();
        return matches.Length == 1 ? new(matches[0], matches[0].FindBone(bone), Transform3D.Identity) : null;
    }

    private static Transform3D WeaponPose(WeaponAnchor anchor, Node3D node, PreviewWeapon weapon)
    {
        var pose = anchor.Pose(node);
        return weapon.Builtin && weapon.Socket == null && weapon.Position == default && weapon.Rotation == default
            ? pose : pose.Orthonormalized() * weapon.Mount;
    }

    private void UpdateWeaponPreviewPose()
    {
        if (!_showWeapons) return;
        foreach (var binding in _weaponBindings)
        {
            double time = Math.Min(TransportPlayhead, binding.Duration);
            foreach (var visual in binding.Visuals)
            {
                var anchor = visual.Anchor!;
                var pose = WeaponPose(anchor, anchor.Node, visual.Weapon);
                visual.Model.GlobalTransform = pose;
                visual.Box.Visible = _showWeaponBox;
                if (_showWeaponBox) visual.Box.GlobalTransform = pose * visual.Weapon.Trail.Transform;
                visual.Material.SetShaderParameter("playhead", time);
            }
        }
    }

    private async Task SampleWeaponTrails(WeaponBinding[] bindings, CancellationTokenSource request)
    {
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var binding in bindings)
            {
                request.Token.ThrowIfCancellationRequested();
                try { await Sample(binding, request.Token); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                {
                    _weaponToggle.TooltipText = $"部分轨迹不可用 · {error.Message}";
                }
                UpdateWeaponHeatScale();
                Redraw();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_weaponSampling == request)
            {
                _weaponSampling = null;
                if (IsInsideTree()) { UpdateWeaponHeatScale(); Redraw(); }
            }
            request.Dispose();
        }
    }

    private async Task Sample(WeaponBinding binding, CancellationToken ct)
    {
        var (clone, sampler) = TrajectorySampler.Create(_stage, binding.Model, binding.Player, binding.Baseline, binding.Clip);
        try
        {
            var attachments = binding.Visuals.Any(visual => visual.Anchor!.Bone < 0)
                ? Descendants(clone).OfType<BoneAttachment3D>().ToArray() : [];
            double duration = sampler.GetAnimation("sample").Length;
            int segments = (int)Math.Clamp(Math.Ceiling(duration * 60), 1, 1800);
            var tracks = binding.Visuals.Select(visual =>
            {
                var anchor = visual.Anchor!;
                return (Visual: visual, Anchor: anchor, Node: clone.GetNode<Node3D>(binding.Model.GetPathTo(anchor.Node)),
                    Edge: visual.Weapon.Trail.Edge(), Points: new (Vector3 Base, Vector3 Tip)[segments + 1]);
            }).ToArray();
            var budget = Stopwatch.StartNew();
            for (int i = 0; i <= segments; i++)
            {
                ct.ThrowIfCancellationRequested();
                sampler.Seek(duration * i / segments, true, true);
                foreach (var attachment in attachments) attachment.OnSkeletonUpdate();
                foreach (var track in tracks)
                {
                    var pose = WeaponPose(track.Anchor, track.Node, track.Visual.Weapon);
                    var point = (Base: _stage.ToLocal(pose * track.Edge.Base) + binding.SamplingOffset,
                        Tip: _stage.ToLocal(pose * track.Edge.Tip) + binding.SamplingOffset);
                    if (!point.Base.IsFinite() || !point.Tip.IsFinite()) throw new InvalidDataException("武器轨迹包含无效坐标。");
                    track.Points[i] = point;
                }
                if (i < segments && budget.ElapsedMilliseconds >= 4)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    budget.Restart();
                }
            }

            foreach (var track in tracks)
                track.Visual.SpeedCeiling = BuildWeaponRibbon(track.Visual.Trail, track.Points, duration);
        }
        finally { if (GodotObject.IsInstanceValid(clone)) clone.Free(); }
    }

    private void UpdateWeaponHeatScale()
    {
        if (_weaponHeatmapToggle == null) return;
        var visuals = _weaponBindings.SelectMany(binding => binding.Visuals).ToArray();
        float ceiling = Math.Max(MinimumHeatSpeed, visuals.Select(visual => visual.SpeedCeiling).DefaultIfEmpty().Max());
        foreach (var visual in visuals) visual.Material.SetShaderParameter("speed_ceiling", ceiling);
        _weaponHeatmapToggle.Visible = _weaponToggle.Visible;
        _weaponHeatmapToggle.SetPressedNoSignal(_weaponHeatmap);
        _weaponHeatLegend.Visible = _weaponHeatmap && _showWeapons && visuals.Length > 0;
        _weaponHeatLimit.Text = visuals.Any(visual => visual.Trail.GetSurfaceCount() > 0)
            ? $"快 · {ceiling:0.##} m/s" : _weaponSampling != null ? "计算中…" : "暂无轨迹";
    }

    private static float BuildWeaponRibbon(ImmediateMesh mesh, (Vector3 Base, Vector3 Tip)[] points, double duration)
    {
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        int last = points.Length - 1;
        var speed = new float[points.Length];
        for (int i = 0; i <= last; i++)
        {
            int previous = Math.Max(0, i - 1), next = Math.Min(last, i + 1);
            double seconds = duration * (next - previous) / last;
            float distance = points[previous].Tip.DistanceTo(points[i].Tip) + points[i].Tip.DistanceTo(points[next].Tip);
            speed[i] = (float)(distance / seconds);
        }
        void Vertex(Vector3 point, int sample, float edge)
        {
            mesh.SurfaceSetUV(new((float)(duration * sample / last), speed[sample]));
            mesh.SurfaceSetUV2(new(edge, 0));
            mesh.SurfaceAddVertex(point);
        }
        for (int i = 1; i < points.Length; i++)
        {
            Vertex(points[i - 1].Tip, i - 1, 1);
            Vertex(points[i - 1].Base, i - 1, 0);
            Vertex(points[i].Base, i, 0);
            Vertex(points[i - 1].Tip, i - 1, 1);
            Vertex(points[i].Base, i, 0);
            Vertex(points[i].Tip, i, 1);
        }
        mesh.SurfaceEnd();
        // Keep isolated spikes from compressing the useful color range.
        Array.Sort(speed);
        return speed[(int)Math.Ceiling(last * .95)];
    }

    public override void _EnterTree()
    {
        // Moving the preview between dock panels can interrupt an in-progress sample.
        if (_weaponSampling?.IsCancellationRequested == true)
            Callable.From(() => { if (IsInsideTree()) RefreshWeaponPreview(); }).CallDeferred();
    }

    public override void _ExitTree() => _weaponSampling?.Cancel();
}
