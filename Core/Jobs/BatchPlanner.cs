using System.Text.Json;

namespace Animanager.Core.Jobs;

public sealed class BatchPlanner(WorkspaceApi api)
{
    public string PlansFolder=>Path.Combine(api.Storage.Root,"jobs","plans");
    internal static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};

    public BatchPlan Export(string query,string folder,bool favorites=false)
    {
        if(!Path.IsPathFullyQualified(folder))throw new IOException("Export folder must be an absolute path.");
        folder=Path.GetFullPath(folder);
        if(File.Exists(folder))throw new IOException("Export destination is a file, not a folder.");
        var assets=api.Database.SearchAll(query=="*"?"":query,favorites?"Favorites":"All");
        if(assets.Count==0)throw new InvalidOperationException("No assets matched; no export job was created.");
        var plan=new BatchPlan{Kind=JobKind.ExportFbx,Selection=favorites?"Favorites: "+query:query,OutputFolder=folder,
            Items=assets.Select(a=>new PlanItem{AssetId=a.Id,Name=a.Name,Input=a.VirtualPath}).ToList()};
        Save(plan);return plan;
    }
    public BatchPlan ExportSingle(string idOrName,string folder)
    {
        if(!Path.IsPathFullyQualified(folder)||File.Exists(folder))throw new IOException("Export destination must be an absolute folder path.");
        var a=api.Find(idOrName);
        var plan=new BatchPlan{Kind=JobKind.ExportFbx,Selection=a.Id,OutputFolder=Path.GetFullPath(folder),
            Items=[new(){AssetId=a.Id,Name=a.Name,Input=a.VirtualPath}]};
        Save(plan);return plan;
    }
    public async Task<BatchPlan> ImportAsync(IEnumerable<string> paths,CancellationToken ct=default)
    {
        var inputs=paths.ToArray();
        if(inputs.Length>0&&inputs.All(p=>Path.GetExtension(p).ToLowerInvariant() is ".uasset" or ".zip"))return ImportUasset(inputs);
        var plan=await Task.Run(()=>{
            var items=new List<PlanItem>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int ignored=0;
            void Visit(string raw,bool explicitInput)
            {
                ct.ThrowIfCancellationRequested();
                string path;
                try{path=Path.GetFullPath(raw);}catch(Exception ex){items.Add(new(){Input=raw,Name=raw,Error=ex.Message});return;}
                if(!visited.Add(path))return;
                try
                {
                    if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("Input does not exist: "+path);
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Skipped reparse point to avoid recursive links.");
                    if(Directory.Exists(path))
                    {
                        var manifests=Directory.GetFiles(path,"*.motion.json");
                        foreach(var child in Directory.EnumerateFileSystemEntries(path).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))
                        {
                            if(manifests.Length>0&&Directory.Exists(child)&&Path.GetFileName(child).Equals("objects",StringComparison.OrdinalIgnoreCase))continue;
                            Visit(child,false);
                        }
                    }
                    else if(Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf" or ".fbx"||Animanager.Core.Motion.MotionJson.IsMotion(path))
                        items.Add(new(){Input=path,Name=Path.GetFileNameWithoutExtension(path)});
                    else if(explicitInput)throw new IOException("Unsupported format; use GLB, glTF or FBX.");
                    else ignored++;
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){items.Add(new(){Input=path,Name=Path.GetFileName(path),Error=ex.Message});}
            }
            foreach(var path in inputs)Visit(path,true);
            return new BatchPlan{Kind=JobKind.Import,Selection=string.Join("; ",inputs),IgnoredFiles=ignored,Items=items};
        },ct);
        ct.ThrowIfCancellationRequested();
        if(plan.Items.Count==0)throw new InvalidOperationException($"No supported assets found ({plan.IgnoredFiles} other files ignored).");
        Save(plan);return plan;
    }
    public BatchPlan ImportUasset(IEnumerable<string> paths)
    {
        var inputs=paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(inputs.Length==0)throw new ArgumentException("No .uasset inputs selected.");
        foreach(var path in inputs)
        {
            if(!Directory.Exists(path)&&(!File.Exists(path)||Path.GetExtension(path).ToLowerInvariant() is not (".uasset" or ".zip")))throw new IOException("Choose a .uasset, ZIP, or asset directory: "+path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse points are not supported.");
        }
        var plan=new BatchPlan{Kind=JobKind.ImportUasset,Selection="UE4 / UE5 standard skeletons · "+string.Join("; ",inputs),Items=inputs.Select(p=>new PlanItem{Input=p,Name=Path.GetFileName(p)}).ToList()};
        Save(plan);return plan;
    }
    public async Task<BatchPlan> ImportManifestAsync(string path,CancellationToken ct=default)
    {
        var file=Path.GetFullPath(path);
        var inputs=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(file,ct))??throw new IOException("Expected a JSON array of file or folder paths.");
        if(inputs.Length==0||inputs.Any(string.IsNullOrWhiteSpace))throw new IOException("Import manifest must contain non-empty paths.");
        return await ImportAsync(inputs.Select(p=>Path.GetFullPath(p,Path.GetDirectoryName(file)!)),ct);
    }
    public BatchPlan Load(string id)
    {
        ValidateId(id);
        var plan=JsonSerializer.Deserialize<BatchPlan>(File.ReadAllText(Path.Combine(PlansFolder,id+".json")),JsonOptions)??throw new IOException("Invalid plan.");
        if(plan.Version!=1||plan.Id!=id||plan.Items.Count==0)throw new IOException("Unsupported or invalid plan.");return plan;
    }
    public void Save(BatchPlan plan)
    {
        ValidateId(plan.Id);Directory.CreateDirectory(PlansFolder);
        AtomicJson(Path.Combine(PlansFolder,plan.Id+".json"),plan);
    }
    internal static void ValidateId(string id){if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Use the complete 32-character plan / job ID.");}
    internal static void AtomicJson<T>(string path,T value)
    {
        var temp=path+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(value,JsonOptions));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
