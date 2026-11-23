using Godot;
using Animanager.Core.Preview;
using Animanager.Core.Motion;
using Animanager.Core.Pipeline;

namespace Animanager.UI.Components;

public partial class Preview3DViewport : SubViewportContainer
{
    public SubViewport View { get; private set; } = null!;
    public AnimationPlayer? Player { get; private set; }
    public Node3D? Model { get; private set; }
    public MotionPlayback? Motion {get;private set;}
    public int BoneCount { get; private set; }
    public int MeshCount { get; private set; }
    public string[] Clips { get; private set; } = [];
    public string CurrentClip { get; private set; } = "";
    public BoneInspection Bones { get; private set; } = null!;
    private PoseSnapshot? _baseline;
    public bool Playing => Player?.IsPlaying() == true;
    public double Duration => Player != null && CurrentClip.Length>0 ? Player.GetAnimation(CurrentClip).Length : 0;
    public double Playhead => Player != null && CurrentClip.Length>0 ? Player.CurrentAnimationPosition : 0;
    public double FrameStep => 1.0 / DisplayFps;
    public Core.Retarget.AssetReadiness? SourceReadiness { get; private set; }
    private double _sourceRate = 30;
    public double DisplayFps => Motion?.Manifest.SourceFps ?? _sourceRate;
    public int TotalFrames => Duration > 0 ? Math.Max(1, (int)Math.Ceiling(Duration * DisplayFps - 1e-5) + 1) : 0;
    public int CurrentFrame => TotalFrames == 0 ? 0 : Math.Clamp((int)Math.Round(Playhead * DisplayFps) + 1, 1, TotalFrames);
    private double _speed = 1;
    private Node3D _stage = null!;
    private Camera3D _camera = null!;
    private Vector3 _target=new(0,0.9f,0);
    private float _distance=4.5f, _yaw=0.5f, _pitch=0.18f;
    internal bool ResetCameraOnAnimationChange { get; set; }
    private bool PreserveAnimationCamera => CurrentClip.Length > 0 && !ResetCameraOnAnimationChange;
    private bool _loop=true;
    private bool _clay=true;
    private BoneAttachment3D[] _attachments=[];
    private BoneScreenLabels? _screenLabels;
    public Vector2? ProjectLabel(Vector3 position)
    {
        if (_camera == null || !_camera.IsInsideTree() || _camera.IsPositionBehind(position) || View.Size.X == 0 || View.Size.Y == 0) return null;
        var point = _camera.UnprojectPosition(position) * Size / (Vector2)View.Size;
        return new Rect2(Vector2.Zero, Size).HasPoint(point) ? point : null;
    }

    public override void _Ready()
    {
        Stretch=true; MouseFilter=MouseFilterEnum.Stop;
        CustomMinimumSize=new Vector2(160,120);FocusMode=FocusModeEnum.All;
        SizeFlagsVertical=SizeFlags.ExpandFill;
        View=new SubViewport { OwnWorld3D=true,TransparentBg=false,RenderTargetUpdateMode=SubViewport.UpdateMode.Once,Msaa3D=Viewport.Msaa.Msaa2X };
        AddChild(View);
        _stage=new Node3D();View.AddChild(_stage);
        _stage.AddChild(new WorldEnvironment { Environment=new Godot.Environment {
            BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("22282d"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=new Color("d0d8e1"),AmbientLightEnergy=0.65f,
            TonemapMode=Godot.Environment.ToneMapper.Filmic } });
        _stage.AddChild(new DirectionalLight3D { RotationDegrees=new Vector3(-45,-35,0),LightEnergy=1.2f });
        _stage.AddChild(new DirectionalLight3D { RotationDegrees=new Vector3(-25,140,0),LightColor=new Color("bacbe4"),LightEnergy=0.6f });
        var grid=new ImmediateMesh();grid.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for(int i=-10;i<=10;i++)
        {
            grid.SurfaceAddVertex(new Vector3(i,0,-10));grid.SurfaceAddVertex(new Vector3(i,0,10));
            grid.SurfaceAddVertex(new Vector3(-10,0,i));grid.SurfaceAddVertex(new Vector3(10,0,i));
        }
        grid.SurfaceEnd();
        _grid = new MeshInstance3D { Mesh=grid, MaterialOverride=new StandardMaterial3D {
            ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,AlbedoColor=new Color("3c454c") } };
        _stage.AddChild(_grid);
        _camera=new Camera3D { Fov=38,Near=0.01f,Far=10000 };_stage.AddChild(_camera);
        Bones=new BoneInspection(this,_stage);
        _screenLabels = new BoneScreenLabels { Name = "BoneScreenLabels", Preview = this, MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
        AddChild(_screenLabels); _screenLabels.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildNavigation();
        UpdateCamera();GuiInput+=HandleInput;Resized+=UpdateCamera;VisibilityChanged+=()=>{if(IsVisibleInTree())UpdateCamera();};
        SetProcess(false);
    }
    public void Clear()
    {
        EndAnimationComparison(resume: false);
        ClearWeaponPreview();
        ClearRetarget();
        Bones.Clear();_baseline=null;Motion=null;_attachments=[];_sourceRate=30;SourceReadiness=null;
        if(Model!=null) { _stage.RemoveChild(Model);Model.QueueFree(); }
        Model=null;Player=null;Clips=[];CurrentClip="";BoneCount=0;MeshCount=0;
        SetProcess(false);Redraw();
    }
    public void LoadFile(string path)
    {
        using var source = new PreviewSource(path);
        AttachSource(source);
    }
    public void AttachSource(PreviewSource source, bool notify = true, bool switchingAsset = false)
    {
        AttachModel(source.TakeRoot(), preserveCamera: switchingAsset && PreserveAnimationCamera);
        Motion = source.Motion; _sourceRate = source.FrameRate; Motion?.Sync();
        try { Core.Retarget.RetargetBaker.RejectAdditive(source.Path); }
        catch (NotSupportedException e) { SourceReadiness = new(false, (SourceReadiness?.Errors ?? []).Append(e.Message).ToArray()); }
        Redraw(); if (notify) SourceLoaded?.Invoke();
    }
    internal void AttachModel(Node3D root, Core.Retarget.AssetReadiness? retargetSourceReadiness = null, bool preserveCamera = false)
    {
        bool hadMesh = MeshCount > 0;
        Clear();Model=root;_stage.AddChild(root);_baseline=new PoseSnapshot(root);
        var nodes=Descendants(root).ToList();
        _attachments=nodes.OfType<BoneAttachment3D>().ToArray();
        Player=nodes.OfType<AnimationPlayer>().FirstOrDefault();
        if (Player != null) Player.SpeedScale = (float)_speed;
        SetClay(_clay);
        BoneCount=nodes.OfType<Skeleton3D>().Sum(s=>s.GetBoneCount());MeshCount=nodes.OfType<MeshInstance3D>().Count(mesh=>mesh.Mesh!=null);
        Clips=Player?.GetAnimationList().Where(n=>n!="RESET").ToArray()??[];
        SourceReadiness = retargetSourceReadiness ?? Core.Retarget.AssetReadiness.Inspect(root, Clips.Length > 0);
        Bones.Bind(root,Player,_baseline,MeshCount==0);
        if(Clips.Length>0) SelectClip(Clips[0], frameCamera: false);
        if (retargetSourceReadiness == null && (!preserveCamera || Clips.Length == 0 || hadMesh && MeshCount == 0)) FrameModel();
    }
    public static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach(var child in root.GetChildren()) foreach(var nested in Descendants(child)) yield return nested;
    }
    public void SelectClip(string clip) => SelectClip(clip, ResetCameraOnAnimationChange && CurrentClip != clip);

    private void SelectClip(string clip, bool frameCamera)
    {
        if(Player==null || !Player.HasAnimation(clip)) return;
        if (AssetComparison != null) ComparisonExitRequested?.Invoke();
        Player.Stop();if(Model!=null)_baseline?.Restore(Model);
        CurrentClip=clip;
        Player.GetAnimation(clip).LoopMode=_loop?Animation.LoopModeEnum.Linear:Animation.LoopModeEnum.None;
        Player.Play(clip);Player.Advance(0);Player.Pause();SetProcess(false);Redraw();
        if (frameCamera) FrameModel();
        Bones.ClipChanged();
        RefreshWeaponPreview();
    }
    public void TogglePlay()
    {
        if(Player==null || CurrentClip.Length==0) return;
        if(Playing) Player.Pause(); else { if(Playhead>=Duration) Player.Seek(0,true);Player.Play(CurrentClip); }
        SetProcess(TransportPlaying);Redraw();
    }
    public void Seek(double time)
    {
        if(Player==null||CurrentClip.Length==0)return;
        Player.Pause();
        var animation = Player.GetAnimation(CurrentClip); var loop = animation.LoopMode;
        animation.LoopMode = Animation.LoopModeEnum.None;
        Player.Seek(Math.Clamp(time, 0, Duration), true);
        animation.LoopMode = loop;
        SetProcess(TransportPlaying);Redraw();
    }
    public void SetSpeed(double speed) { _speed = Math.Clamp(speed, .1, 3); if(Player!=null)Player.SpeedScale=(float)_speed; }
    public void SetLoop(bool loop) { _loop=loop;if(Player!=null&&CurrentClip.Length>0)Player.GetAnimation(CurrentClip).LoopMode=loop?Animation.LoopModeEnum.Linear:Animation.LoopModeEnum.None; }
    public void SetClay(bool enabled)
    {
        _clay=enabled;
        if (Model != null) ApplyClay(Model, enabled);
        if (_comparison != null) ApplyClay(_comparison, enabled);
        if (AssetComparison is { } comparison)
            foreach (var entry in comparison.Entries)
                if (entry.Root != Model) ApplyClay(entry.Root, enabled);
        Redraw();
    }
    internal static void ApplyClay(Node3D model, bool enabled)
    {
        foreach (var mesh in Descendants(model).OfType<MeshInstance3D>())
            if (!mesh.HasMeta("animanager_prop_visual"))
                mesh.MaterialOverride = enabled ? new StandardMaterial3D { AlbedoColor = new Color("a9b6c5"), Roughness = .8f } : null;
    }
    public override void _Process(double delta)
    {
        AssetComparison?.Advance(delta * _speed, _loop);
        Redraw();
        if (!TransportPlaying) SetProcess(false);
    }
    public void Redraw()
    {
        if (AssetComparison == null)
        {
            SyncRetargetComparison();
            foreach (var attachment in _attachments) attachment.OnSkeletonUpdate();
        }
        UpdateWeaponPreviewPose();
        _screenLabels?.QueueRedraw();
        if (View != null) View.RenderTargetUpdateMode = IsVisibleInTree() ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
    }
    public void Zoom(float multiplier){_distance=Mathf.Clamp(_distance*multiplier,.05f,5000);UpdateCamera();}
    public void FocusPoint(Vector3 point){_target=point;UpdateCamera();}
    public void FrameModel()
    {
        if (AssetComparison is { } comparison) { FrameComparison(comparison.Bounds); return; }
        Aabb? bounds=Model!=null?CalculateWorldBounds(Model):null;
        if (bounds == null)
        {
            foreach (var entry in Bones.Skeletons)
            {
                var skeleton = entry.Node;
                for (int bone = 0; bone < skeleton.GetBoneCount(); bone++)
                {
                    var point = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone).Origin;
                    if (point.IsFinite()) bounds = bounds?.Expand(point) ?? new Aabb(point, Vector3.Zero);
                }
            }
        }
        if(bounds.HasValue)
        {
            _target=bounds.Value.GetCenter();var size=bounds.Value.Size;
            _distance=Mathf.Max(0.3f,size.Length()*1.7f);
        }
        else {_target=new Vector3(0,0.9f,0);_distance=4.5f;}
        _yaw=0.5f;_pitch=0.16f;_viewName="用户视图";UpdateCamera();
    }
    public static Aabb? CalculateWorldBounds(Node3D root)
    {
        Aabb? bounds=null;
        void Include(Vector3 point)=>bounds=bounds.HasValue?bounds.Value.Expand(point):new Aabb(point,Vector3.Zero);
        foreach(var mesh in Descendants(root).OfType<MeshInstance3D>())
        {
            if(mesh.Mesh==null)continue;
            var skin=mesh.Skin;var skeleton=mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton);
            if(skin==null||skeleton==null)
            {
                var box=mesh.GlobalTransform*mesh.GetAabb();bounds=bounds.HasValue?bounds.Value.Merge(box):box;continue;
            }
            var matrices=new Transform3D[skin.GetBindCount()];
            for(int i=0;i<matrices.Length;i++)
            {
                int bone=skin.GetBindName(i).IsEmpty?skin.GetBindBone(i):skeleton.FindBone(skin.GetBindName(i));
                matrices[i]=bone>=0?skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(bone)*skin.GetBindPose(i):mesh.GlobalTransform;
            }
            for(int surface=0;surface<mesh.Mesh.GetSurfaceCount();surface++)
            {
                var arrays=mesh.Mesh.SurfaceGetArrays(surface);
                var vertices=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                if(arrays[(int)Mesh.ArrayType.Bones].VariantType==Variant.Type.Nil)
                {foreach(var v in vertices)Include(mesh.GlobalTransform*v);continue;}
                var bones=arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();var weights=arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                int influences=vertices.Length==0?0:bones.Length/vertices.Length;
                for(int i=0;i<vertices.Length;i+=Math.Max(1,vertices.Length/20000))
                {
                    var point=Vector3.Zero;float total=0;
                    for(int j=0;j<influences;j++)
                    {int k=i*influences+j;if(weights[k]<=0||bones[k]<0||bones[k]>=matrices.Length)continue;point+=(matrices[bones[k]]*vertices[i])*weights[k];total+=weights[k];}
                    Include(total>0?point/total:mesh.GlobalTransform*vertices[i]);
                }
            }
        }
        return bounds;
    }
    private void UpdateCamera()
    {
        if(_camera==null || !_camera.IsInsideTree())return;
        // Preserve the user's orbit/zoom while fitting the narrower field of view in portrait panels.
        float aspect=Size.Y>0?Math.Max(.1f,Size.X/Size.Y):1;
        _camera.Position=_target+new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch))*(_distance*Math.Max(1,1/aspect));
        _camera.LookAt(_target);
        _camera.Size = _distance * .65f;
        _axisGizmo?.QueueRedraw();
        if (_axisGizmo != null) _axisGizmo.TooltipText = _viewName + (Orthographic ? " · 正交" : " · 透视") + "\n拖动旋转；点击 X / Y / Z 切换轴向视图（Y 向上）";
        Redraw();
    }
    private void HandleInput(InputEvent ev)
    {
        if(ev is InputEventMouseButton button && button.Pressed)
        {
            GrabFocus();
            if(button.ButtonIndex==MouseButton.WheelUp)_distance=Mathf.Max(0.05f,_distance*0.88f);
            if(button.ButtonIndex==MouseButton.WheelDown)_distance=Mathf.Min(5000,_distance/0.88f);
            UpdateCamera();
        }
        if(ev is InputEventMouseMotion motion)
        {
            bool middle = (motion.ButtonMask & MouseButtonMask.Middle) != 0;
            bool left = (motion.ButtonMask & MouseButtonMask.Left) != 0;
            if (middle && motion.CtrlPressed) Navigate("zoom", motion.Relative);
            else if (middle && motion.ShiftPressed || left && motion.ShiftPressed) Navigate("pan", motion.Relative);
            else if (middle || (motion.ButtonMask & MouseButtonMask.Right) != 0) Navigate("orbit", motion.Relative);
        }
        if (ev is InputEventKey key && key.Pressed && !key.Echo && HandleViewKey(key)) AcceptEvent();
    }
}
