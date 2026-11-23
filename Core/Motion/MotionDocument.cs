using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Animanager.Core.Motion;

public sealed class MotionObject
{
    public string Path {get;set;}="";
    public string Sha256 {get;set;}="";
    public long Bytes {get;set;}
}
public sealed class MotionBone
{
    public string Name {get;set;}="";
    public int Parent {get;set;}=-1;
    public float[] Position {get;set;}=[0,0,0];
    public float[] Rotation {get;set;}=[0,0,0,1];
    public float[] Scale {get;set;}=[1,1,1];
}
public sealed class MotionRig
{
    public int SchemaVersion {get;set;}=1;
    public string Id {get;set;}="";
    public string CoordinateConvention {get;set;}="ue-reflected-y-meters-with-zup-parent-v1";
    public MotionBone[] Bones {get;set;}=[];
}
public sealed class MotionManifest
{
    public int SchemaVersion {get;set;}=1;
    public string Kind {get;set;}="animanager-motion";
    public string Name {get;set;}="";
    public string Clip {get;set;}="";
    public double Duration {get;set;}
    public int Frames {get;set;}
    public double SourceFps {get;set;}
    public string PoseSemantics {get;set;}="raw-source-pose";
    public string RootMotionStorage {get;set;}="embedded-in-body-root";
    public string Mapping {get;set;}="nodachi-ue5-hand-r-v1";
    public Dictionary<string,string> Provenance {get;set;}=[];
    public Dictionary<string,MotionObject> Objects {get;set;}=[];
    public List<MotionProp> Props {get;set;}=[];
}
public sealed class MotionProp
{
    internal const string PrimaryId = "primary-weapon";
    internal const string NodachiSourceBone = "Nodachi_Weapon_R";
    public string Id {get;set;}=PrimaryId;
    public string SourceBone {get;set;}=NodachiSourceBone;
    public string Anchor {get;set;}="hand_r";
    public string Space {get;set;}="skeleton-anchor";
    public string TrackObject {get;set;}="prop";
    public string State {get;set;}="animated";
    // Hierarchy establishes an anchor, not contact or the shape of the missing weapon mesh.
    public string GripBasis {get;set;}="source-bone-origin-unverified";
    public List<MotionContact> Contacts {get;set;}=[];
}
public sealed class MotionContact
{
    public double Start {get;set;}
    public double End {get;set;}
    public string State {get;set;}="unknown";
    public string Evidence {get;set;}="not-authored-in-anim-sequence";
    public string Enforcement {get;set;}="observe-only";
}
public sealed class MotionPresentation
{
    public int SchemaVersion {get;set;}=1;
    public string Revision {get;set;}="default";
    public string PreviousRevision {get;set;}="";
    public float[] GripPosition {get;set;}=[0,0,0];
    public float[] GripRotation {get;set;}=[0,0,0,1];
    public bool GripConfirmed {get;set;}
    public string PropPreset {get;set;}="diagnostic-blade-v1";
    public bool Visible {get;set;}=true;
    public List<MotionContact>? Contacts {get;set;}
}
public static class MotionJson
{
    public static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,WriteIndented=true};
    public static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,Options);
    public static T Read<T>(string path)=>JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Options)??throw new InvalidDataException("Empty motion document.");
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool IsMotion(string path)=>path.EndsWith(".motion.json",StringComparison.OrdinalIgnoreCase);
    public static void Atomic<T>(string path,T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var f=new FileStream(temp,FileMode.CreateNew)){var b=Bytes(value);f.Write(b);f.Flush(true);}File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static string ResolveObject(string manifestPath,MotionObject item,bool verify=true)
    {
        if(item.Sha256.Length!=64||item.Sha256.Any(c=>!char.IsAsciiHexDigit(c))||item.Bytes<0)throw new InvalidDataException("Invalid motion object identity.");
        var root=System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(manifestPath)!)+System.IO.Path.DirectorySeparatorChar;
        if(System.IO.Path.IsPathRooted(item.Path)||item.Path.Contains(':')||!item.Path.Replace('\\','/').StartsWith("objects/",StringComparison.Ordinal))throw new InvalidDataException("Motion object must be inside objects/.");
        var path=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,item.Path));
        if(!path.StartsWith(System.IO.Path.Combine(root,"objects")+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new IOException("Missing or invalid motion object: "+item.Path);
        Animanager.Core.Storage.LocalStorageProvider.RequireLocalPath(path);
        if(new FileInfo(path).Length!=item.Bytes)throw new InvalidDataException("Motion object size mismatch: "+item.Path);
        if(verify&&Hash(File.ReadAllBytes(path))!=item.Sha256)throw new InvalidDataException("Motion object checksum mismatch: "+item.Path);
        return path;
    }
    public static MotionManifest Load(string path,bool verify=true)
    {
        var m=ReadManifest(path);
        foreach(var o in m.Objects.Values)ResolveObject(path,o,verify);
        return m;
    }
    public static MotionManifest ReadManifest(string path)
    {
        var m=Read<MotionManifest>(path);
        if(m.SchemaVersion!=1||m.Kind!="animanager-motion"||m.Mapping!="nodachi-ue5-hand-r-v1"||m.PoseSemantics!="raw-source-pose"||m.RootMotionStorage!="embedded-in-body-root"||!double.IsFinite(m.Duration)||m.Duration<=0||m.Duration>3600||m.Frames<2||!double.IsFinite(m.SourceFps)||m.SourceFps<1||m.SourceFps>600)throw new InvalidDataException("Unsupported motion manifest or timeline.");
        foreach(var key in new[]{"preset","rig","sourceRig","source","body","prop"})if(!m.Objects.ContainsKey(key))throw new InvalidDataException("Missing motion object: "+key);
        if(m.Props.Count!=1||m.Props[0].Anchor!="hand_r"||m.Props[0].Space!="skeleton-anchor"||m.Props[0].TrackObject!="prop")throw new NotSupportedException("This version supports the verified Nodachi right-hand mapping.");
        ValidateContacts(m.Props[0].Contacts,m.Duration);
        return m;
    }
    public static void ValidateContacts(List<MotionContact> contacts,double duration)
    {
        double end=0;
        foreach(var c in contacts)
        {
            if(!double.IsFinite(c.Start)||!double.IsFinite(c.End)||Math.Abs(c.Start-end)>1e-7||c.End<=c.Start||c.End>duration+1e-7||c.State is not ("unknown" or "right" or "left" or "both" or "free")||c.Enforcement!="observe-only")throw new InvalidDataException("Contacts must cover the clip with ordered, contiguous intervals; this version does not enforce IK.");
            end=c.End;
        }
        if(contacts.Count==0||Math.Abs(end-duration)>1e-7)throw new InvalidDataException("Contact intervals must cover the complete clip.");
    }
    public static string PresentationPath(string manifestPath)=>manifestPath+".presentation.json";
    public static MotionPresentation Presentation(string manifestPath,MotionManifest m)
    {
        var path=PresentationPath(manifestPath);var p=File.Exists(path)?Read<MotionPresentation>(path):new();ValidatePresentation(p,m);return p;
    }
    public static void ValidatePresentation(MotionPresentation p,MotionManifest m)
    {
        if(p.SchemaVersion!=1||p.PropPreset!="diagnostic-blade-v1"||p.GripPosition.Length!=3||p.GripRotation.Length!=4||p.GripPosition.Concat(p.GripRotation).Any(v=>!float.IsFinite(v))||p.GripPosition.Any(v=>Math.Abs(v)>10))throw new InvalidDataException("Invalid grip or unsupported presentation preset.");
        var q=p.GripRotation;float norm=q.Sum(v=>v*v);if(Math.Abs(norm-1)>1e-4)throw new InvalidDataException("Grip quaternion must be normalized.");
        if(p.Contacts!=null)ValidateContacts(p.Contacts,m.Duration);
    }
    public static string ContactAt(List<MotionContact> contacts,double t)=>contacts.FirstOrDefault(c=>t>=c.Start&&(t<c.End||c==contacts[^1]&&t<=c.End))?.State??"unknown";
}
