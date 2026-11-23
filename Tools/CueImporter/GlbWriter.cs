using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;


namespace Animanager.CueImporter;

internal static class GlbWriter
{
    // Baseline keeps Z-up bone-local axes; its parent converts to glTF Y-up.
    // Reflect Y and convert centimetres to metres in the bone-local space.
    public static byte[] Convert(ResolvedRig rig,Clip anim,string rootLockMode="")
    {
        var baseline=rig.Mesh;
        int jsonLength=BitConverter.ToInt32(baseline,12),binOffset=20+jsonLength;
        var gltf=JsonNode.Parse(Encoding.UTF8.GetString(baseline,20,jsonLength))!.AsObject();
        var nodes=gltf["nodes"]!.AsArray();var views=gltf["bufferViews"]!.AsArray();var accessors=gltf["accessors"]!.AsArray();
        using var bin=new MemoryStream();bin.Write(baseline,binOffset+8,BitConverter.ToInt32(baseline,binOffset));
        using var writer=new BinaryWriter(bin,Encoding.UTF8,true);
        int Access(float[][] values,string type,bool time=false)
        {
            while(bin.Length%4!=0)writer.Write((byte)0);var start=bin.Length;
            foreach(var value in values)foreach(var v in value){if(!float.IsFinite(v))throw new InvalidDataException("Non-finite animation key.");writer.Write(v);}
            var view=views.Count;views.Add(new JsonObject{["buffer"]=0,["byteOffset"]=start,["byteLength"]=bin.Length-start});
            var result=accessors.Count;var accessor=new JsonObject{["bufferView"]=view,["componentType"]=5126,["count"]=values.Length,["type"]=type};
            if(time){accessor["min"]=new JsonArray(0f);accessor["max"]=new JsonArray(anim.Duration);}
            accessors.Add(accessor);return result;
        }
        var samplers=new JsonArray();var channels=new JsonArray();var timeAccessors=new Dictionary<int,int>();
        void Track(int node,string path,float[][] keys)
        {
            if(keys.Length==0)return;
            if(keys.Length!=1&&keys.Length!=anim.Frames)throw new NotSupportedException("Unexpected raw key count.");
            if(keys.Length==1)keys=[keys[0],keys[0]];
            if(!timeAccessors.TryGetValue(keys.Length,out int input))
            {input=Access(Enumerable.Range(0,keys.Length).Select(i=>new[]{anim.Duration*i/(keys.Length-1)}).ToArray(),"SCALAR",true);timeAccessors[keys.Length]=input;}
            int sampler=samplers.Count;samplers.Add(new JsonObject{["input"]=input,["output"]=Access(keys,path=="rotation"?"VEC4":"VEC3"),["interpolation"]="LINEAR"});
            channels.Add(new JsonObject{["sampler"]=sampler,["target"]=new JsonObject{["node"]=node,["path"]=path}});
        }
        var skin=gltf["skins"]![0]!;var joints=skin["joints"]!.AsArray();
        var byName=joints.Select(j=>j!.GetValue<int>()).ToDictionary(n=>nodes[n]!["name"]!.GetValue<string>(),n=>n);
        int initialJoints=joints.Count;
        foreach(var bone in rig.Bones)
        {
            if(byName.ContainsKey(bone.name))continue;
            int node=nodes.Count;byName[bone.name]=node;
            nodes.Add(new JsonObject{["name"]=bone.name,["translation"]=JsonSerializer.SerializeToNode(new[]{bone.p[0]*.01f,-bone.p[1]*.01f,bone.p[2]*.01f}),["rotation"]=JsonSerializer.SerializeToNode(new[]{-bone.r[0],bone.r[1],-bone.r[2],bone.r[3]}),["scale"]=JsonSerializer.SerializeToNode(bone.s)});
            if(bone.parent<0)throw new NotSupportedException("Additional skeleton root is unsupported.");
            int parent=byName[rig.Bones[bone.parent].name];
            nodes[parent]!["children"]??=new JsonArray();nodes[parent]!["children"]!.AsArray().Add(node);joints.Add(node);
        }
        if(joints.Count>initialJoints)
        {
            var oldAccessor=accessors[skin["inverseBindMatrices"]!.GetValue<int>()]!;
            var oldView=views[oldAccessor["bufferView"]!.GetValue<int>()]!;
            int offset=(oldView["byteOffset"]?.GetValue<int>()??0)+(oldAccessor["byteOffset"]?.GetValue<int>()??0);
            var raw=bin.ToArray();var matrices=new List<float[]>();
            for(int i=0;i<initialJoints;i++)matrices.Add(Enumerable.Range(0,16).Select(c=>BitConverter.ToSingle(raw,offset+(i*16+c)*4)).ToArray());
            var parents=Enumerable.Repeat(-1,nodes.Count).ToArray();
            for(int n=0;n<nodes.Count;n++)foreach(var child in nodes[n]?["children"]?.AsArray()??new JsonArray())parents[child!.GetValue<int>()]=n;
            float[] Values(int n,string key,float[] defaults)=>nodes[n]?[key]?.AsArray().Select(v=>v!.GetValue<float>()).ToArray()??defaults;
            Matrix4x4 Matrix(float[] m)=>new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
            Matrix4x4 Global(int n)
            {
                Matrix4x4 local;
                if(nodes[n]?["matrix"]!=null)local=Matrix(Values(n,"matrix",[]));
                else {var t=Values(n,"translation",[0,0,0]);var r=Values(n,"rotation",[0,0,0,1]);var s=Values(n,"scale",[1,1,1]);local=Matrix4x4.CreateScale(s[0],s[1],s[2])*Matrix4x4.CreateFromQuaternion(new(r[0],r[1],r[2],r[3]))*Matrix4x4.CreateTranslation(t[0],t[1],t[2]);}
                return parents[n]<0?local:local*Global(parents[n]);
            }
            // Existing inverse binds encode mesh space; retain them and infer that same space for appended joints.
            var meshSpace=Matrix(matrices[0])*Global(joints[0]!.GetValue<int>());
            for(int i=initialJoints;i<joints.Count;i++)
            {
                if(!Matrix4x4.Invert(Global(joints[i]!.GetValue<int>()),out var inverse))throw new InvalidDataException("Singular bone bind matrix.");
                var m=meshSpace*inverse;
                matrices.Add([m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44]);
            }
            skin["inverseBindMatrices"]=Access(matrices.ToArray(),"MAT4");
        }
        foreach(var raw in anim.Tracks)
        {
            int node=byName[raw.Name];
            Track(node,"translation",raw.Positions.Select(v=>new[]{v[0]*.01f,-v[1]*.01f,v[2]*.01f}).ToArray());
            Track(node,"rotation",raw.Rotations.Select(q=>{var v=Quaternion.Normalize(new Quaternion(-q[0],q[1],-q[2],q[3]));return new[]{v.X,v.Y,v.Z,v.W};}).ToArray());
            Track(node,"scale",raw.Scales);
        }
        gltf["animations"]=new JsonArray(new JsonObject{["name"]=anim.Name,["samplers"]=samplers,["channels"]=channels});
        gltf["extras"]=new JsonObject{["animanagerSourceFps"]=Math.Max(1,(anim.Frames-1)/anim.Duration),["animanagerBackend"]="CUE4Parse",["sourceAdditiveType"]=anim.AdditiveType,["poseSemantics"]="raw-source-pose",["skeletonProfile"]=rig.Profile.id,["sourceEncoding"]=anim.Encoding,["dependencySubstituted"]=rig.Substituted};
        if(rootLockMode.Length>0)gltf["extras"]!["sourceForceRootLockMode"]=rootLockMode;
        gltf["buffers"]![0]!["byteLength"]=bin.Length;
        byte[] json=Encoding.UTF8.GetBytes(gltf.ToJsonString());int padded=(json.Length+3)&~3;
        using var resultStream=new MemoryStream();using var output=new BinaryWriter(resultStream);
        output.Write(0x46546c67);output.Write(2);output.Write((int)(12+8+padded+8+bin.Length));output.Write(padded);output.Write(0x4e4f534a);output.Write(json);for(int i=json.Length;i<padded;i++)output.Write((byte)32);
        output.Write((int)bin.Length);output.Write(0x004e4942);output.Write(bin.ToArray());return resultStream.ToArray();
    }
}
