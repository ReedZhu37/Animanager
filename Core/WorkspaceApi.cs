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
        using var activity=BeginActivity();
        if(!await _writer.WaitAsync(0,ct))throw new InvalidOperationException("另一个导入任务正在进行。");
        try{return await new ImportService(Database,Storage).ImportAsync(paths,progress,ct);}
        finally{_writer.Release();}
    }
    public void SetTags(string idOrName,string tags)=>Edit([Find(idOrName).Id],a=>a with{Tags=tags.Trim()});
    public async Task<UassetImportOutcome> ImportUassetAsync(string input,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        using var activity=BeginActivity();
        if(!await _writer.WaitAsync(0,ct))throw new InvalidOperationException("另一个导入任务正在进行。");
        var id=Guid.NewGuid().ToString("N");
        var output=Storage.Resolve($"cache/conversion/{id}");
        var reports=Storage.Resolve($"uasset/{id}");
        try
        {
            progress?.Report("Recognizing UE4 / UE5 standard skeletons; importing source skeletal poses without retargeting, Montage, curve or event evaluation.");
            var report=await Uasset.ConvertAsync(input,output,false,progress,ct);
            Directory.CreateDirectory(reports);
            var durableReport=Path.Combine(reports,"conversion-report.json");
            File.Copy(report.ReportPath,durableReport);
            report.ReportPath=durableReport;
            int added=0,skipped=0,excludedInPlace=0,excludedWithoutAnimation=0;
            var addedAssetIds = new List<string>();
            var errors=new List<string>();var warnings=new List<string>();var imported=new List<object>();
            foreach(var item in report.Items)
            {
                ct.ThrowIfCancellationRequested();
                if(item.Status=="failed"){errors.Add(item.Source+": "+item.Error);continue;}
                if(item.Status=="dependency")continue;
                if(item.Status!="converted"){progress?.Report("Unsupported: "+item.Source+" · "+item.Error);continue;}
                if(AnimationDisplacement.IsInPlaceSource(item.Source))
                {
                    RecordInPlace(item);
                    continue;
                }
                progress?.Report(item.Source+": "+item.Profile+", "+item.SkeletonBones+" bones"+(item.DependencySubstituted?"; missing Skeleton replaced with bundled standard baseline":"; source Skeleton validated"));
                var result=await new ImportService(Database,Storage).ImportAsync([item.Output],progress,ct);
                addedAssetIds.AddRange(result.AddedAssetIds);
                added+=result.Added;skipped+=result.Skipped;errors.AddRange(result.Errors);warnings.AddRange(result.Warnings);
                if(result.ExcludedInPlace>0)
                {
                    RecordInPlace(item);
                    continue;
                }
                if(result.ExcludedWithoutAnimation>0)
                {
                    excludedWithoutAnimation+=result.ExcludedWithoutAnimation;
                    imported.Add(new { source=item.Source, status="skipped-no-animation" });
                    File.Delete(item.Output);
                    continue;
                }
                var asset=Database.FindByHash(item.OutputSha256);
                if(asset!=null&&result.Errors.Count==0)
                {
                    if(result.Added>0)ChangeTags([asset.Id],"uasset "+(item.Profile=="ue5-mannequin"?"UE5_Mannequin Manny":"UE4_Mannequin")+" "+Path.GetFileNameWithoutExtension(input),true);
                    imported.Add(new{source=item.Source,profile=item.Profile,skeletonSource=item.SkeletonSource,dependencySubstituted=item.DependencySubstituted,id=asset.Id,path=asset.VirtualPath,sha256=asset.FileHash,status=result.Added>0?"added":"duplicate"});
                    // The immutable conversion report retains source hashes after the output is archived.
                    File.Delete(item.Output);
                }
            }
            BatchPlanner.AtomicJson(Path.Combine(reports,"import-report.json"),new{version=2,input,profile=UassetPipeline.Profile,conversionReport=report.ReportPath,cacheRoot=output,added,skipped,excludedInPlace,excludedWithoutAnimation,unsupported=report.Unsupported,errors,items=imported});
            warnings.AddRange(report.Items.Where(i=>i.Status is not ("converted" or "dependency" or "failed"))
                .GroupBy(i=>i.Error).OrderByDescending(g=>g.Count())
                .Select(g=>$"{g.Count()} 个：{g.Key}（例如 {Path.GetFileName(g.First().Source)}）").Take(12));
            if(added+skipped+excludedInPlace+excludedWithoutAnimation==0&&errors.Count==0&&warnings.Count==0)
                warnings.Add(report.Items.Count==0?"目录中没有找到 .uasset 文件。":"目录中只有骨架等依赖资源，没有可导入的 AnimSequence 动画。");
            var outcome=new UassetImportOutcome(added,skipped,report.Unsupported,report.ReportPath,errors)
                {AddedAssetIds=addedAssetIds.ToArray(),Warnings=warnings,ExcludedInPlace=excludedInPlace,ExcludedWithoutAnimation=excludedWithoutAnimation};
            progress?.Report($"UASSET_RESULT {added} added; {skipped} duplicates; {excludedInPlace} InPlace skipped; {excludedWithoutAnimation} without animation skipped; {report.Unsupported} unsupported. {report.ReportPath}");
            return outcome;

            void RecordInPlace(UassetResult item)
            {
                excludedInPlace++;
                progress?.Report("跳过 InPlace 动画 · " + item.Source);
                imported.Add(new { source=item.Source, status="skipped-in-place" });
                File.Delete(item.Output);
            }
        }
        finally{_writer.Release();}
    }
    public void SetFavorite(string idOrName,bool favorite)=>SetFavorites([Find(idOrName).Id],favorite);
    public Task<string> ExportFbxAsync(string idOrName,string folder,IProgress<string>? progress=null,CancellationToken ct=default) =>
        ExportAsync(idOrName,folder,JobKind.ExportFbx,null,progress,ct);
}
