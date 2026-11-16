using Godot;
using Animanager.UI.Components;

namespace Animanager.Core.Motion;

/// <summary>One AnimationPlayer drives the fixed body and independent prop. No retarget or contact solver.</summary>
public sealed class MotionPlayback
{
    public string ManifestPath {get;}
    public MotionManifest Manifest {get;}
    public MotionPresentation Presentation {get;}
    public Node3D Root {get;}
    public Skeleton3D Skeleton {get;}
    public BoneAttachment3D Anchor {get;}
    public Node3D Prop {get;}
    public Node3D Grip {get;}
    public AnimationPlayer Player {get;}
    public string Contact=>MotionJson.ContactAt(Presentation.Contacts??Manifest.Props[0].Contacts,Player.CurrentAnimationPosition);
    public string Summary=>$"人体 {Skeleton.GetBoneCount()} 骨骼 · 独立武器 1 · 接触 {Contact}（仅记录） · 握持{(Presentation.GripConfirmed?"已校准":"待校准")}";
    public MotionPlayback(string path)
    {
        ManifestPath=path;Manifest=MotionJson.Load(path);Presentation=MotionJson.Presentation(path,Manifest);
        var preset=MotionJson.ResolveObject(path,Manifest.Objects["preset"],false);
        using var doc=new GltfDocument();using var state=new GltfState();
        if(doc.AppendFromFile(preset,state,(uint)GltfDocument.ImportFlags.UseNamedSkinBinds)!=Error.Ok)throw new IOException("Cannot load motion character preset.");
        Root=doc.GenerateScene(state) as Node3D??throw new IOException("Invalid character preset scene.");
        try
        {
            Skeleton=Preview3DViewport.Descendants(Root).OfType<Skeleton3D>().Single();
            var rig=MotionJson.Read<MotionRig>(MotionJson.ResolveObject(path,Manifest.Objects["rig"],false));
            if(rig.SchemaVersion!=1||rig.Id!="ue5-mannequin-v1"||rig.Bones.Length!=161||rig.Bones.Any(b=>b.Name=="Nodachi_Weapon_R"))throw new InvalidDataException("Invalid standard UE5 preset rig.");
            foreach(var bone in rig.Bones)
            {
                int index=Skeleton.FindBone(bone.Name);var rest=Transform(bone);
                if(index<0)
                {
                    index=Skeleton.GetBoneCount();Skeleton.AddBone(bone.Name);
                    if(bone.Parent>=0){int parent=Skeleton.FindBone(rig.Bones[bone.Parent].Name);if(parent<0)throw new InvalidDataException("Preset parent missing.");Skeleton.SetBoneParent(index,parent);}
                    Skeleton.SetBoneRest(index,rest);Skeleton.SetBonePose(index,rest);
                }
                else
                {
                    var actual=Skeleton.GetBoneRest(index);
                    if(actual.Origin.DistanceTo(rest.Origin)>.0001f||Math.Abs(actual.Basis.GetRotationQuaternion().Dot(rest.Basis.GetRotationQuaternion()))<.99999f)throw new InvalidDataException("Preset reference pose differs: "+bone.Name);
                    int parent=Skeleton.GetBoneParent(index);string parentName=parent<0?"":Skeleton.GetBoneName(parent).ToString();
                    if(parentName!=(bone.Parent<0?"":rig.Bones[bone.Parent].Name))throw new InvalidDataException("Preset hierarchy differs: "+bone.Name);
                }
            }
            // The Skin resource and every vertex/inverse-bind buffer remain exactly as imported (89 binds).
            if(Skeleton.GetBoneCount()!=161)throw new InvalidDataException("Unexpected bones in preset.");
            Anchor=new BoneAttachment3D{Name="PrimaryWeaponAnchor",BoneName=Manifest.Props[0].Anchor,OverridePose=false};Skeleton.AddChild(Anchor);Anchor.Owner=Root;
            var propData=MotionGlb.Read(MotionJson.ResolveObject(path,Manifest.Objects["prop"],false));
            if(propData.Nodes.Length!=1)throw new InvalidDataException("Invalid rigid prop graph.");
            Prop=new Node3D{Name="PrimaryWeaponMotion",Transform=Transform(propData.Nodes[0])};Anchor.AddChild(Prop);Prop.Owner=Root;
            Grip=new Node3D{Name="GripCalibration",Transform=new Transform3D(new Basis(Quat(Presentation.GripRotation)),Vec(Presentation.GripPosition)),Visible=Presentation.Visible};Prop.AddChild(Grip);Grip.Owner=Root;
            AddMarker(Grip,Root);
            Player=new AnimationPlayer{Name="MotionPlayer",RootNode=new NodePath("..")};Root.AddChild(Player);Player.Owner=Root;
            var animation=new Animation{Length=Manifest.Duration,Step=(float)(1/Manifest.SourceFps)};
            var body=MotionGlb.Read(MotionJson.ResolveObject(path,Manifest.Objects["body"],false));
            foreach(var channel in body.Channels)
            {
                string bone=body.Nodes[channel.Node].Name;if(Skeleton.FindBone(bone)<0)throw new InvalidDataException("Body channel targets a nonstandard bone: "+bone);
                AddTrack(animation,channel,new NodePath(Root.GetPathTo(Skeleton)+":"+bone));
            }
            foreach(var channel in propData.Channels)AddTrack(animation,channel,Root.GetPathTo(Prop));
            var library=new AnimationLibrary();library.AddAnimation(Manifest.Clip,animation);Player.AddAnimationLibrary("",library);
        }
        catch{Root.Free();throw;}
    }
    public void Sync()=>Anchor.OnSkeletonUpdate();
    public static Vector3 Vec(float[] v)=>new(v[0],v[1],v[2]);
    public static Quaternion Quat(float[] q)=>new Quaternion(q[0],q[1],q[2],q[3]).Normalized();
    public static Transform3D Transform(MotionBone b)=>new(new Basis(Quat(b.Rotation)).Scaled(Vec(b.Scale)),Vec(b.Position));
    private static void AddTrack(Animation animation,MotionChannel c,NodePath path)
    {
        int track=animation.AddTrack(c.Path switch{"translation"=>Animation.TrackType.Position3D,"rotation"=>Animation.TrackType.Rotation3D,"scale"=>Animation.TrackType.Scale3D,_=>throw new InvalidDataException("Unknown transform channel.")});
        animation.TrackSetPath(track,path);animation.TrackSetInterpolationType(track,Animation.InterpolationType.Linear);
        for(int i=0;i<c.Times.Length;i++)switch(c.Path)
        {
            case "translation":animation.PositionTrackInsertKey(track,c.Times[i],Vec(c.Values[i]));break;
            case "rotation":animation.RotationTrackInsertKey(track,c.Times[i],Quat(c.Values[i]));break;
            case "scale":animation.ScaleTrackInsertKey(track,c.Times[i],Vec(c.Values[i]));break;
        }
    }
    private static void AddMarker(Node3D grip,Node3D owner)
    {
        // An application-owned diagnostic prop, not the pack's mesh or a claim about its actual dimensions.
        void Box(string name,Vector3 size,Vector3 position,string color)
        {
            var node=new MeshInstance3D{Name=name,Mesh=new BoxMesh{Size=size},Position=position,MaterialOverride=new StandardMaterial3D{AlbedoColor=new Color(color),Roughness=.65f}};
            node.SetMeta("animanager_prop_visual",true);grip.AddChild(node);node.Owner=owner;
        }
        Box("Blade",new(.035f,.008f,.8f),new(0,0,.4f),"eab65a");Box("Guard",new(.15f,.018f,.025f),Vector3.Zero,"485766");Box("Handle",new(.025f,.025f,.18f),new(0,0,-.09f),"303a44");
    }
}
