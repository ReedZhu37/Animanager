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
    public double FrameStep => 1.0/(Motion?.Manifest.SourceFps??30);
    private Node3D _stage = null!;
    private Camera3D _camera = null!;
    private Vector3 _target=new(0,0.9f,0);
    private float _distance=4.5f, _yaw=0.5f, _pitch=0.18f;
    private bool _loop=true;
    private bool _clay=true;
    private BoneAttachment3D[] _attachments=[];

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
        _stage.AddChild(new MeshInstance3D { Mesh=grid, MaterialOverride=new StandardMaterial3D {
            ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,AlbedoColor=new Color("3c454c") } });
        _camera=new Camera3D { Fov=38,Near=0.01f,Far=10000 };_stage.AddChild(_camera);
        Bones=new BoneInspection(this,_stage);
        UpdateCamera();GuiInput+=HandleInput;Resized+=Redraw;VisibilityChanged+=()=>{if(IsVisibleInTree())Redraw();};
        SetProcess(false);
    }
    public void Clear()
    {
        Bones.Clear();_baseline=null;Motion=null;_attachments=[];
        if(Model!=null) { _stage.RemoveChild(Model);Model.QueueFree(); }
        Model=null;Player=null;Clips=[];CurrentClip="";BoneCount=0;MeshCount=0;
        SetProcess(false);Redraw();
    }
    public void LoadFile(string path)
    {
        Clear();
        if(MotionJson.IsMotion(path))
        {
            var motion=new MotionPlayback(path);AttachModel(motion.Root);Motion=motion;motion.Sync();Redraw();return;
        }
        AttachModel(SceneAsset.Load(path,out _));
    }
    internal void AttachModel(Node3D root)
    {
        Clear();Model=root;_stage.AddChild(root);_baseline=new PoseSnapshot(root);
        var nodes=Descendants(root).ToList();
        _attachments=nodes.OfType<BoneAttachment3D>().ToArray();
        Player=nodes.OfType<AnimationPlayer>().FirstOrDefault();
        SetClay(_clay);
        BoneCount=nodes.OfType<Skeleton3D>().Sum(s=>s.GetBoneCount());MeshCount=nodes.OfType<MeshInstance3D>().Count();
        Clips=Player?.GetAnimationList().Where(n=>n!="RESET").ToArray()??[];
        Bones.Bind(root,Player,_baseline);
        if(Clips.Length>0) SelectClip(Clips[0]);
        FrameModel();
    }
    public static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach(var child in root.GetChildren()) foreach(var nested in Descendants(child)) yield return nested;
    }
    public void SelectClip(string clip)
    {
        if(Player==null || !Player.HasAnimation(clip)) return;
        Player.Stop();if(Model!=null)_baseline?.Restore(Model);
        CurrentClip=clip;
        Player.GetAnimation(clip).LoopMode=_loop?Animation.LoopModeEnum.Linear:Animation.LoopModeEnum.None;
        Player.Play(clip);Player.Advance(0);Player.Pause();SetProcess(false);Redraw();
        Bones.ClipChanged();
    }
    public void TogglePlay()
    {
        if(Player==null || CurrentClip.Length==0) return;
        if(Playing) Player.Pause(); else { if(Playhead>=Duration) Player.Seek(0,true);Player.Play(CurrentClip); }
        SetProcess(Playing);Redraw();
    }
    public void Seek(double time) { if(Player==null||CurrentClip.Length==0)return;Player.Pause();Player.Seek(time,true);SetProcess(false);Redraw(); }
    public void SetSpeed(double speed) { if(Player!=null)Player.SpeedScale=(float)speed; }
    public void SetLoop(bool loop) { _loop=loop;if(Player!=null&&CurrentClip.Length>0)Player.GetAnimation(CurrentClip).LoopMode=loop?Animation.LoopModeEnum.Linear:Animation.LoopModeEnum.None; }
    public void SetClay(bool enabled)
    {
        _clay=enabled;
        if (Model != null) ApplyClay(Model, enabled);
        Redraw();
    }
    internal static void ApplyClay(Node3D model, bool enabled)
    {
        foreach (var mesh in Descendants(model).OfType<MeshInstance3D>())
            if (!mesh.HasMeta("animanager_prop_visual"))
                mesh.MaterialOverride = enabled ? new StandardMaterial3D { AlbedoColor = new Color("a9b6c5"), Roughness = .8f } : null;
    }
    public override void _Process(double delta) {Redraw();if(!Playing)SetProcess(false);}
    public void Redraw() { foreach(var attachment in _attachments)attachment.OnSkeletonUpdate();if(View!=null)View.RenderTargetUpdateMode=IsVisibleInTree()?SubViewport.UpdateMode.Once:SubViewport.UpdateMode.Disabled; }
    public void Zoom(float multiplier){_distance=Mathf.Clamp(_distance*multiplier,.05f,5000);UpdateCamera();}
    public void FocusPoint(Vector3 point){_target=point;UpdateCamera();}
    public void FrameModel()
    {
        Aabb? bounds=Model!=null?CalculateWorldBounds(Model):null;
        if(bounds.HasValue)
        {
            _target=bounds.Value.GetCenter();var size=bounds.Value.Size;
            _distance=Mathf.Max(0.3f,size.Length()*1.7f);
        }
        else {_target=new Vector3(0,0.9f,0);_distance=4.5f;}
        _yaw=0.5f;_pitch=0.16f;UpdateCamera();
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
        _camera.Position=_target+new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch))*_distance;
        _camera.LookAt(_target);Redraw();
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
            if((motion.ButtonMask&MouseButtonMask.Right)!=0) {_yaw-=motion.Relative.X*0.008f;_pitch=Mathf.Clamp(_pitch+motion.Relative.Y*0.008f,-1.4f,1.4f);}
            if((motion.ButtonMask&MouseButtonMask.Middle)!=0||((motion.ButtonMask&MouseButtonMask.Left)!=0&&motion.ShiftPressed)) _target+=(-_camera.Basis.X*motion.Relative.X+_camera.Basis.Y*motion.Relative.Y)*_distance*0.0015f;
            if(motion.ButtonMask!=0)UpdateCamera();
        }
    }
}
