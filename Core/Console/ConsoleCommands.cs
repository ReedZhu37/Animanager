using Godot;
using Animanager.Core.Pipeline;
using Animanager.UI.Components;
using System.Text.Json;
using Animanager.Core.Jobs;
using Animanager.Core.Storage;
using System.Collections.Concurrent;

namespace Animanager.Core.Console;

/// <summary>Limbo transport adapter. Operations live in WorkspaceApi, not in menu callbacks.</summary>
public partial class ConsoleCommands : Node
{
    private WorkspaceApi? _sessionApi;
    private WorkspaceApi _api=>_sessionApi??throw new InvalidOperationException("Open or create a library first.");
    private Preview3DViewport _preview=null!;
    private Node _console=null!;
    private Action _refresh=null!;
    private Action<Models.AssetItem> _selection=null!;
    private CancellationTokenSource? _job;
    private CancellationTokenSource? _scan;
    private bool _closing;
    private string? _lastPlan;
    private readonly Dictionary<string,(JobState State,int Finished)> _printedProgress=[];
    private readonly ConcurrentQueue<JobUpdate> _updates=new();
    public string? SelectedId { get; private set; }
    public string LastResult { get; private set; }="";
    public bool JobRunning=>_job!=null||_scan!=null||_sessionApi?.LibraryBusy==true;
    public Task PendingTask { get; private set; }=Task.CompletedTask;
    public void Initialize(WorkspaceApi? api,Preview3DViewport preview,Action refresh,Action<Models.AssetItem> selection,
        LibraryManager libraries,Action beforeSwitch,Action<LibrarySession> activate)
    {
        _preview=preview;_refresh=refresh;_selection=selection;_console=GetNode("/root/LimboConsole");
        Bind("app commands",nameof(Commands),"List Animanager hacking commands");
        Bind("app status",nameof(Status),"Show library and Blender status");
        Bind("asset search",nameof(Search),"Search by name or tag: asset search <query>");
        Bind("asset list",nameof(List),"List page (0-based): asset list <page>");
        Bind("asset import",nameof(Import),"Import a file or directory: asset import <path>");
        Bind("asset inspect",nameof(Inspect),"Inspect by exact unique name or full ID");
        Bind("asset tags",nameof(Tags),"Set tags: asset tags <id_or_name> <tags>");
        Bind("asset favorite",nameof(Favorite),"Set favorite: asset favorite <id_or_name> <bool>");
        RegisterLibraryCommands(libraries,beforeSwitch,activate);
        RegisterAssetBrowseCommands();
        Bind("preview load",nameof(Load),"Load an asset by exact unique name or ID");
        Bind("preview play",nameof(Play),"Play the selected clip");
        Bind("preview pause",nameof(Pause),"Pause the selected clip");
        Bind("preview seek",nameof(Seek),"Seek to seconds");
        Bind("preview clip",nameof(Clip),"Select a clip by exact name");
        Bind("preview speed",nameof(Speed),"Playback multiplier, 0.1 to 4");
        Bind("preview bones",nameof(Bones),"Print the bone hierarchy with skeleton:bone IDs");
        RegisterBoneCommands();
        RegisterMotionCommands();
        Bind("preview material",nameof(Material),"Preview material: clay or original (does not affect export)");
        Bind("export fbx",nameof(ExportFbx),"Blender FBX export: export fbx <id_or_name> <folder>");
        Bind("export scene",nameof(ExportScene),"Export loaded preview as GLB: export scene <folder>");
        Bind("export animations",nameof(ExportAnimations),"Export loaded preview as RES: export animations <folder>");
        RegisterConfiguredExportCommands();
        Bind("pipeline blender",nameof(Blender),"Set Blender executable path for this session");
        Bind("job cancel",nameof(Cancel),"Cancel the active console import / conversion");
        Bind("job list",nameof(JobList),"Recent jobs, progress, and report locations");
        Bind("job status",nameof(JobStatus),"Job details: full ID, last or current");
        Bind("job current",nameof(JobCurrent),"Show the active queue job");
        Bind("job report",nameof(JobReport),"Full JSON report: job report <id_or_last>");
        Bind("job cancel_id",nameof(JobCancelId),"Cancel queued or running job by ID");
        Bind("job retry",nameof(JobRetry),"Retry only failed, cancelled or interrupted items");
        Bind("export plan",nameof(ExportPlan),"Snapshot all matches without exporting: export plan <query_or_*> <folder>");
        Bind("export show",nameof(ShowPlan),"Inspect a saved plan: export show <id_or_last>");
        Bind("export run",nameof(RunExportPlan),"Queue a saved FBX, GLB or RES export plan");
        Bind("batch export",nameof(BatchExport),"Queue all matching assets as FBX; use * for all");
        Bind("batch export_favorites",nameof(ExportFavorites),"Queue favorite assets as FBX: <folder>");
        Bind("batch import",nameof(BatchImport),"Recursively scan and queue a file / directory");
        Bind("batch import_list",nameof(ImportList),"Import a JSON array of file / folder paths");
        Bind("batch scan",nameof(Scan),"Build import plan without copying assets");
        Bind("batch run",nameof(RunPlan),"Queue a saved import or export plan");
        Bind("baseline import",nameof(Baselines),"Import the Epic official UE4 / UE5 baseline files");
        Bind("uasset status",nameof(UassetStatus),"Independent CUE4Parse importer capabilities and baseline profile");
        Bind("uasset inspect",nameof(UassetInspect),"Read .uasset / ZIP / directory and report support; no assets imported");
        Bind("uasset import",nameof(UassetImport),"Batch import .uasset / ZIP / directory with automatic UE4 / UE5 standard skeleton recognition");
        SetKey("limbo_console_toggle",Key.Quoteleft);SetKey("limbo_auto_complete_reverse",Key.Tab,shift:true);SetKey("limbo_console_search_history",Key.R,ctrl:true);
        _console.Call("add_argument_autocomplete_source","preview load",0,Callable.From(()=>_sessionApi?.Search().Items.Select(a=>a.Name).ToArray()??[]));
        ChangeLibrary(api);
        Info("Ready. Press ` or F12. Run: app commands");
    }
    private static void SetKey(string action,Key key,bool shift=false,bool ctrl=false)
    {
        if(InputMap.HasAction(action))return;InputMap.AddAction(action);InputMap.ActionAddEvent(action,new InputEventKey{Keycode=key,ShiftPressed=shift,CtrlPressed=ctrl});
    }
    private void Bind(string command,string method,string description)=>_console.Call("register_command",new Callable(this,method),command,description);
    private void Write(string value)
    {
        if(_closing)return;
        // Limbo accepts BBCode. Escape data so JSON arrays and filenames such as [walk] stay visible.
        var escaped=string.Concat(value.Select(c=>c switch{'['=>"[lb]",']'=>"[rb]",_=>c.ToString()}));
        _console.Call("print_line",escaped,false);GD.Print(value);
    }
    private void Info(string value){LastResult=value;Write(value);}
    private void Fail(Exception ex){LastResult="ERROR: "+ex.Message;Write(LastResult);}
    private void Guard(Action action){try{action();}catch(Exception ex){Fail(ex);}}
    private void Job(Func<CancellationToken,Task> action)
    {
        if(_job!=null){Info("A job is already running. Use job cancel.");return;}
        _job=new CancellationTokenSource();PendingTask=Run(action,_job);
    }
    private async Task Run(Func<CancellationToken,Task> action,CancellationTokenSource token)
    {
        try{using var activity=_api.BeginActivity();await action(token.Token);}
        catch(OperationCanceledException){Info("Job cancelled; completed imports are retained.");}
        catch(Exception ex){Fail(ex);}
        finally{token.Dispose();_job=null;if(!_closing)_refresh();}
    }
    public void Commands()=>Info("library current | library list | library create <path> <name> | library open <path_or_id>\nlibrary register <path> | library relocate <id> <path> | library check | library rebuild <path>\nlibrary backup <path> | library restore <backup> <destination> | library cache_preview | library cache_clear true\nasset rename <asset> <name> | asset tags_add <ids> <tags> | asset tags_remove <ids> <tags> | asset favorites <ids> <bool>\nasset trash <ids> | asset trash_list * | asset restore <ids> | asset purge <ids> true\ncollection list | collection create <name> | collection add <collection_id> <ids> | collection search <collection_id> *\nmotion inspect <asset> | motion grip <asset> <x,y,z> <degrees_x,y,z>\nmotion contact <asset> <start> <end> <state> | motion sample <seconds>\nmotion visible <asset> <bool> | motion reset <asset>\nuasset status | uasset inspect <path> | uasset import <path>\nasset list 0 | asset search \"Manny\" | asset inspect <name_or_id>\nbatch import <folder> | batch import_list <json> | batch scan <folder>\nexport fbx <name_or_id> <folder> | export plan <query_or_*> <folder>\nexport show last | export run last | batch export <query_or_*> <folder>\nbatch export_favorites <folder> | batch run <plan_id>\njob current | job list | job status last | job report last | job cancel\njob cancel_id <id> | job retry <id_or_last>\npreview load <name> | preview play | preview pause | preview seek 0.5\npreview clip <clip> | preview speed 1.0 | preview bones | preview material clay\nbone tree <query_or_*> | bone select <name_or_skeleton:bone> | bone info | bone focus | bone clear\nbone show true | bone xray true | trail show true | trail world | trail relative <bone> | trail rate 60 | trail status\nbone select_many \"LeftFoot,RightFoot\" | bone add <bone> | bone remove <bone> | bone selection\ntrail pin | trail pins | trail unpin <pin_id> | trail visible <pin_id> <bool> | trail clear_pins\nrootmotion source <root_bone> | rootmotion show true | rootmotion status | rootmotion sample 0.5\nexport scene <folder> | export animations <folder> | baseline import\nUse quotes and forward slashes in paths. Jobs run sequentially; failures do not stop the batch. help <command> shows typed arguments.\n"+ConfiguredExportHelp+"\n"+AssetBrowseHelp);
    public void Status()=>Guard(()=>Info(JsonSerializer.Serialize(new{assets=_sessionApi?.Search().Total??0,library=_sessionApi?.Storage.Root,libraryId=_libraries.Active?.Identity.Id,libraryName=_libraries.Active?.Identity.Name,blender=_sessionApi?.Blender.Executable,fbxExport=_sessionApi?.Blender.Available??false,jobRunning=JobRunning,selected=SelectedId})));
    public void Search(string query)=>Guard(()=>PrintSearch(query,0));
    public void List(int page)=>Guard(()=>PrintSearch("",Math.Max(0,page)));
    private void PrintSearch(string query,int page){var r=_api.Search(query,page:page);Info($"{r.Total} assets / page {page}\n"+string.Join("\n",r.Items.Select(a=>$"{a.Id} | {a.Name} | {a.SourceFormat} | {a.Tags}")));}
    public void Inspect(string asset)=>Guard(()=>Info(JsonSerializer.Serialize(_api.Find(asset))));
    public void Tags(string asset,string tags)=>Guard(()=>{_api.SetTags(asset,tags);_refresh();Info("Tags saved.");});
    public void Favorite(string asset,bool enabled)=>Guard(()=>{_api.SetFavorite(asset,enabled);_refresh();Info("Favorite saved.");});
    public void Import(string path)=>BatchImport(path);
    public void Baselines()=>Import("res://BuiltinAssets/Baselines");
    public void UassetStatus()=>Guard(()=>Info($"CUE4Parse available: {_api.Uasset.Available}\nUE 4.23 raw / UE 5.0 tagged AnimDataModel / UE 5.6 Sequencer Direct FK AnimSequence · automatic UE4 / UE5 standard skeleton recognition\nNodachi: fixed 161-bone character, independent prop tracks, grip and contact metadata (no IK). Source poses; full Skeleton dependency preserved as source data when supplied. Missing standard Skeleton substitutions are reported. Other UE versions / data models, Montage, blending, curves, events, cooked assets and retargeting are unsupported. No Unreal installation required.\nHelper: {_api.Uasset.Executable}"));
    public void UassetImport(string path)=>Guard(()=>StartPlan(_api.Planner.ImportUasset([Expand(path)])));
    public void UassetInspect(string path)=>Job(async ct=>{
        var output=Path.Combine(_api.Storage.Root,"uasset",Guid.NewGuid().ToString("N"));
        var report=await _api.Uasset.ConvertAsync(Expand(path),output,true,null,ct);
        Info($"CUE4Parse: {report.Items.Count(i=>i.Status=="supported")} supported, {report.Unsupported} unsupported, {report.Failed} failed. Profiles: {string.Join(", ",report.Items.Where(i=>i.Status=="supported").GroupBy(i=>i.Profile).Select(g=>g.Key+"="+g.Count()))}. Substituted Skeleton: {report.Items.Count(i=>i.Status=="supported"&&i.DependencySubstituted)}.\nReport: {report.ReportPath}\n"+string.Join("\n",report.Items.Where(i=>i.Status is "unsupported" or "failed").Take(15).Select(i=>i.Source+": "+i.Error)));
    });
    public void Load(string asset)=>Job(async ct=>{
        var a=_api.Find(asset);var path=await _api.Storage.AcquireLocalFileAsync(a.VirtualPath,ct);
        _preview.LoadFile(path);SelectedId=a.Id;var type=_preview.Clips.Length>0?"Animation":"Model";
        if(type!=a.Type){_api.SetAssetType(a.Id,type);a.Type=type;}
        _selection(a);
        Info($"Loaded {a.Name}: {_preview.MeshCount} meshes, {_preview.BoneCount} bones; clips: "+string.Join(", ",_preview.Clips));
    });
    public void Play()=>Guard(()=>{if(!_preview.Playing)_preview.TogglePlay();Info(_preview.Playing?"Playing":"No animation loaded");});
    public void Pause()=>Guard(()=>{if(_preview.Playing)_preview.TogglePlay();Info("Paused");});
    public void Seek(double seconds)=>Guard(()=>{if(!double.IsFinite(seconds)||seconds<0||seconds>_preview.Duration)throw new ArgumentOutOfRangeException(nameof(seconds));_preview.Seek(seconds);Info($"Time {_preview.Playhead:F3}s");});
    public void Clip(string clip)=>Guard(()=>{if(!_preview.Clips.Contains(clip))throw new ArgumentException("Unknown clip");_preview.SelectClip(clip);PendingTask=_preview.Bones.PendingSampling;Info(clip);});
    public void Speed(double value)=>Guard(()=>{if(!double.IsFinite(value)||value<.1||value>4)throw new ArgumentOutOfRangeException(nameof(value));_preview.SetSpeed(value);Info($"Speed {value}");});
    public void Material(string mode)=>Guard(()=>{if(mode is not ("clay" or "original"))throw new ArgumentException("Use clay or original");_preview.SetClay(mode=="clay");Info("Preview material: "+mode);});
    public void Bones()=>Guard(()=>Info(_preview.Bones.Tree()));
    public void ExportFbx(string asset,string folder)=>Guard(()=>StartPlan(_api.Planner.ExportSingle(asset,Expand(folder))));
    public void ExportPlan(string query,string folder)=>Guard(()=>PrintPlan(_api.Planner.Export(query,Expand(folder))));
    public void ShowPlan(string id)=>Guard(()=>PrintPlan(_api.Planner.Load(PlanId(id))));
    public void RunExportPlan(string id)=>Guard(()=>{var plan=_api.Planner.Load(PlanId(id));if(!plan.Kind.IsExport())throw new ArgumentException("Not an export plan; use batch run.");StartPlan(plan);});
    public void RunPlan(string id)=>Guard(()=>StartPlan(_api.Planner.Load(PlanId(id))));
    public void BatchExport(string query,string folder)=>Guard(()=>StartPlan(_api.Planner.Export(query,Expand(folder))));
    public void ExportFavorites(string folder)=>Guard(()=>StartPlan(_api.Planner.Export("*",Expand(folder),true)));
    public void BatchImport(string path)=>StartScan(path,false,true);
    public void ImportList(string path)=>StartScan(path,true,true);
    public void Scan(string path)=>StartScan(path,false,false);
    private void StartScan(string path,bool manifest,bool enqueue)
    {
        if(_scan!=null){Info("An input scan is running. Use job cancel or wait.");return;}
        _scan=new CancellationTokenSource();PendingTask=ScanAsync(path,manifest,enqueue,_scan);
    }
    private async Task ScanAsync(string path,bool manifest,bool enqueue,CancellationTokenSource cancellation)
    {
        BatchPlan? plan=null;
        IDisposable? activity=null;
        try{activity=_api.BeginActivity();Info("Scanning inputs...");plan=manifest?await _api.Planner.ImportManifestAsync(Expand(path),cancellation.Token):await _api.Planner.ImportAsync([Expand(path)],cancellation.Token);}
        catch(OperationCanceledException){Info("Scan cancelled; nothing queued.");}
        catch(Exception ex){Fail(ex);}
        finally{cancellation.Dispose();_scan=null;}
        using var lease=activity;
        if(plan==null||_closing)return;
        try
        {
            PrintPlan(plan);
            if(enqueue){var id=Enqueue(plan);await Observe(id);}
        }
        catch(Exception ex){Fail(ex);}
    }
    private string PlanId(string id)=>id=="last"?_lastPlan??throw new InvalidOperationException("No plan in this session."):id;
    private string JobId(string id)=>id switch{"last"=>_api.Jobs.LatestId??throw new InvalidOperationException("No jobs."),"current"=>_api.Jobs.CurrentId??throw new InvalidOperationException("No active job."),_=>id};
    private void PrintPlan(BatchPlan plan)
    {
        _lastPlan=plan.Id;Info($"Plan {plan.Id} | {plan.Kind} | {plan.Items.Count} items | {plan.IgnoredFiles} other files ignored\nOutput: {plan.OutputFolder}\n"+DescribeExportOptions(plan)+"\n"+
            string.Join("\n",plan.Items.Take(20).Select(i=>$"{i.AssetId} | {i.Name} | {i.Input}"+(i.Error.Length>0?" | ERROR: "+i.Error:"")))+
            (plan.Items.Count>20?$"\n... {plan.Items.Count-20} more in saved plan":"")+$"\nSaved: {Path.Combine(_api.Planner.PlansFolder,plan.Id+".json")}\nNo export files created by planning; filename conflicts are numbered at execution.");
    }
    private string Enqueue(BatchPlan plan)
    {
        _lastPlan=plan.Id;var id=_api.Jobs.Enqueue(plan);Info($"Queued {id}: {plan.Kind}, {plan.Items.Count} items. Use job status {id}");return id;
    }
    private void StartPlan(BatchPlan plan){var id=Enqueue(plan);PendingTask=Observe(id);}
    private async Task Observe(string id)
    {
        try{using var activity=_api.BeginActivity();var result=await _api.Jobs.WaitAsync(id);Info(Summary(result));if(!_closing)_refresh();}
        catch(Exception ex){Fail(ex);}
    }
    private static string Summary(JobRecord r)=>$"{r.Id} | {r.Kind} | {r.State} | {r.Finished}/{r.Total}\nSucceeded {r.Succeeded}; skipped {r.Skipped}; failed {r.Failed}; cancelled {r.Cancelled}\n"+(r.Kind==JobKind.ImportUasset?$"Animations: {r.Items.Sum(i=>i.ImportedAssets)} added, {r.Items.Sum(i=>i.DuplicateAssets)} duplicate, {r.Items.Sum(i=>i.UnsupportedAssets)} unsupported\n":"")+$"Report: {r.ReportPath}";
    private void OnJobChanged(JobUpdate update)=>_updates.Enqueue(update);
    public override void _Process(double delta)
    {
        // Import progress can arrive from worker threads; Limbo / Godot calls stay on the main thread.
        while(_updates.TryDequeue(out var update))PrintProgress(update);
    }
    private void PrintProgress(JobUpdate update)
    {
        if(_closing)return;var state=(update.State,update.Finished);
        if(_printedProgress.TryGetValue(update.Id,out var previous)&&previous==state)return;
        _printedProgress[update.Id]=state;
        Write($"[{update.Id[..8]}] {update.State} {update.Finished}/{update.Total}: {update.Message}");
    }
    public void JobList()=>Guard(()=>Info(string.Join("\n",_api.Jobs.Recent().Select(r=>$"{r.Id} | {r.Kind} | {r.State} | {r.Finished}/{r.Total}"))));
    public void JobStatus(string id)=>Guard(()=>{var r=_api.Jobs.Get(JobId(id));Info(Summary(r)+"\n"+r.Message+(r.Error.Length>0?"\nERROR: "+r.Error:"")+"\n"+string.Join("\n",r.Items.Where(i=>i.Error.Length>0).Take(10).Select(i=>i.Source.Name+": "+i.Error)));});
    public void JobCurrent()=>Guard(()=>{if(_api.Jobs.CurrentId is {} id)JobStatus(id);else Info(_scan!=null?"Scanning inputs":"No active queue job");});
    public void JobReport(string id)=>Guard(()=>Info(JsonSerializer.Serialize(_api.Jobs.Get(JobId(id)),new JsonSerializerOptions{WriteIndented=true})));
    public void JobCancelId(string id)=>Guard(()=>Info(_api.Jobs.Cancel(JobId(id))?"Cancellation requested":"Job has already finished"));
    public void JobRetry(string id)=>Guard(()=>{var next=_api.Jobs.Retry(JobId(id));Info("Retry queued: "+next);PendingTask=Observe(next);});
    public void ExportScene(string folder)=>Guard(()=>{using var activity=_api.BeginActivity();Info("Exported "+ExportService.Export(_preview,Expand(folder),_preview.Model?.Name.ToString()??"scene",false));});
    public void ExportAnimations(string folder)=>Guard(()=>{using var activity=_api.BeginActivity();Info("Exported "+ExportService.Export(_preview,Expand(folder),_preview.Model?.Name.ToString()??"animations",true));});
    public void Blender(string path)=>Guard(()=>{_api.Blender.Configure(Expand(path));Info("Blender configured: "+_api.Blender.Executable);});
    public void Cancel()=>Guard(()=>{if(_scan!=null)_scan.Cancel();else if(_job!=null)_job.Cancel();else if(_api.Jobs.CurrentId is {} id)JobCancelId(id);else Info("No active task. Use job cancel_id to cancel a queued job.");});
    private static string Expand(string path)=>RuntimePaths.Expand(path);
    public void Open()=>_console.Call("open_console");
    public override void _Input(InputEvent ev){if(ev is InputEventKey{Pressed:true,Echo:false,Keycode:Key.F12}){_console.Call("toggle_console");GetViewport().SetInputAsHandled();}}
    public async Task ShutdownAsync(){_closing=true;CancelAssetBrowseCommands();_preview.Bones.Clear();_job?.Cancel();_scan?.Cancel();if(_sessionApi!=null)await _sessionApi.Jobs.ShutdownAsync();await PendingTask;await _maintenanceTask;}
    public override void _ExitTree(){_closing=true;CancelAssetBrowseCommands();_job?.Cancel();_scan?.Cancel();_preview.Bones.Notice-=Write;if(_sessionApi!=null){_sessionApi.Jobs.Changed-=OnJobChanged;_ = _sessionApi.Jobs.ShutdownAsync();}}
}
