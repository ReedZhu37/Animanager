using Animanager.Core.Motion;
using System.Numerics;

namespace Animanager.CueImporter;

internal static class MotionWriter
{
    public static bool Matches(ResolvedRig rig)
    {
        var standard=rig.Profile.bones.Select(b=>b.name).ToHashSet();var extra=rig.Bones.Where(b=>!standard.Contains(b.name)).ToArray();
        return rig.Profile.id=="ue5-mannequin"&&!rig.Substituted&&rig.Bones.Length==162&&standard.IsSubsetOf(rig.Bones.Select(b=>b.name))&&extra.Length==1&&extra[0].name=="Nodachi_Weapon_R"&&rig.Bones[extra[0].parent].name=="hand_r";
    }
    private static MotionBone Bone(Bone b)=>new(){Name=b.name,Parent=b.parent,Position=[b.p[0]*.01f,-b.p[1]*.01f,b.p[2]*.01f],Rotation=Rotation(b.r),Scale=b.s};
    private static float[] Rotation(float[] v){var q=Quaternion.Normalize(new Quaternion(-v[0],v[1],-v[2],v[3]));return [q.X,q.Y,q.Z,q.W];}
    public static byte[] Write(string output,ResolvedRig rig,Clip clip,Result row,byte[] source,byte[] skeleton)
    {
        if(!Matches(rig))throw new NotSupportedException("Unverified prop mapping.");
        var sourceBones=rig.Bones.Select(Bone).ToArray();var standardBones=rig.Profile.bones.Select(Bone).ToArray();
        double fps=Math.Max(1,(clip.Frames-1)/(double)clip.Duration);if(Math.Abs(fps-Math.Round(fps))<.001)fps=Math.Round(fps);
        MotionChannel[] Tracks(MotionBone[] bones,IEnumerable<BoneTrack> selected)
        {
            var indices=bones.Select((b,i)=>(b,i)).ToDictionary(x=>x.b.Name,x=>x.i);var result=new List<MotionChannel>();
            foreach(var t in selected)
            {
                void Add(string property,float[][] values)
                {
                    if(values.Length==0)return;
                    if(values.Length!=1&&values.Length!=clip.Frames)throw new InvalidDataException("Inconsistent source keys.");
                    var times=values.Length==1?new[]{0f}:Enumerable.Range(0,values.Length).Select(i=>clip.Duration*i/(values.Length-1)).ToArray();
                    result.Add(new(indices[t.Name],property,times,values));
                }
                Add("translation",t.Positions.Select(p=>new[]{p[0]*.01f,-p[1]*.01f,p[2]*.01f}).ToArray());Add("rotation",t.Rotations.Select(Rotation).ToArray());Add("scale",t.Scales);
            }
            return result.ToArray();
        }
        var m=new MotionManifest{Name=row.name,Clip=clip.Name,Duration=clip.Duration,Frames=clip.Frames,SourceFps=fps,Provenance=new(){["source"]=row.source,["sourceSha256"]=row.sha256,["skeleton"]=row.skeletonSource,["skeletonSha256"]=row.skeletonSha256,["engine"]=row.engine,["backend"]="CUE4Parse",["revision"]=Program.Revision,["patch"]="ue50-tagged-raw-track-v1",["normalizer"]="nodachi-motion-v1",["encoding"]=clip.Encoding,["additiveType"]=row.additive,["rootMotion"]=row.rootMotion.ToString(),["forceRootLock"]=row.forceRootLock.ToString(),["notifiesNotEvaluated"]=row.notifies.ToString(),["sourceCompleteness"]="archived-editor-inputs"}};
        void Put(string key,byte[] bytes,string extension)
        {
            string hash=MotionJson.Hash(bytes),relative="objects/"+hash+extension,path=Path.Combine(output,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(!File.Exists(path))File.WriteAllBytes(path,bytes);
            else if(MotionJson.Hash(File.ReadAllBytes(path))!=hash)throw new IOException("Existing object is corrupt.");
            m.Objects.Add(key,new(){Path=relative,Sha256=hash,Bytes=bytes.Length});
        }
        Put("preset",rig.Mesh,".glb");
        Put("rig",MotionJson.Bytes(new MotionRig{Id="ue5-mannequin-v1",Bones=standardBones}),".json");
        Put("sourceRig",MotionJson.Bytes(new MotionRig{Id="nodachi-source",Bones=sourceBones}),".json");
        Put("source",MotionGlb.Write(sourceBones,Tracks(sourceBones,clip.Tracks),clip.Name,fps,true),".glb");
        var names=standardBones.Select(b=>b.Name).ToHashSet();
        Put("body",MotionGlb.Write(standardBones,Tracks(standardBones,clip.Tracks.Where(t=>names.Contains(t.Name))),clip.Name,fps,true),".glb");
        var prop=Bone(rig.Bones.Single(b=>b.name=="Nodachi_Weapon_R"));prop.Parent=-1;
        var propTracks=Tracks([prop],clip.Tracks.Where(t=>t.Name==prop.Name));
        if(propTracks.Length==0)propTracks=[new(0,"translation",[0],[prop.Position]),new(0,"rotation",[0],[prop.Rotation]),new(0,"scale",[0],[prop.Scale])];
        Put("prop",MotionGlb.Write([prop],propTracks,clip.Name,fps,false),".glb");
        Put("sourceAsset",source,".uasset");Put("sourceSkeleton",skeleton,".uasset");
        bool present=clip.Tracks.Any(t=>t.Name==prop.Name),varying=propTracks.Any(t=>t.Values.Skip(1).Any(v=>!v.SequenceEqual(t.Values[0])));
        m.Props.Add(new(){State=!present?"rest-fallback":varying?"animated":"constant",Contacts=[new(){Start=0,End=m.Duration}]});
        return MotionJson.Bytes(m);
    }
}
