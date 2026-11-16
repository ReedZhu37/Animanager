using CUE4Parse;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;
namespace Animanager.CueImporter;
internal static class Probe
{
    public static int Run(string file,string output,string game)
    {
        Globals.FatalObjectSerializationErrors=true;
        Log.Logger=new LoggerConfiguration().WriteTo.Console().CreateLogger();
        Directory.CreateDirectory(output);
        using var ar=new FByteArchive(file,File.ReadAllBytes(file),new VersionContainer(Enum.Parse<EGame>(game)));
        var p=new Package(ar,null,(FArchive?)null);
        Console.WriteLine($"SUMMARY {p.Summary.SavedByEngineVersion} {p.Summary.FileVersionUE}");
        for(int i=0;i<p.ExportMap.Length;i++)
        {
            var e=p.ExportMap[i];Console.WriteLine($"EXPORT {i} {e.ClassIndex.Name} {e.ObjectName} size={e.SerialSize}");
            try
            {
                var o=p.ExportsLazy[i].Value;
                File.WriteAllText(Path.Combine(output,i+"-"+o.Name+".json"),JsonConvert.SerializeObject(o,Formatting.Indented));
                if(o is UAnimSequence a)Console.WriteLine($"ANIM frames={a.NumFrames} duration={a.SequenceLength} raw={a.RawAnimationData?.Length} compressed={a.CompressedDataStructure?.GetType()}");
                if(o is USkeleton s)File.WriteAllText(Path.Combine(output,"skeleton.json"),JsonConvert.SerializeObject(new{s.ReferenceSkeleton,s.BoneTree},Formatting.Indented));
            }
            catch(Exception ex){Console.WriteLine("ERROR "+ex);}
        }
        return 0;
    }
}
