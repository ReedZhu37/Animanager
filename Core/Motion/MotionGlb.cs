using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Animanager.Core.Motion;

public sealed record MotionChannel(int Node,string Path,float[] Times,float[][] Values);
public sealed class MotionGlb
{
    public MotionBone[] Nodes {get;init;}=[];
    public MotionChannel[] Channels {get;init;}=[];
    public string Name {get;init;}="";
    private Dictionary<int,MotionChannel[]>? _byNode;
    public Matrix4x4 LocalAt(int node,double time)
    {
        _byNode??=Channels.GroupBy(c=>c.Node).ToDictionary(g=>g.Key,g=>g.ToArray());
        var b=Nodes[node];var p=b.Position;var r=b.Rotation;var s=b.Scale;
        if(_byNode.TryGetValue(node,out var tracks))foreach(var c in tracks){var v=ValuesAt(c,time);switch(c.Path){case "translation":p=v;break;case "rotation":r=v;break;case "scale":s=v;break;}}
        return Matrix4x4.CreateScale(s[0],s[1],s[2])*Matrix4x4.CreateFromQuaternion(QuaternionOf(r))*Matrix4x4.CreateTranslation(p[0],p[1],p[2]);
    }
    public Matrix4x4 WorldAt(int node,double time)
    {
        var m=LocalAt(node,time);int parent=Nodes[node].Parent,count=0;
        while(parent>=0){if(++count>Nodes.Length)throw new InvalidDataException("Cyclic hierarchy.");m*=LocalAt(parent,time);parent=Nodes[parent].Parent;}return m;
    }
    public Dictionary<string,int> ByName=>Nodes.Select((b,i)=>(b,i)).ToDictionary(x=>x.b.Name,x=>x.i,StringComparer.Ordinal);
    public static Quaternion QuaternionOf(float[] v)=>Quaternion.Normalize(new(v[0],v[1],v[2],v[3]));
    public static float[] ValuesAt(MotionChannel c,double time)
    {
        int hi=Array.BinarySearch(c.Times,(float)time);if(hi>=0)return c.Values[hi];hi=~hi;
        if(hi==0)return c.Values[0];if(hi==c.Times.Length)return c.Values[^1];
        float t=(float)((time-c.Times[hi-1])/(c.Times[hi]-c.Times[hi-1]));var a=c.Values[hi-1];var b=c.Values[hi];
        if(c.Path=="rotation"){var q=Quaternion.Normalize(Quaternion.Slerp(QuaternionOf(a),QuaternionOf(b),t));return [q.X,q.Y,q.Z,q.W];}
        return a.Zip(b,(x,y)=>x+(y-x)*t).ToArray();
    }
    public Matrix4x4[] Evaluate(double time)
    {
        var p=Nodes.Select(n=>n.Position).ToArray();var r=Nodes.Select(n=>n.Rotation).ToArray();var s=Nodes.Select(n=>n.Scale).ToArray();
        foreach(var c in Channels){var v=ValuesAt(c,time);switch(c.Path){case "translation":p[c.Node]=v;break;case "rotation":r[c.Node]=v;break;case "scale":s[c.Node]=v;break;}}
        return Enumerable.Range(0,Nodes.Length).Select(i=>Matrix4x4.CreateScale(s[i][0],s[i][1],s[i][2])*Matrix4x4.CreateFromQuaternion(QuaternionOf(r[i]))*Matrix4x4.CreateTranslation(p[i][0],p[i][1],p[i][2])).ToArray();
    }
    public Matrix4x4 World(Matrix4x4[] local,int node)
    {
        var m=local[node];int parent=Nodes[node].Parent,count=0;
        while(parent>=0){if(++count>Nodes.Length)throw new InvalidDataException("Cyclic hierarchy.");m*=local[parent];parent=Nodes[parent].Parent;}return m;
    }
    public static byte[] Write(MotionBone[] bones,MotionChannel[] channels,string name,double fps,bool zUpParent)
    {
        var nodes=new JsonArray();int shift=zUpParent?1:0;
        if(zUpParent)nodes.Add(new JsonObject{["name"]="MotionCoordinateBasis",["rotation"]=new JsonArray(-.7071067811865476f,0,0,.7071067811865476f),["children"]=new JsonArray(bones.Select((b,i)=>(b,i)).Where(x=>x.b.Parent<0).Select(x=>(JsonNode?)JsonValue.Create(x.i+shift)).ToArray())});
        for(int i=0;i<bones.Length;i++)
        {
            var b=bones[i];var n=new JsonObject{["name"]=b.Name,["translation"]=ArrayNode(b.Position),["rotation"]=ArrayNode(b.Rotation),["scale"]=ArrayNode(b.Scale)};
            var children=bones.Select((c,j)=>(c,j)).Where(x=>x.c.Parent==i).Select(x=>(JsonNode?)JsonValue.Create(x.j+shift)).ToArray();if(children.Length>0)n["children"]=new JsonArray(children);nodes.Add(n);
        }
        var views=new JsonArray();var accessors=new JsonArray();var samplers=new JsonArray();var channelJson=new JsonArray();using var data=new MemoryStream();using var writer=new BinaryWriter(data);var timeCache=new Dictionary<string,int>();
        int Access(float[][] values,string type,bool times=false)
        {
            while(data.Length%4!=0)writer.Write((byte)0);long offset=data.Length;
            foreach(var row in values)foreach(float v in row){if(!float.IsFinite(v))throw new InvalidDataException("Non-finite motion key.");writer.Write(v);}
            int view=views.Count;views.Add(new JsonObject{["buffer"]=0,["byteOffset"]=offset,["byteLength"]=data.Length-offset});
            int index=accessors.Count;var a=new JsonObject{["bufferView"]=view,["componentType"]=5126,["count"]=values.Length,["type"]=type};
            if(times){a["min"]=new JsonArray(values[0][0]);a["max"]=new JsonArray(values[^1][0]);}accessors.Add(a);return index;
        }
        foreach(var c in channels)
        {
            if(c.Times.Length==0||c.Times.Length!=c.Values.Length)throw new InvalidDataException("Motion channel key count mismatch.");
            var bytes=new byte[c.Times.Length*4];Buffer.BlockCopy(c.Times,0,bytes,0,bytes.Length);var hash=MotionJson.Hash(bytes);
            if(!timeCache.TryGetValue(hash,out var input)){input=Access(c.Times.Select(t=>new[]{t}).ToArray(),"SCALAR",true);timeCache[hash]=input;}
            int sampler=samplers.Count;samplers.Add(new JsonObject{["input"]=input,["output"]=Access(c.Values,c.Path=="rotation"?"VEC4":"VEC3"),["interpolation"]="LINEAR"});
            channelJson.Add(new JsonObject{["sampler"]=sampler,["target"]=new JsonObject{["node"]=c.Node+shift,["path"]=c.Path}});
        }
        var json=new JsonObject{["asset"]=new JsonObject{["version"]="2.0",["generator"]="Animanager motion 1"},["scene"]=0,["scenes"]=new JsonArray(new JsonObject{["nodes"]=zUpParent?new JsonArray(0):new JsonArray(bones.Select((b,i)=>(b,i)).Where(x=>x.b.Parent<0).Select(x=>(JsonNode?)JsonValue.Create(x.i)).ToArray())}),["nodes"]=nodes,["buffers"]=new JsonArray(new JsonObject{["byteLength"]=data.Length}),["bufferViews"]=views,["accessors"]=accessors,["animations"]=new JsonArray(new JsonObject{["name"]=name,["samplers"]=samplers,["channels"]=channelJson}),["extras"]=new JsonObject{["animanagerSourceFps"]=fps,["animanagerMotionOnly"]=true}};
        return Pack(json,data.ToArray());
    }
    internal static JsonArray ArrayNode(float[] v)=>new(v.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());
    public static (JsonObject Json,byte[] Bin) Unpack(byte[] b)
    {
        if(b.Length<28||BitConverter.ToUInt32(b,0)!=0x46546c67||BitConverter.ToInt32(b,4)!=2||BitConverter.ToInt32(b,8)!=b.Length||BitConverter.ToUInt32(b,16)!=0x4e4f534a)throw new InvalidDataException("Invalid GLB header.");
        int len=BitConverter.ToInt32(b,12),offset=20+len;
        if(len<0||offset+8>b.Length||BitConverter.ToUInt32(b,offset+4)!=0x004e4942||BitConverter.ToInt32(b,offset)!=b.Length-offset-8)throw new InvalidDataException("Invalid GLB chunks.");
        return (JsonNode.Parse(Encoding.UTF8.GetString(b,20,len))!.AsObject(),b[(offset+8)..]);
    }
    public static byte[] Pack(JsonObject json,byte[] bin)
    {
        var j=Encoding.UTF8.GetBytes(json.ToJsonString());int padded=(j.Length+3)&~3,binLength=(bin.Length+3)&~3;
        using var s=new MemoryStream();using var w=new BinaryWriter(s);w.Write(0x46546c67);w.Write(2);w.Write(28+padded+binLength);w.Write(padded);w.Write(0x4e4f534a);w.Write(j);for(int i=j.Length;i<padded;i++)w.Write((byte)32);w.Write(binLength);w.Write(0x004e4942);w.Write(bin);for(int i=bin.Length;i<binLength;i++)w.Write((byte)0);return s.ToArray();
    }
    public static MotionGlb Read(string path)=>Read(File.ReadAllBytes(path));
    public static MotionGlb Read(byte[] bytes)
    {
        var (j,bin)=Unpack(bytes);var nodes=j["nodes"]!.AsArray();if(nodes.Count>2048)throw new InvalidDataException("Too many motion nodes.");
        float[] Floats(JsonNode? n,float[] fallback)=>n?.AsArray().Select(x=>x!.GetValue<float>()).ToArray()??fallback;
        var bones=nodes.Select((n,i)=>new MotionBone{Name=n?["name"]?.GetValue<string>()??"node:"+i,Position=Floats(n?["translation"],[0,0,0]),Rotation=Floats(n?["rotation"],[0,0,0,1]),Scale=Floats(n?["scale"],[1,1,1])}).ToArray();
        for(int i=0;i<nodes.Count;i++)foreach(var child in nodes[i]?["children"]?.AsArray()??new JsonArray()){int c=child!.GetValue<int>();if(c<0||c>=nodes.Count||bones[c].Parent!=-1)throw new InvalidDataException("Invalid motion hierarchy.");bones[c].Parent=i;}
        float[][] Access(int index)
        {
            var a=j["accessors"]![index]!;if(a["componentType"]!.GetValue<int>()!=5126||a["sparse"]!=null)throw new NotSupportedException("Only dense float animation accessors are supported.");
            int width=a["type"]!.GetValue<string>() switch{"SCALAR"=>1,"VEC3"=>3,"VEC4"=>4,_=>throw new NotSupportedException("Invalid motion accessor type")};
            int count=a["count"]!.GetValue<int>();if(count<1||count>200000)throw new InvalidDataException("Invalid motion key count.");
            var view=j["bufferViews"]![a["bufferView"]!.GetValue<int>()]!;int start=(view["byteOffset"]?.GetValue<int>()??0)+(a["byteOffset"]?.GetValue<int>()??0),stride=view["byteStride"]?.GetValue<int>()??width*4;
            if(start<0||stride<width*4||(long)start+(long)(count-1)*stride+width*4>bin.Length)throw new InvalidDataException("Motion accessor out of bounds.");
            return Enumerable.Range(0,count).Select(i=>Enumerable.Range(0,width).Select(k=>BitConverter.ToSingle(bin,start+i*stride+k*4)).ToArray()).ToArray();
        }
        var animation=j["animations"]?[0];var tracks=new List<MotionChannel>();
        if(animation!=null)foreach(var c in animation["channels"]!.AsArray())
        {
            var sampler=animation["samplers"]![c!["sampler"]!.GetValue<int>()]!;
            if((sampler["interpolation"]?.GetValue<string>()??"LINEAR")!="LINEAR")throw new NotSupportedException("Only LINEAR motion channels are supported.");
            string property=c["target"]!["path"]!.GetValue<string>();if(property is not ("translation" or "rotation" or "scale"))continue;
            int node=c["target"]!["node"]!.GetValue<int>();var times=Access(sampler["input"]!.GetValue<int>()).Select(v=>v[0]).ToArray();var values=Access(sampler["output"]!.GetValue<int>());
            if(node<0||node>=bones.Length||times.Length!=values.Length||times.Any(v=>!float.IsFinite(v)||v<0)||times.Skip(1).Where((v,i)=>v<=times[i]).Any()||values.SelectMany(v=>v).Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid animation samples.");
            tracks.Add(new(node,property,times,values));
        }
        return new(){Nodes=bones,Channels=tracks.ToArray(),Name=animation?["name"]?.GetValue<string>()??""};
    }
}
