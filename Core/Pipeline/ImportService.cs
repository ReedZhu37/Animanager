using System.Security.Cryptography;
using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Storage;
using Animanager.Core.Motion;

namespace Animanager.Core.Pipeline;

public sealed record ImportResult(int Added, int Skipped, List<string> Errors)
{
    public string[] AddedAssetIds { get; init; } = [];
    public int ExcludedInPlace { get; init; }
    public int ExcludedWithoutAnimation { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ImportService(AssetDatabase database, IStorageProvider storage)
{
    public Task<ImportResult> ImportAsync(IEnumerable<string> inputs, IProgress<string>? progress=null, CancellationToken ct=default)
        => Task.Run(async () => {
            int skipped=0, excludedInPlace=0, excludedWithoutAnimation=0;
            var addedAssetIds = new List<string>();
            var errors=new List<string>(); var warnings=new List<string>();
            void SkipInPlace(string file)
            {
                excludedInPlace++;
                progress?.Report("跳过 InPlace 动画 · " + Path.GetFileName(file));
            }
            void SkipWithoutAnimation(string file)
            {
                excludedWithoutAnimation++;
                progress?.Report("跳过无可用动画的文件 · " + Path.GetFileName(file));
            }
            foreach(var input in inputs)
            {
                ct.ThrowIfCancellationRequested();
                IEnumerable<string> files=Directory.Exists(input) ? EnumerateFiles(input) : new[]{input};
                foreach(var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext=Path.GetExtension(file).ToLowerInvariant();
                    if(ext is not (".glb" or ".gltf" or ".fbx")&&!MotionJson.IsMotion(file)) continue;
                    try
                    {
                        progress?.Report("正在入库 · "+Path.GetFileName(file));
                        bool namedInPlace=AnimationDisplacement.IsInPlaceSource(file);
                        if(MotionJson.IsMotion(file))
                        {
                            var manifest=MotionJson.ReadManifest(file);
                            if(namedInPlace||AnimationDisplacement.IsInPlace(manifest))
                            { SkipInPlace(file); continue; }
                            var (id, hasAnimation) = await MotionAssetStore.ImportAssetAsync(file, database, storage, ct);
                            if (!hasAnimation) { SkipWithoutAnimation(file); continue; }
                            if (id != null) addedAssetIds.Add(id);
                            else
                            {
                                skipped++;
                                if(database.FindByHash(MotionJson.Hash(await File.ReadAllBytesAsync(file,ct)))?.Trashed==true)
                                    progress?.Report("资产已在回收站，可从回收站恢复："+Path.GetFileName(file));
                            }
                            continue;
                        }
                        var dependencies=ReadDependencies(file,ext);
                        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        foreach(var part in new[]{file}.Concat(dependencies.Select(d=>d.Source)))
                        {
                            await using var stream=File.OpenRead(part);
                            var buffer=new byte[81920]; int read;
                            while((read=await stream.ReadAsync(buffer,ct))>0) hash.AppendData(buffer,0,read);
                        }
                        var digest=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                        var duplicate=database.FindByHash(digest);
                        var motion=AnimationDisplacement.InspectScene(file,ct,namedInPlace);
                        if(motion.InPlace) { SkipInPlace(file); continue; }
                        if(!motion.HasAnimation)
                        {
                            SkipWithoutAnimation(file);
                            continue;
                        }
                        if(duplicate!=null)
                        {
                            if(duplicate.Trashed)progress?.Report("资产已在回收站，可从回收站恢复："+duplicate.Name);
                            skipped++;continue;
                        }
                        if(motion.Warning is { } warning)
                            warnings.Add(Path.GetFileName(file)+" · 无法判定位移 · "+warning);
                        var prefix="assets/"+digest+"/";
                        foreach(var dependency in dependencies) await storage.PutFileAsync(dependency.Source,prefix+dependency.Relative,ct);
                        var relative=prefix+Path.GetFileName(file);
                        await storage.PutFileAsync(file,relative,ct);
                        var a=new AssetItem {Name=Path.GetFileNameWithoutExtension(file),VirtualPath=relative,SourceFormat=ext,
                            FileHash=digest,Size=new FileInfo(file).Length,Tags=motion.Tag??"",Type="Animation"};
                        ct.ThrowIfCancellationRequested();
                        var storedPath = await storage.AcquireLocalFileAsync(relative, ct);
                        var timing = StoredDuration.Inspect(digest, storedPath,
                            () => Browse.AssetAttributeReader.Read(storedPath, ext, ct).Duration);
                        if(database.Insert(a, timing)) addedAssetIds.Add(a.Id); else skipped++;
                    }
                    catch(OperationCanceledException) {throw;}
                    catch(Exception ex) {errors.Add(Path.GetFileName(file)+": "+ex.Message);}
                }
            }
            return new ImportResult(addedAssetIds.Count,skipped,errors){AddedAssetIds=addedAssetIds.ToArray(),ExcludedInPlace=excludedInPlace,
                ExcludedWithoutAnimation=excludedWithoutAnimation,Warnings=warnings};
        },ct);

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        var options=new EnumerationOptions{RecurseSubdirectories=false,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint};
        var files=Directory.EnumerateFiles(folder,"*",options).ToArray();bool bundle=files.Any(MotionJson.IsMotion);
        foreach(var file in files)yield return file;
        foreach(var child in Directory.EnumerateDirectories(folder,"*",options))
        {
            if(bundle&&Path.GetFileName(child).Equals("objects",StringComparison.OrdinalIgnoreCase))continue;
            foreach(var file in EnumerateFiles(child))yield return file;
        }
    }
    internal static List<(string Source,string Relative)> ReadDependencies(string file,string ext)
    {
        var result=new List<(string,string)>();
        if(ext!=".gltf") return result;
        using var doc=JsonDocument.Parse(File.ReadAllText(file));
        var root=Path.GetFullPath(Path.GetDirectoryName(file)!)+Path.DirectorySeparatorChar;
        foreach(var name in new[]{"buffers","images"})
        {
            if(!doc.RootElement.TryGetProperty(name,out var array)) continue;
            foreach(var entry in array.EnumerateArray())
            {
                if(!entry.TryGetProperty("uri",out var value)) continue;
                var uri=value.GetString()!;
                if(uri.StartsWith("data:")) continue;
                var relative=Uri.UnescapeDataString(uri).Replace('/',Path.DirectorySeparatorChar);
                var path=Path.GetFullPath(Path.Combine(root,relative));
                if(Path.IsPathRooted(relative)||relative.Contains(':')||!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("glTF 依赖必须位于文件所在目录内；请先打包为 GLB。");
                if(!File.Exists(path)) throw new FileNotFoundException("缺少 glTF 依赖: "+relative);
                LocalStorageProvider.RequireLocalPath(path);
                result.Add((path,relative));
            }
        }
        return result.Distinct().ToList();
    }
}
