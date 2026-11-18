using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Pipeline;

namespace Animanager.Core.Jobs;

public sealed class BatchPlanner(WorkspaceApi api)
{
    public string PlansFolder=>Path.Combine(api.Storage.Root,"jobs","plans");
    internal static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};

    public BatchPlan Export(string query, string folder, bool favorites = false) =>
        Export(query, folder, JobKind.ExportFbx, null, favorites);

    public BatchPlan Export(string query, string folder, JobKind kind, ExportOptions? options, bool favorites = false) =>
        ExportAssets(api.Database.SearchAll(query == "*" ? "" : query, favorites ? "Favorites" : "All"),
            folder, kind, options, favorites ? "Favorites: " + query : query);

    public BatchPlan ExportSingle(string idOrName, string folder) =>
        ExportSingle(idOrName, folder, JobKind.ExportFbx, null);

    public BatchPlan ExportSingle(string idOrName, string folder, JobKind kind, ExportOptions? options)
    {
        var asset = api.Find(idOrName);
        return ExportAssets([asset], folder, kind, options, asset.Id);
    }

    public BatchPlan ExportSelection(IEnumerable<string> ids, string folder, JobKind kind, ExportOptions options, string selection)
    {
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        var assets = api.Database.Snapshot().Assets.Where(a => !a.Trashed).ToDictionary(a => a.Id, StringComparer.Ordinal);
        return ExportAssets(requested.Select(id => assets.TryGetValue(id, out var asset) ? asset
            : throw new KeyNotFoundException("所选资产已不在当前库中：" + id)), folder, kind, options, selection);
    }

    private BatchPlan ExportAssets(IEnumerable<AssetItem> assets, string folder, JobKind kind, ExportOptions? options, string selection)
    {
        if (!kind.IsExport()) throw new ArgumentException("请选择 FBX、GLB 或 RES 导出格式。");
        var plan = new BatchPlan
        {
            Kind = kind,
            Selection = selection,
            OutputFolder = ExportFolder(folder),
            ExportOptions = kind.SnapshotExportOptions(options),
            Items = assets.Select(a => new PlanItem { AssetId = a.Id, Name = a.Name, Input = a.VirtualPath }).ToList()
        };
        if (plan.Items.Count == 0) throw new InvalidOperationException("没有可导出的匹配资产。");
        Save(plan);
        return plan;
    }

    internal static string ExportFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("请输入绝对导出目录路径。");
        folder = Path.GetFullPath(folder);
        if (File.Exists(folder)) throw new IOException("导出目标必须是目录。");
        return folder;
    }
    public async Task<BatchPlan> ImportAsync(IEnumerable<string> paths,CancellationToken ct=default)
    {
        using var activity=api.BeginActivity();
        var inputs=paths.ToArray();
        if(inputs.Length>0&&inputs.All(p=>Path.GetExtension(p).ToLowerInvariant() is ".uasset" or ".zip"))return ImportUasset(inputs);
        var plan=await Task.Run(()=>{
            var items=new List<PlanItem>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int ignored=0;
            var uassetInputs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string raw,bool explicitInput,string? directoryRoot=null)
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
                            Visit(child,false,directoryRoot??path);
                        }
                    }
                    else if(Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf" or ".fbx"||Animanager.Core.Motion.MotionJson.IsMotion(path))
                        items.Add(new(){Input=path,Name=Path.GetFileNameWithoutExtension(path)});
                    else if(Path.GetExtension(path).Equals(".uasset",StringComparison.OrdinalIgnoreCase))
                        uassetInputs.Add(directoryRoot??path);
                    else if(Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase))
                        uassetInputs.Add(path);
                    else if(explicitInput)throw new IOException("不支持此格式。请选择 GLB、glTF、FBX、motion.json、uasset、ZIP 或资产文件夹。");
                    else ignored++;
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){items.Add(new(){Input=path,Name=Path.GetFileName(path),Error=ex.Message});}
            }
            // Scan directories before files so overlapping selections keep one dependency root.
            foreach(var path in inputs.OrderBy(p=>Directory.Exists(p)?0:1).ThenBy(p=>p.Length))Visit(path,true);
            foreach(var path in uassetInputs)items.Add(new(){Input=path,Name=Path.GetFileName(path),IsUasset=true});
            return new BatchPlan{Kind=JobKind.Import,Selection=string.Join("; ",inputs),IgnoredFiles=ignored,Items=items};
        },ct);
        ct.ThrowIfCancellationRequested();
        if(plan.Items.Count==0)throw new InvalidOperationException($"没有找到可导入资产（跳过 {plan.IgnoredFiles} 个辅助或不支持的文件）。支持 GLB、glTF、FBX、motion.json、uasset 和 ZIP。请检查所选目录。 ");
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
        var plan=new BatchPlan{Kind=JobKind.ImportUasset,Selection="UE4 / UE5 standard skeletons · "+string.Join("; ",inputs),Items=inputs.Select(p=>new PlanItem{Input=p,Name=Path.GetFileName(p),IsUasset=true}).ToList()};
        Save(plan);return plan;
    }
    public async Task<BatchPlan> ImportManifestAsync(string path,CancellationToken ct=default)
    {
        using var activity=api.BeginActivity();
        var file=Path.GetFullPath(path);
        var inputs=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(file,ct))??throw new IOException("Expected a JSON array of file or folder paths.");
        if(inputs.Length==0||inputs.Any(string.IsNullOrWhiteSpace))throw new IOException("Import manifest must contain non-empty paths.");
        return await ImportAsync(inputs.Select(p=>Path.GetFullPath(p,Path.GetDirectoryName(file)!)),ct);
    }
    public BatchPlan Load(string id)
    {
        ValidateId(id);
        var plan=JsonSerializer.Deserialize<BatchPlan>(File.ReadAllText(Path.Combine(PlansFolder,id+".json")),JsonOptions)??throw new IOException("Invalid plan.");
        if(plan.Version!=1||plan.Id!=id||plan.Items==null||plan.Items.Count==0||plan.Items.Any(item=>item==null)||!Enum.IsDefined(plan.Kind))
            throw new IOException("Unsupported or invalid plan.");
        return plan with { ExportOptions = plan.Kind.SnapshotExportOptions(plan.ExportOptions) };
    }
    public void Save(BatchPlan plan)
    {
        using var activity=api.BeginActivity();
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
