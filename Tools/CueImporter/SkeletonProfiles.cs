using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace Animanager.CueImporter;

internal sealed record ResolvedRig(Profile Profile,Bone[] Bones,byte[] Mesh,string Dependency,string Hash,bool Substituted)
{
    public Clip BindTrackNames(Clip clip)
    {
        var names = Bones.ToDictionary(b => b.name, StringComparer.OrdinalIgnoreCase);
        return clip with { Tracks = clip.Tracks.Select(track => track with { Name = names[track.Name].name }).ToArray() };
    }
}
internal sealed class SkeletonProfiles
{
    private readonly Profile[] _profiles;
    private readonly Dictionary<string,byte[]> _meshes=[];
    private readonly Dictionary<string,(Bone[] bones,string hash)> _dependencies=[];
    public SkeletonProfiles()
    {
        _profiles=new[]{"ue4-mannequin","ue5-mannequin"}.Select(id=>JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Baseline",id+".json")))!).ToArray();
        foreach(var p in _profiles)_meshes[p.id]=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Baseline",p.mesh));
    }
    public ResolvedRig Resolve(AssetCatalog catalog,string reference,Clip clip,string selection)
    {
        var dependency=catalog.Resolve(reference);Bone[]? source=null;string hash="";
        if(dependency!=null)
        {
            if(!_dependencies.TryGetValue(dependency.Relative,out var cached))
            {
                var package=AssetCatalog.Load(dependency,out var bytes);
                var index=Array.FindIndex(package.ExportMap,e=>e.ClassIndex.Name=="Skeleton");
                if(index<0)throw new InvalidDataException("Skeleton dependency does not contain a Skeleton: "+dependency.Relative);
                var skeleton=(USkeleton)package.ExportsLazy[index].Value;
                var info=skeleton.ReferenceSkeleton.FinalRefBoneInfo;var poses=skeleton.ReferenceSkeleton.FinalRefBonePose;
                cached=(info.Select((b,i)=>new Bone{name=b.Name.Text,parent=b.ParentIndex,p=[poses[i].Translation.X,poses[i].Translation.Y,poses[i].Translation.Z],r=[poses[i].Rotation.X,poses[i].Rotation.Y,poses[i].Rotation.Z,poses[i].Rotation.W],s=[poses[i].Scale3D.X,poses[i].Scale3D.Y,poses[i].Scale3D.Z]}).ToArray(),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                ValidateHierarchy(cached.Item1);_dependencies[dependency.Relative]=cached;
            }
            (source,hash)=cached;
        }
        var candidates=new List<Profile>();var reasons=new List<string>();
        foreach(var profile in _profiles.Where(p=>selection=="auto"||p.id==selection))
        {
            var meshNames=MeshBones(_meshes[profile.id]);
            var tracks=clip.Tracks.Select(t=>t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(source==null)
            {
                // Missing dependencies are an explicit substitution, never a claim that a partial rig matches.
                if(reference.Split('.')[0].Split('/').Last()==profile.sourceSkeletonName&&(tracks.SetEquals(meshNames)||tracks.SetEquals(profile.bones.Select(b=>b.name))))candidates.Add(profile);
            }
            else
            {
                try{ValidateCompatible(profile,source,meshNames,tracks);candidates.Add(profile);}
                catch(NotSupportedException e){reasons.Add(profile.id+": "+e.Message);}
            }
        }
        if(candidates.Count!=1)throw new NotSupportedException("Cannot identify a compatible standard skeleton. Include the referenced Skeleton asset; custom proportions require retargeting. "+string.Join("; ",reasons));
        var selected=candidates[0];
        var bones=source==null?selected.bones:CanonicalizeStandardNames(selected,source);
        return new(selected,bones,_meshes[selected.id],dependency?.Relative??"bundled-standard-substitution",hash,source==null);
    }
    internal static Bone[] CanonicalizeStandardNames(Profile profile, Bone[] source)
    {
        var standardNames = profile.bones.ToDictionary(b => b.name, b => b.name, StringComparer.OrdinalIgnoreCase);
        return source.Select(b => standardNames.TryGetValue(b.name, out var name) && name != b.name
            ? new Bone { name = name, parent = b.parent, p = b.p, r = b.r, s = b.s }
            : b).ToArray();
    }
    internal static HashSet<string> MeshBones(byte[] mesh)
    {
        var json=JsonNode.Parse(Encoding.UTF8.GetString(mesh,20,BitConverter.ToInt32(mesh,12)))!;
        return json["skins"]![0]!["joints"]!.AsArray().Select(j=>json["nodes"]![j!.GetValue<int>()]!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }
    internal static void ValidateHierarchy(Bone[] bones)
    {
        if(bones.Length==0||bones.Length>1024||bones.Select(b=>b.name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=bones.Length)throw new InvalidDataException("Invalid skeleton bone count or duplicate names.");
        for(int i=0;i<bones.Length;i++)
        {
            var b=bones[i];
            if(b.parent>=i||b.parent< -1||(i>0&&b.parent<0)||b.p.Length!=3||b.r.Length!=4||b.s.Length!=3||b.p.Concat(b.r).Concat(b.s).Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid skeleton hierarchy or reference pose.");
            if(b.r.Sum(v=>v*v)<.0001f||b.s.Any(v=>Math.Abs(v)<.00001f))throw new InvalidDataException("Singular skeleton reference pose.");
        }
    }
    internal static void ValidateCompatible(Profile profile,Bone[] source,HashSet<string> required,HashSet<string> tracks)
    {
        // Unreal FName comparisons ignore case. Keep the preview's canonical joint names and bind pose checks.
        var sourceNames=source.Select(b=>b.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standard=profile.bones.ToDictionary(b=>b.name,StringComparer.OrdinalIgnoreCase);
        if(!sourceNames.IsSupersetOf(required)||!sourceNames.IsSupersetOf(tracks))throw new NotSupportedException("Required preview bones or animation bones are missing.");
        foreach(var b in source.Where(b=>standard.ContainsKey(b.name)))
        {
            var expected=standard[b.name];
            string Parent(Bone bone,Bone[] bones)=>bone.parent<0?"":bones[bone.parent].name;
            if(!string.Equals(Parent(b,source),Parent(expected,profile.bones),StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Different parent for "+b.name);
            var p=new Vector3(b.p[0],b.p[1],b.p[2]);var e=new Vector3(expected.p[0],expected.p[1],expected.p[2]);
            var q=Quaternion.Normalize(new(b.r[0],b.r[1],b.r[2],b.r[3]));var eq=Quaternion.Normalize(new(expected.r[0],expected.r[1],expected.r[2],expected.r[3]));
            if(Vector3.Distance(p,e)>.01f||Math.Abs(Quaternion.Dot(q,eq))<.999999f||b.s.Zip(expected.s).Any(v=>Math.Abs(v.First-v.Second)>.0001f))throw new NotSupportedException("Different bind pose for "+b.name);
        }
    }
}
public sealed class Profile {public string id{get;set;}="";public string mesh{get;set;}="";public string sourceSkeletonName{get;set;}="";public Bone[] bones{get;set;}=[];}
public sealed class Bone {public string name{get;set;}="";public int parent{get;set;}public float[] p{get;set;}=[];public float[] r{get;set;}=[];public float[] s{get;set;}=[];}
