using Godot;
using Animanager.Core.Preview;

namespace Animanager.UI.Components;

/// <summary>Preview-only geometry, deliberately outside the model subtree exported to GLB.</summary>
public partial class BoneOverlay : Node3D
{
    private readonly ImmediateMesh _bones=new(),_heading=new();
    private readonly StandardMaterial3D _lines=Material(Colors.White,true);
    private readonly Dictionary<BoneId,(MeshInstance3D Marker,Label3D Label)> _selected=[];
    private readonly List<PathLayer> _paths=[];
    private PathLayer? _rootMotion;
    private RootMotionTrack? _motion;
    private BoneId? _primary;
    private BoneTrajectory? _primaryTrail;
    private float _radius=.018f;
    private sealed record PathLayer(Node3D Root,ImmediateMesh Mesh,MeshInstance3D Current,Label3D Label,DisplayedBoneTrail? Data);
    public bool ShowSkeleton { get; set; }
    public bool Xray { get; private set; }=true;
    public Vector3? SelectedWorldPosition=>_primary is {} id&&_selected.TryGetValue(id,out var selected)?selected.Marker.GlobalPosition:null;
    public Vector3? CurrentWorldPosition=>_paths.FirstOrDefault(p=>p.Data?.Trajectory==_primaryTrail)?.Current.GlobalPosition;
    public Vector3? RootMotionWorldPosition=>_rootMotion?.Current.GlobalPosition;
    public int SelectedMarkerCount=>_selected.Count;
    public int TrailCount=>_paths.Count;
    public int RootMotionHeadingCount { get; private set; }
    public int SegmentCount { get; private set; }
    public Vector3? TrailCurrentWorldPosition(string id)=>_paths.FirstOrDefault(p=>p.Data?.Id==id)?.Current.GlobalPosition;

    public override void _Ready()
    {
        AddChild(new MeshInstance3D{Name="SkeletonLines",Mesh=_bones,MaterialOverride=_lines,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        AddChild(new MeshInstance3D{Name="RootMotionHeading",Mesh=_heading,MaterialOverride=_lines,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
    }
    private static StandardMaterial3D Material(Color color,bool vertex=false)=>new()
    {ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,AlbedoColor=color,VertexColorUseAsAlbedo=vertex,NoDepthTest=true};
    private MeshInstance3D Marker(Node parent,Color color,float size=1)
    {
        var material=Material(color);material.NoDepthTest=Xray;
        var marker=new MeshInstance3D{Mesh=new SphereMesh{Radius=1,Height=2,RadialSegments=12,Rings=6},MaterialOverride=material,
            Scale=Vector3.One*_radius*size,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};
        parent.AddChild(marker);return marker;
    }
    private Label3D Label(Node parent,string text)
    {
        var label=new Label3D{Text=text,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled,NoDepthTest=Xray,
            FontSize=32,OutlineSize=6,PixelSize=_radius*.2f};
        parent.AddChild(label);return label;
    }
    public void SetXray(bool enabled)
    {
        Xray=enabled;_lines.NoDepthTest=enabled;
        foreach(var node in Preview3DViewport.Descendants(this))
        {
            if(node is MeshInstance3D {MaterialOverride:BaseMaterial3D material})material.NoDepthTest=enabled;
            if(node is Label3D label)label.NoDepthTest=enabled;
        }
    }
    public void SetScaleFromSkeletons(IReadOnlyList<SkeletonEntry> skeletons)
    {
        float span=0;
        foreach(var entry in skeletons)
        {
            Aabb? bounds=null;
            for(int i=0;i<entry.Node.GetBoneCount();i++)
            {var p=(entry.Node.GlobalTransform*entry.Node.GetBoneGlobalPose(i)).Origin;bounds=bounds?.Expand(p)??new Aabb(p,Vector3.Zero);}
            if(bounds.HasValue)span=Math.Max(span,bounds.Value.Size.Length());
        }
        _radius=Math.Max(.001f,span*.008f);
    }
    private static void Remove(Node node){node.GetParent()?.RemoveChild(node);node.Free();}
    public void Clear()
    {
        _bones.ClearSurfaces();_heading.ClearSurfaces();
        foreach(var selected in _selected.Values){Remove(selected.Marker);Remove(selected.Label);}
        _selected.Clear();_primary=null;_primaryTrail=null;SetTrajectories([],null);
    }
    public void SetTrajectories(IReadOnlyList<DisplayedBoneTrail> trajectories,RootMotionTrack? motion)
    {
        foreach(var path in _paths){Remove(path.Root);path.Mesh.Dispose();}
        _paths.Clear();if(_rootMotion!=null){Remove(_rootMotion.Root);_rootMotion.Mesh.Dispose();_rootMotion=null;}
        _motion=motion;SegmentCount=0;
        foreach(var data in trajectories)
            _paths.Add(CreatePath(data.Trajectory.Samples.Select(s=>s.Position).ToArray(),data.Color,data.Label,data));
        if(motion!=null)_rootMotion=CreatePath(motion.Samples.Select(s=>s.WorldPosition).ToArray(),new Color("ffb454"),"ROOT MOTION",null);
        _heading.ClearSurfaces();RootMotionHeadingCount=0;
    }
    private PathLayer CreatePath(Vector3[] samples,Color color,string text,DisplayedBoneTrail? data)
    {
        var root=new Node3D{Name=data==null?"RootMotion":"BoneTrajectory"};AddChild(root);
        var mesh=new ImmediateMesh();root.AddChild(new MeshInstance3D{Mesh=mesh,MaterialOverride=_lines,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        if(samples.Length>1)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            for(int i=1;i<samples.Length;i++){Line(mesh,samples[i-1],samples[i],color);SegmentCount++;}
            mesh.SurfaceEnd();
        }
        Marker(root,new Color("58df98")).Position=samples[0];Marker(root,new Color("ff6f82")).Position=samples[^1];
        Label(root,"START").Position=samples[0]+Vector3.Up*_radius*3;
        Label(root,"END").Position=samples[^1]-Vector3.Up*_radius*3;
        var label=Label(root,text);label.Modulate=color;
        return new(root,mesh,Marker(root,Colors.White,.7f),label,data);
    }
    public void UpdatePose(BoneInspection inspection,double time)
    {
        _primary=inspection.Selected;_primaryTrail=inspection.Trajectory;
        var selection=inspection.SelectedBones.ToHashSet();
        foreach(var id in _selected.Keys.Where(id=>!selection.Contains(id)).ToArray())
        {var pair=_selected[id];Remove(pair.Marker);Remove(pair.Label);_selected.Remove(id);}
        foreach(var id in selection)
        {
            if(!_selected.TryGetValue(id,out var pair))
            {pair=(Marker(this,inspection.ColorFor(id)),Label(this,inspection.Name(id)));_selected[id]=pair;}
            pair.Marker.GlobalPosition=inspection.WorldPose(id).Origin;
            pair.Label.GlobalPosition=pair.Marker.GlobalPosition+Vector3.Up*_radius*3;
        }
        _bones.ClearSurfaces();bool begun=false;
        for(int s=0;s<inspection.Skeletons.Count;s++)
        {
            var skeleton=inspection.Skeletons[s].Node;
            for(int b=0;b<skeleton.GetBoneCount();b++)
            {
                var id=new BoneId(s,b);bool selected=selection.Contains(id);int parent=skeleton.GetBoneParent(b);
                if(parent<0||(!ShowSkeleton&&!selected))continue;
                if(!begun){_bones.SurfaceBegin(Mesh.PrimitiveType.Lines);begun=true;}
                Line(_bones,inspection.WorldPose(new(s,parent)).Origin,inspection.WorldPose(id).Origin,selected?inspection.ColorFor(id):new Color("81939f"));
            }
        }
        if(begun)_bones.SurfaceEnd();
        foreach(var path in _paths)
        {
            var data=path.Data!.Trajectory;
            path.Root.GlobalTransform=data.Reference is {} reference?inspection.WorldPose(reference):Transform3D.Identity;
            // Exact live position, including between samples; each pin keeps its own frame.
            path.Current.GlobalPosition=inspection.WorldPose(data.Bone).Origin;
            path.Label.GlobalPosition=path.Current.GlobalPosition-Vector3.Up*_radius*4;
        }
        _heading.ClearSurfaces();RootMotionHeadingCount=0;
        if(_rootMotion!=null&&_motion!=null)
        {
            var pose=inspection.WorldPose(_motion.Source);_rootMotion.Current.GlobalPosition=pose.Origin;
            _rootMotion.Label.GlobalPosition=pose.Origin+Vector3.Up*_radius*7;
            _heading.SurfaceBegin(Mesh.PrimitiveType.Lines);
            // Keep heading arrows legible even for stationary, rotation-only clips.
            int stride=Math.Max(1,(_motion.Samples.Count-1)/12);
            for(int i=0;i<_motion.Samples.Count;i+=stride)
            {var sample=_motion.Samples[i];Heading(sample.WorldPosition,sample.WorldRotation,new Color("ffb454"));}
            if(pose.Basis.Determinant()>1e-10f)Heading(pose.Origin,pose.Basis.Orthonormalized().GetRotationQuaternion(),Colors.White);
            _heading.SurfaceEnd();
        }
    }
    private void Heading(Vector3 position,Quaternion rotation,Color color)
    {
        var forward=rotation*Vector3.Forward*_radius*10;var side=rotation*Vector3.Right*_radius*2;
        var tip=position+forward;Line(_heading,position,tip,color);
        Line(_heading,tip,tip-forward*.25f+side,color);Line(_heading,tip,tip-forward*.25f-side,color);RootMotionHeadingCount++;
    }
    private static void Line(ImmediateMesh mesh,Vector3 a,Vector3 b,Color color)
    {mesh.SurfaceSetColor(color);mesh.SurfaceAddVertex(a);mesh.SurfaceSetColor(color);mesh.SurfaceAddVertex(b);}
}

