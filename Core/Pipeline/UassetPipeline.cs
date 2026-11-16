using System.Diagnostics;
using System.Text.Json;

namespace Animanager.Core.Pipeline;

/// <summary>Isolated local parser. Never starts Unreal or uploads source files.</summary>
public sealed class UassetPipeline
{
    public string Executable {get;}=RuntimePaths.Bundled(Godot.OS.HasFeature("editor")?".runtime/cue-importer/CueImporter.exe":"Importers/Cue4Parse/CueImporter.exe");
    public bool Available=>File.Exists(Executable);
    public const string Profile="auto";
    public async Task<UassetReport> ConvertAsync(string input,string output,bool inspect,IProgress<string>? progress,CancellationToken ct)
    {
        if(!Available)throw new FileNotFoundException("Independent .uasset importer is missing. Run Tools/Build-CueImporter.ps1 or use a complete App build.",Executable);
        if(!Path.IsPathFullyQualified(input)||!Path.IsPathFullyQualified(output))throw new ArgumentException("Importer paths must be absolute.");
        ct.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{inspect?"inspect":"convert",input,output,Profile})start.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=start};
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        process.Start();
        using var registration=cancel.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        var errors=new List<string>();
        async Task Drain(StreamReader reader,bool error){while(await reader.ReadLineAsync() is {} line){if(error&&errors.Count<30)errors.Add(line);progress?.Report(line);}}
        await Task.WhenAll(Drain(process.StandardOutput,false),Drain(process.StandardError,true),process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();if(timeout.IsCancellationRequested)throw new TimeoutException("CUE4Parse import timed out; source files are unchanged.");
        var reportPath=Path.Combine(output,"conversion-report.json");
        if(!File.Exists(reportPath))throw new IOException($"CUE4Parse exited {process.ExitCode} without a report: "+string.Join("\n",errors));
        var report=JsonSerializer.Deserialize<UassetReport>(await File.ReadAllTextAsync(reportPath,ct),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new IOException("Invalid CUE report.");
        if(report.Version is not (2 or 3)||report.Backend!="CUE4Parse"||report.Profile!=Profile||process.ExitCode is not (0 or 2))throw new IOException("Unsupported CUE report or failed process.");
        var root=Path.GetFullPath(output)+Path.DirectorySeparatorChar;
        foreach(var item in report.Items.Where(i=>i.Status=="converted"))
            if(!Path.GetFullPath(item.Output).StartsWith(root,StringComparison.OrdinalIgnoreCase)||(Path.GetExtension(item.Output)!=".glb"&&!Animanager.Core.Motion.MotionJson.IsMotion(item.Output))||!File.Exists(item.Output))throw new IOException("Converter returned an invalid output path.");
        report.ReportPath=reportPath;
        progress?.Report($"CUE4Parse: {report.Converted} converted, {report.Unsupported} unsupported, {report.Failed} failed. Profile: {Profile}. Report: {reportPath}");
        return report;
    }
}
public sealed class UassetReport
{
    public int Version{get;set;}public string Backend{get;set;}="";public string Profile{get;set;}="";public int Converted{get;set;}public int Unsupported{get;set;}public int Failed{get;set;}
    public List<UassetResult> Items{get;set;}=[];public string ReportPath{get;set;}="";
}
public sealed class UassetResult
{
    public string Profile{get;set;}="";public string Encoding{get;set;}="";public string SkeletonSource{get;set;}="";public bool DependencySubstituted{get;set;}public int SkeletonBones{get;set;}public int PreviewBones{get;set;}
    public string Source{get;set;}="";public string Status{get;set;}="";public string Error{get;set;}="";public string Output{get;set;}="";public string OutputSha256{get;set;}="";
}
public sealed record UassetImportOutcome(int Added,int Skipped,int Unsupported,string ReportPath,List<string> Errors);
