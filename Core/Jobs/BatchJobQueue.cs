using System.Text.Json;

namespace Animanager.Core.Jobs;

public sealed record JobUpdate(string Id,JobState State,int Finished,int Total,string Message);

/// <summary>Sequential, durable batch jobs. Never retries automatically or overwrites output files.</summary>
public sealed class BatchJobQueue
{
    private readonly WorkspaceApi _api;
    private readonly object _gate=new();
    private readonly List<JobRecord> _jobs=[];
    private readonly Dictionary<string,TaskCompletionSource<JobRecord>> _completion=[];
    private CancellationTokenSource? _activeToken;
    private string? _activeId;
    private bool _pumping,_stopping;
    private Task _pump=Task.CompletedTask;
    private IDisposable? _activity;
    public event Action<JobUpdate>? Changed;
    public string ReportsFolder { get; }
    public string? CurrentId { get{lock(_gate)return _activeId;} }
    public string? LatestId { get{lock(_gate)return _jobs.LastOrDefault()?.Id;} }
    public bool Busy {get{lock(_gate)return _pumping;}}

    public BatchJobQueue(WorkspaceApi api)
    {
        _api=api;ReportsFolder=Path.Combine(api.Storage.Root,"jobs","reports");Directory.CreateDirectory(ReportsFolder);
        foreach(var path in Directory.EnumerateFiles(ReportsFolder,"*.json").OrderBy(p=>File.GetLastWriteTimeUtc(p)))
        {
            // A damaged report must not prevent the library from opening; it remains on disk for inspection.
            JobRecord? job;
            try{job=JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(path),BatchPlanner.JsonOptions);}
            catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException){continue;}
            if(job==null||job.Version!=1||!Guid.TryParseExact(job.Id,"N",out _)||Path.GetFileNameWithoutExtension(path)!=job.Id||job.Items==null||job.Items.Any(i=>i==null||i.Source==null))continue;
            RebaseLibraryPaths(job, api.Storage.Root);
            job.ReportPath=path;
            if(job.State is JobState.Queued or JobState.Running)
            {
                job.State=JobState.Interrupted;job.FinishedUtc=DateTime.UtcNow;job.Message="Previous session ended. Inspect outputs, then use job retry.";
                foreach(var item in job.Items.Where(i=>i.State is ItemState.Running or ItemState.Pending))item.State=ItemState.Interrupted;
                Save(job);
            }
            _jobs.Add(job);
        }
    }

    private static void RebaseLibraryPaths(JobRecord job, string libraryRoot)
    {
        if (string.IsNullOrEmpty(job.ReportPath) || !Path.IsPathFullyQualified(job.ReportPath)) return;
        var reports = Path.GetDirectoryName(job.ReportPath);
        var jobs = Path.GetDirectoryName(reports);
        var previousRoot = Path.GetDirectoryName(jobs);
        if (previousRoot == null || !string.Equals(Path.GetFileName(reports), "reports", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(jobs), "jobs", StringComparison.OrdinalIgnoreCase) ||
            Storage.LibraryManager.SamePath(previousRoot, libraryRoot)) return;

        string Rebase(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return path;
            try
            {
                var full = Path.GetFullPath(path);
                return Storage.LibraryManager.Nested(full, previousRoot)
                    ? Path.Combine(libraryRoot, Path.GetRelativePath(previousRoot, full)) : path;
            }
            catch (ArgumentException) { return path; }
        }

        job.OutputFolder = Rebase(job.OutputFolder);
        foreach (var item in job.Items)
        {
            item.Log = Rebase(item.Log);
            item.Output = Rebase(item.Output);
        }
    }
    public string Enqueue(BatchPlan plan,string retryOf="")
    {
        if(plan.Items.Count==0)throw new InvalidOperationException("Cannot enqueue an empty plan.");
        if(!Enum.IsDefined(plan.Kind))throw new ArgumentException("Invalid job kind.");
        if(plan.Kind==JobKind.ExportFbx&&!_api.Blender.Available)throw new InvalidOperationException("Blender is not configured; use pipeline blender <exe>.");
        if(plan.Kind==JobKind.ImportUasset&&!_api.Uasset.Available)throw new InvalidOperationException("缺少 uasset 导入器。请使用包含 CUE4Parse 的完整应用，开发环境可运行 Tools/Build-CueImporter.ps1。");
        var job=new JobRecord{Kind=plan.Kind,PlanId=plan.Id,RetryOf=retryOf,OutputFolder=plan.OutputFolder,IgnoredFiles=plan.IgnoredFiles,
            ExportOptions=plan.Kind.SnapshotExportOptions(plan.ExportOptions),
            Items=plan.Items.Select(p=>new JobItem{Source=p}).ToList()};
        job.ReportPath=Path.Combine(ReportsFolder,job.Id+".json");
        lock(_gate)
        {
            if(_stopping)throw new InvalidOperationException("Queue is shutting down.");
            using var admission=_api.BeginActivity();
            Save(job);_jobs.Add(job);_completion[job.Id]=new(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!_pumping){_activity=_api.BeginActivity();_pumping=true;_pump=PumpAsync();}
        }
        Notify(job);return job.Id;
    }
    public Task<JobRecord> WaitAsync(string id)
    {
        lock(_gate){var job=Find(id);return _completion.TryGetValue(id,out var completion)?completion.Task:Task.FromResult(Clone(job));}
    }
    public JobRecord Get(string id){lock(_gate)return Clone(Find(id));}
    public JobRecord[] Recent(int count=20){lock(_gate)return _jobs.TakeLast(count).Reverse().Select(Clone).ToArray();}
    public bool Cancel(string id)
    {
        JobRecord job;
        lock(_gate)
        {
            job=Find(id);
            if(job.State==JobState.Queued)
            {
                foreach(var item in job.Items)item.State=ItemState.Cancelled;
                job.State=JobState.Cancelled;job.Message="Cancelled before starting";job.FinishedUtc=DateTime.UtcNow;Save(job);
                _completion[id].TrySetResult(Clone(job));
            }
            else if(job.State==JobState.Running){job.Message="Cancellation requested";_activeToken?.Cancel();}
            else return false;
        }
        Notify(job);return true;
    }
    public string Retry(string id)
    {
        var original=Get(id);
        if(original.State is JobState.Queued or JobState.Running)throw new InvalidOperationException("Wait for the job to stop before retrying.");
        var items=original.Items.Where(i=>i.State is ItemState.Failed or ItemState.Cancelled or ItemState.Interrupted or ItemState.Pending)
            .Select(i=>i.Source with{Error=""}).ToList();
        if(items.Count==0)throw new InvalidOperationException("Nothing to retry; successful / skipped items are not exported again.");
        var plan=new BatchPlan{Kind=original.Kind,Selection="Retry "+id,OutputFolder=original.OutputFolder,
            ExportOptions=original.Kind.SnapshotExportOptions(original.ExportOptions),Items=items};
        _api.Planner.Save(plan);return Enqueue(plan,id);
    }
    public Task ShutdownAsync()
    {
        string[] pending;
        lock(_gate){_stopping=true;pending=_jobs.Where(j=>j.State is JobState.Queued or JobState.Running).Select(j=>j.Id).ToArray();}
        foreach(var id in pending)Cancel(id);
        return _pump;
    }
    private async Task PumpAsync()
    {
        await Task.Yield();
        while(true)
        {
            JobRecord job;CancellationTokenSource token;
            lock(_gate)
            {
                var next=_jobs.FirstOrDefault(j=>j.State==JobState.Queued);
                if(next==null){_activity?.Dispose();_activity=null;_pumping=false;return;}
                job=next;_activeId=job.Id;_activeToken=token=new CancellationTokenSource();
                job.State=JobState.Running;job.StartedUtc=DateTime.UtcNow;job.Message="Starting";
            }
            try
            {
                Save(job);Notify(job);
                for(int index=0;index<job.Items.Count;index++)
                {
                    var item=job.Items[index];
                    token.Token.ThrowIfCancellationRequested();
                    lock(_gate){item.State=ItemState.Running;job.Message=$"{index+1}/{job.Total}: {item.Source.Name}";
                        item.Log=Path.Combine(ReportsFolder,job.Id+"_"+(index+1)+".log");Save(job);}
                    Notify(job);
                    try
                    {
                        using var log=new ItemLog(item.Log,line=>{lock(_gate)job.Message=line;Notify(job);});
                        log.Report("Input: "+item.Source.Input);
                        if(item.Source.Error.Length>0)throw new IOException(item.Source.Error);
                        if(job.Kind.IsExport())
                        {
                            var output=await _api.ExportAsync(item.Source.AssetId,job.OutputFolder,job.Kind,job.ExportOptions,log,token.Token);
                            lock(_gate){item.Output=output;item.State=ItemState.Succeeded;}
                        }
                        else if(job.Kind==JobKind.ImportUasset||item.Source.IsUasset)
                        {
                            var result=await _api.ImportUassetAsync(item.Source.Input,log,token.Token);
                            lock(_gate){item.Output=result.ReportPath;item.ImportedAssets=result.Added;item.AddedAssetIds=result.AddedAssetIds;item.DuplicateAssets=result.Skipped;item.ExcludedInPlaceAssets=result.ExcludedInPlace;item.ExcludedWithoutAnimationAssets=result.ExcludedWithoutAnimation;item.UnsupportedAssets=result.Unsupported;item.Warnings=result.Warnings;}
                            if(result.Errors.Count>0)throw new IOException(string.Join("\n",result.Errors));
                            if(result.Added+result.Skipped+result.ExcludedInPlace+result.ExcludedWithoutAnimation==0)throw new IOException("没有导入可用的 AnimSequence 动画。\n"+string.Join("\n",result.Warnings)+"\n详细报告："+result.ReportPath);
                            lock(_gate)item.State=result.Added>0?ItemState.Succeeded:ItemState.Skipped;
                        }
                        else
                        {
                            var result=await _api.ImportAsync([item.Source.Input],log,token.Token);
                            lock(_gate){item.ImportedAssets=result.Added;item.AddedAssetIds=result.AddedAssetIds;item.DuplicateAssets=result.Skipped;item.ExcludedInPlaceAssets=result.ExcludedInPlace;item.ExcludedWithoutAnimationAssets=result.ExcludedWithoutAnimation;item.Warnings=result.Warnings;}
                            if(result.Errors.Count>0)throw new IOException(string.Join("\n",result.Errors));
                            if(result.Added+result.Skipped+result.ExcludedInPlace+result.ExcludedWithoutAnimation==0)throw new IOException("No asset was imported; input is missing or unsupported.");
                            lock(_gate)item.State=result.Added>0?ItemState.Succeeded:ItemState.Skipped;
                        }
                        log.Report("Result: "+item.State+(item.Output.Length>0?" -> "+item.Output:""));
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested)
                    {
                        lock(_gate){item.State=ItemState.Cancelled;item.Error="Cancelled";}
                        throw;
                    }
                    catch(Exception ex)
                    {
                        lock(_gate){item.State=ItemState.Failed;item.Error=ex.Message;}
                        File.AppendAllText(item.Log,"\nFAILED: "+ex+"\n");
                    }
                    lock(_gate){job.Message=$"{job.Finished}/{job.Total}: {item.Source.Name} -> {item.State}";Save(job);}
                    Notify(job);
                }
                lock(_gate)job.State=job.Failed>0?JobState.CompletedWithErrors:JobState.Completed;
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested)
            {
                lock(_gate){job.State=JobState.Cancelled;foreach(var item in job.Items.Where(i=>i.State is ItemState.Pending or ItemState.Running))item.State=ItemState.Cancelled;}
            }
            catch(Exception ex)
            {
                lock(_gate){job.State=JobState.Failed;job.Error=ex.Message;foreach(var item in job.Items.Where(i=>i.State is ItemState.Pending or ItemState.Running))item.State=ItemState.Interrupted;}
            }
            finally
            {
                lock(_gate)
                {
                    job.FinishedUtc=DateTime.UtcNow;job.Message=$"{job.State}: {job.Succeeded} succeeded, {job.Skipped} skipped, {job.Failed} failed, {job.Cancelled} cancelled";
                    try{Save(job);}catch(Exception ex){job.Error="Cannot save report: "+ex.Message;job.State=JobState.Failed;}
                    _activeId=null;_activeToken=null;token.Dispose();_completion[job.Id].TrySetResult(Clone(job));
                }
                Notify(job);
            }
        }
    }
    private JobRecord Find(string id){BatchPlanner.ValidateId(id);return _jobs.SingleOrDefault(j=>j.Id==id)??throw new KeyNotFoundException("Job not found: "+id);}
    private static JobRecord Clone(JobRecord job)=>JsonSerializer.Deserialize<JobRecord>(JsonSerializer.Serialize(job,BatchPlanner.JsonOptions),BatchPlanner.JsonOptions)!;
    private void Save(JobRecord job)=>BatchPlanner.AtomicJson(job.ReportPath,job);
    private void Notify(JobRecord job)
    {
        JobUpdate update;lock(_gate)update=new(job.Id,job.State,job.Finished,job.Total,job.Message);
        if(Changed==null)return;
        foreach(Action<JobUpdate> subscriber in Changed.GetInvocationList())try{subscriber(update);}catch{/* Observers must not fail a conversion. */}
    }
    private sealed class ItemLog : IProgress<string>,IDisposable
    {
        private readonly TextWriter _writer;private readonly Action<string> _progress;
        public ItemLog(string path,Action<string> progress){_writer=TextWriter.Synchronized(new StreamWriter(path){AutoFlush=true});_progress=progress;}
        public void Report(string value){_writer.WriteLine(value);_progress(value);}
        public void Dispose()=>_writer.Dispose();
    }
}
