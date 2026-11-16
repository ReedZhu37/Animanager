using Animanager.Core.Models;
using Animanager.Core.Pipeline;
using Animanager.Core.Storage;
using Animanager.Core.Jobs;

namespace Animanager.Core;

/// <summary>UI-independent application operations, shared by the UI and Limbo commands.</summary>
public sealed partial class WorkspaceApi(AssetDatabase database,LocalStorageProvider storage,BlenderPipeline blender)
{
    private readonly SemaphoreSlim _writer=new(1,1);
    public AssetDatabase Database { get; }=database;
    public LocalStorageProvider Storage { get; }=storage;
    public BlenderPipeline Blender { get; }=blender;
    public UassetPipeline Uasset { get; }=new();
    private BatchPlanner? _planner;
    private BatchJobQueue? _jobs;
    public BatchPlanner Planner=>_planner??=new BatchPlanner(this);
    public BatchJobQueue Jobs=>_jobs??=new BatchJobQueue(this);
    public (List<AssetItem> Items,int Total) Search(string query="",string category="All",int page=0)=>Database.Search(query,category,page);
    public AssetItem Find(string idOrName)
    {
        var item=Database.Find(idOrName);
        return item??throw new KeyNotFoundException("Asset not found (use its full ID or exact unique name): "+idOrName);
    }
    public async Task<ImportResult> ImportAsync(string[] paths,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        if(!await _writer.WaitAsync(0,ct))throw new InvalidOperationException("另一个导入任务正在进行。");
        try{return await new ImportService(Database,Storage).ImportAsync(paths,progress,ct);}
        finally{_writer.Release();}
    }
    public void SetTags(string idOrName,string tags){var a=Find(idOrName);a.Tags=tags.Trim();Database.Update(a);}
    public async Task<UassetImportOutcome> ImportUassetAsync(string input,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        if(!await _writer.WaitAsync(0,ct))throw new InvalidOperationException("另一个导入任务正在进行。");
        var output=Path.Combine(Storage.Root,"uasset",Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("Recognizing UE4 / UE5 standard skeletons; importing source skeletal poses without retargeting, Montage, curve or event evaluation.");
            var report=await Uasset.ConvertAsync(input,output,false,progress,ct);
            int added=0,skipped=0;var errors=new List<string>();var imported=new List<object>();
            foreach(var item in report.Items)
            {
                ct.ThrowIfCancellationRequested();
                if(item.Status=="failed"){errors.Add(item.Source+": "+item.Error);continue;}
                if(item.Status=="dependency")continue;
                if(item.Status!="converted"){progress?.Report("Unsupported: "+item.Source+" · "+item.Error);continue;}
                progress?.Report(item.Source+": "+item.Profile+", "+item.SkeletonBones+" bones"+(item.DependencySubstituted?"; missing Skeleton replaced with bundled standard baseline":"; source Skeleton validated"));
                var result=await new ImportService(Database,Storage).ImportAsync([item.Output],progress,ct);
                added+=result.Added;skipped+=result.Skipped;errors.AddRange(result.Errors);
                var asset=Database.FindByHash(item.OutputSha256);
                if(asset!=null)
                {
                    if(result.Added>0){asset.Tags="uasset "+(item.Profile=="ue5-mannequin"?"UE5_Mannequin Manny":"UE4_Mannequin")+" "+Path.GetFileNameWithoutExtension(input);Database.Update(asset);}
                    imported.Add(new{source=item.Source,profile=item.Profile,skeletonSource=item.SkeletonSource,dependencySubstituted=item.DependencySubstituted,id=asset.Id,path=asset.VirtualPath,sha256=asset.FileHash,status=result.Added>0?"added":"duplicate"});
                    // Only our successfully copied GLBs are removed; the immutable conversion report retains source hashes.
                    File.Delete(item.Output);
                }
                BatchPlanner.AtomicJson(Path.Combine(output,"import-report.json"),new{version=1,input,profile=UassetPipeline.Profile,conversionReport=report.ReportPath,added,skipped,unsupported=report.Unsupported,errors,items=imported});
            }
            var outcome=new UassetImportOutcome(added,skipped,report.Unsupported,report.ReportPath,errors);
            progress?.Report($"UASSET_RESULT {added} added; {skipped} duplicates; {report.Unsupported} unsupported. {report.ReportPath}");
            return outcome;
        }
        finally{_writer.Release();}
    }
    public void SetFavorite(string idOrName,bool favorite){var a=Find(idOrName);a.Favorite=favorite;Database.Update(a);}
    public async Task<string> ExportFbxAsync(string idOrName,string folder,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        var a=Find(idOrName);var source=await Storage.AcquireLocalFileAsync(a.VirtualPath,ct);
        if(!Path.IsPathFullyQualified(folder))throw new IOException("Export folder must be an absolute path");
        Directory.CreateDirectory(folder);
        // Put staging beside the final outputs: publication is a same-volume rename,
        // including when the library and destination live on different drives.
        // Export through a private job directory, then publish atomically without overwriting.
        var jobsRoot=Path.Combine(folder,".animanager-staging");
        var job=Path.Combine(jobsRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
        var temp=Path.Combine(job,"result.fbx");
        try
        {
            if(Animanager.Core.Motion.MotionJson.IsMotion(source))source=Animanager.Core.Motion.MotionExport.Glb(source,Path.Combine(job,"assembled.glb"),true);
            await Blender.ConvertAsync(source,temp,progress,ct);
            ct.ThrowIfCancellationRequested();
            var name=string.Concat(a.Name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
            for(int i=1;;i++)
            {
                var destination=Path.Combine(folder,name+(i==1?"":"_"+i)+".fbx");
                try{File.Move(temp,destination,false);return destination;}
                catch(IOException) when(File.Exists(destination)){}
            }
        }
        finally
        {
            var cleanup=Path.GetFullPath(job);
            var allowedRoot=Path.GetFullPath(jobsRoot)+Path.DirectorySeparatorChar;
            if(cleanup.StartsWith(allowedRoot,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(cleanup))Directory.Delete(cleanup,true);
        }
    }
}
