using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace Animanager.CueImporter;

internal static class Program
{
    public const string Revision="d05e5859ad240be10c9d5c8344f5de7c072123d0";
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==3&&args[0]=="validate-motion")return MotionVerification.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));
            if(args.Length==2&&args[0]=="--self-test")return RegressionTests.Run(args[1]);
            if(args.Length==3&&args[0]=="sample-source")return SourceSamples.Run(args[1],args[2]);
            if(args.Length==4&&args[0]=="probe")return Probe.Run(args[1],args[2],args[3]);
            if(args.Length==1&&args[0]=="--version"){Console.WriteLine("Animanager CUE importer 3 / CUE4Parse "+Revision);return 0;}
            if(args.Length!=4||args[0] is not ("convert" or "inspect")||args[3] is not ("auto" or "ue4-mannequin" or "ue5-mannequin"))throw new ArgumentException("Usage: CueImporter convert|inspect <input .uasset / directory / .zip> <new output directory> auto|ue4-mannequin|ue5-mannequin");
            var input=Path.GetFullPath(args[1]);var output=Path.GetFullPath(args[2]);
            if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new IOException("Output must be empty; existing files are never overwritten.");
            Directory.CreateDirectory(output);
            Globals.FatalObjectSerializationErrors=true;Globals.WarnMissingImportPackage=false;
            using var catalog=new AssetCatalog(input);var profiles=new SkeletonProfiles();var rows=new List<Result>();
            foreach(var asset in catalog.Assets)
            {
                var row=new Result{source=asset.Relative};rows.Add(row);
                try
                {
                    var package=AssetCatalog.Load(asset,out var data);
                    row.sha256=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                    var engine=package.Summary.SavedByEngineVersion!;row.engine=engine.ToString();
                    var export=package.ExportMap.Select((x,i)=>(x,i)).FirstOrDefault(x=>x.x.ClassIndex.Name is "AnimSequence" or "AnimMontage" or "Skeleton");
                    if(export.x==null)export=package.ExportMap.Select((x,i)=>(x,i)).FirstOrDefault(x=>x.x.ObjectName.Text==Path.GetFileNameWithoutExtension(asset.Relative));
                    row.type=export.x?.ClassIndex.Name??"Unknown";
                    if(row.type=="Skeleton"){row.status="dependency";continue;}
                    if(row.type!="AnimSequence")throw new NotSupportedException("Unsupported asset class: "+row.type);
                    if(!((engine.Major==4&&engine.Minor==23)||(engine.Major==5&&engine.Minor is 0 or 6)))throw new NotSupportedException($"Editor animation reader is not validated for UE {engine.Major}.{engine.Minor}.");
                    var anim=(UAnimSequence)package.ExportsLazy[export.i].Value;
                    row.skeleton=AssetCatalog.ImportPath(package,anim.Skeleton);
                    row.rootMotion=anim.bEnableRootMotion;row.forceRootLock=anim.bForceRootLock;row.additive=anim.AdditiveAnimType.ToString();row.notifies=anim.Notifies?.Length??0;
                    var clip=AnimationReader.Read(package,anim,engine.Major,engine.Minor);
                    row.frames=clip.Frames;row.duration=clip.Duration;row.tracks=clip.Tracks.Select(t=>t.Name).ToArray();row.encoding=clip.Encoding;
                    var rig=profiles.Resolve(catalog,row.skeleton,clip,args[3]);
                    row.profile=rig.Profile.id;row.skeletonSource=rig.Dependency;row.skeletonSha256=rig.Hash;row.dependencySubstituted=rig.Substituted;row.skeletonBones=rig.Bones.Length;row.previewBones=SkeletonProfiles.MeshBones(rig.Mesh).Count;
                    row.name=Path.ChangeExtension(asset.Relative,null).Replace('\\','/').Replace("/","__");
                    if(args[0]=="convert")
                    {
                        byte[] bytes;
                        if(MotionWriter.Matches(rig))
                        {
                            var dependency=catalog.Resolve(row.skeleton)??throw new InvalidDataException("Missing source Skeleton.");
                            bytes=MotionWriter.Write(output,rig,clip,row,data,dependency.Read());
                            row.storage="motion-v1";row.previewBones=rig.Profile.bones.Length;
                            row.output=Path.Combine(output,row.name+".motion.json");
                        }
                        else {bytes=GlbWriter.Convert(rig,clip);row.output=Path.Combine(output,row.name+".glb");}
                        using(var stream=new FileStream(row.output,FileMode.CreateNew))stream.Write(bytes);
                        row.outputSha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    }
                    row.status=args[0]=="convert"?"converted":"supported";
                }
                catch(NotSupportedException e){row.status="unsupported";row.error=e.Message;}
                catch(Exception e){row.status="failed";row.error=e.ToString();}
                finally{Console.WriteLine($"CUE_PROGRESS {rows.Count}/{catalog.Assets.Count} {row.status} {asset.Relative}");}
            }
            var report=new{version=3,backend="CUE4Parse",revision=Revision,localPatches=new[]{"ue50-tagged-raw-track-v1"},input,profile=args[3],semantics="Source skeletal transforms only. Notifications, curves, Montage, additive blending and retargeting are not evaluated. Missing Skeleton substitutions are recorded per item.",items=rows,converted=rows.Count(r=>r.status=="converted"),unsupported=rows.Count(r=>r.status=="unsupported"),failed=rows.Count(r=>r.status=="failed")};
            File.WriteAllText(Path.Combine(output,"conversion-report.json"),JsonSerializer.Serialize(report,Json));
            Console.WriteLine("CUE_REPORT "+Path.Combine(output,"conversion-report.json"));
            return rows.Count==0||rows.Any(r=>r.status=="failed")?2:0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
public sealed class Result
{
    public string storage{get;set;}="glb";
    public string source{get;set;}="";public string sha256{get;set;}="";public string name{get;set;}="";public string type{get;set;}="";public string engine{get;set;}="";
    public string status{get;set;}="";public string error{get;set;}="";public string output{get;set;}="";public string outputSha256{get;set;}="";public string skeleton{get;set;}="";
    public string profile{get;set;}="";public string encoding{get;set;}="";public string skeletonSource{get;set;}="";public string skeletonSha256{get;set;}="";public bool dependencySubstituted{get;set;}public int skeletonBones{get;set;}public int previewBones{get;set;}
    public int frames{get;set;}public float duration{get;set;}public string[] tracks{get;set;}=[];public bool rootMotion{get;set;}public bool forceRootLock{get;set;}public string additive{get;set;}="";public int notifies{get;set;}
}
