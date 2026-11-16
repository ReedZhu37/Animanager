using System.Security.Cryptography;
using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Storage;
using Animanager.Core.Motion;

namespace Animanager.Core.Pipeline;

public sealed record ImportResult(int Added, int Skipped, List<string> Errors);

public sealed class ImportService(AssetDatabase database, IStorageProvider storage)
{
    public Task<ImportResult> ImportAsync(IEnumerable<string> inputs, IProgress<string>? progress=null, CancellationToken ct=default)
        => Task.Run(async () => {
            int added=0, skipped=0; var errors=new List<string>();
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
                        if(MotionJson.IsMotion(file))
                        {
                            if(await MotionAssetStore.Import(file,database,storage,ct))added++;
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
                        if(database.FindByHash(digest) is {} duplicate)
                        {
                            if(duplicate.Trashed)progress?.Report("资产已在回收站，可从回收站恢复："+duplicate.Name);
                            skipped++;continue;
                        }
                        var prefix="assets/"+digest+"/";
                        foreach(var dependency in dependencies) await storage.PutFileAsync(dependency.Source,prefix+dependency.Relative,ct);
                        var relative=prefix+Path.GetFileName(file);
                        await storage.PutFileAsync(file,relative,ct);
                        var a=new AssetItem {Name=Path.GetFileNameWithoutExtension(file),VirtualPath=relative,SourceFormat=ext,
                            FileHash=digest,Size=new FileInfo(file).Length,Tags="",Type=HasAnimation(file,ext)?"Animation":"Model"};
                        ct.ThrowIfCancellationRequested();
                        if(database.Insert(a)) added++; else skipped++;
                    }
                    catch(OperationCanceledException) {throw;}
                    catch(Exception ex) {errors.Add(Path.GetFileName(file)+": "+ex.Message);}
                }
            }
            return new ImportResult(added,skipped,errors);
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
    private static bool HasAnimation(string file,string ext)
    {
        try
        {
            using var stream=File.OpenRead(file); using var reader=new BinaryReader(stream);
            string json;
            if(ext==".gltf") json=File.ReadAllText(file);
            else if(ext==".glb")
            {
                if(reader.ReadUInt32()!=0x46546C67) return false;
                reader.ReadUInt32();reader.ReadUInt32(); var length=reader.ReadInt32();
                if(length<=0 || length>16*1024*1024 || reader.ReadUInt32()!=0x4E4F534A) return false;
                json=System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
            }
            else return false;
            using var doc=JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("animations",out var animations)&&animations.GetArrayLength()>0;
        }
        catch {return false;}
    }
}
