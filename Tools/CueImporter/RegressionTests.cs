using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace Animanager.CueImporter;

internal static class RegressionTests
{
    public static int Run(string report)
    {
        var checks=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
        void Reject(Action action,string name){bool rejected=false;try{action();}catch(NotSupportedException){rejected=true;}Check(rejected,name);}
        try
        {
            foreach(var id in new[]{"ue4-mannequin","ue5-mannequin"})
            {
                var profile=JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Baseline",id+".json")))!;
                var mesh=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Baseline",profile.mesh));var meshNames=SkeletonProfiles.MeshBones(mesh);
                var tracks=profile.bones.Where(b=>meshNames.Contains(b.name)).Reverse().Select(b=>new BoneTrack(b.name,[b.p],[b.r],[b.s])).ToArray();
                var clip=new Clip("Shuffled",2,1,"AAT_None",tracks,"regression");
                using var catalog=new AssetCatalog(Path.GetDirectoryName(Path.GetFullPath(report))!);
                var profiles=new SkeletonProfiles();
                var rig=profiles.Resolve(catalog,"/Game/Any/Relocated/"+profile.sourceSkeletonName+"."+profile.sourceSkeletonName,clip,"auto");
                Check(rig.Profile.id==id&&rig.Substituted,"shuffled tracks identify "+id+" without a fixed directory");
                Reject(()=>profiles.Resolve(catalog,"/Game/Unknown/Custom.Custom",clip,"auto"),"unknown missing Skeleton is not inferred solely from names: "+id);
                Reject(()=>profiles.Resolve(catalog,"/Game/"+profile.sourceSkeletonName,clip with{Tracks=tracks.Take(4).ToArray()},"auto"),"partial tracks without a Skeleton are rejected: "+id);
                Reject(()=>profiles.Resolve(catalog,"/Game/"+profile.sourceSkeletonName,clip,id=="ue4-mannequin"?"ue5-mannequin":"ue4-mannequin"),"explicit wrong family is rejected: "+id);
                Bone[] Clone()=>JsonSerializer.Deserialize<Bone[]>(JsonSerializer.Serialize(profile.bones))!;
                var bones=Clone();var names=tracks.Select(t=>t.Name).ToHashSet();
                SkeletonProfiles.ValidateCompatible(profile,bones,meshNames,names);
                Check(true,"source hierarchy and bind pose match: "+id);
                bones[1].p[2]+=10;
                Reject(()=>SkeletonProfiles.ValidateCompatible(profile,bones,meshNames,names),"same bone names with custom proportions are rejected: "+id);
                bones=Clone();bones[2].parent=0;
                Reject(()=>SkeletonProfiles.ValidateCompatible(profile,bones,meshNames,names),"reparented standard bone is rejected: "+id);
                bones=[..Clone(),new Bone{name="extra_socket",parent=1,p=[0,0,3],r=[0,0,0,1],s=[1,1,1]}];
                SkeletonProfiles.ValidateHierarchy(bones);SkeletonProfiles.ValidateCompatible(profile,bones,meshNames,names);
                var extra=new BoneTrack("extra_socket",[[0,0,3],[0,0,9]],[[0,0,0,1]],[[1,1,1]]);
                var bytes=GlbWriter.Convert(rig with{Bones=bones},clip with{Tracks=[extra,..tracks]});
                var json=JsonNode.Parse(Encoding.UTF8.GetString(bytes,20,BitConverter.ToInt32(bytes,12)))!;
                var nodes=json["nodes"]!.AsArray();var joints=json["skins"]![0]!["joints"]!.AsArray();
                Check(joints.Count==bones.Length,"additional source bones are retained in the skin: "+id);
                var channels=json["animations"]![0]!["channels"]!.AsArray();
                var mapped=channels.Where(c=>c!["target"]!["path"]!.GetValue<string>()=="translation").Select(c=>nodes[c!["target"]!["node"]!.GetValue<int>()]!["name"]!.GetValue<string>()).ToArray();
                Check(mapped.SequenceEqual(new[]{"extra_socket"}.Concat(tracks.Select(t=>t.Name))),"animation channels map by bone name, including extra tracks: "+id);
                int inverse=json["skins"]![0]!["inverseBindMatrices"]!.GetValue<int>();
                Check(json["accessors"]![inverse]!["count"]!.GetValue<int>()==bones.Length,"inverse bind matrices cover every appended joint: "+id);
                SkeletonProfiles.ValidateCompatible(profile,Clone(),meshNames,tracks.Take(4).Select(t=>t.Name).ToHashSet());
                Check(true,"partial animation is accepted with a validated full source Skeleton: "+id);
            }
            File.WriteAllText(report,JsonSerializer.Serialize(new{checks,error=""},Program.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{checks,error=ex.ToString()},Program.Json));throw;}
    }
}
