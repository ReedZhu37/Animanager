using System.IO.Compression;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Objects.UObject;

namespace Animanager.CueImporter;

internal sealed record SourceAsset(string Relative,Func<byte[]> Read);
internal sealed class AssetCatalog : IDisposable
{
    private ZipArchive? _zip;
    public List<SourceAsset> Assets {get;}=[];
    public AssetCatalog(string input)
    {
        if(File.Exists(input)&&Path.GetExtension(input).Equals(".zip",StringComparison.OrdinalIgnoreCase))
        {
            _zip=ZipFile.OpenRead(input);long total=0;
            foreach(var entry in _zip.Entries.Where(e=>e.FullName.EndsWith(".uasset",StringComparison.OrdinalIgnoreCase)).OrderBy(e=>e.FullName))
            {
                var relative=SafeRelative(entry.FullName);CheckSize(entry.Length);total+=entry.Length;
                if(total>2L*1024*1024*1024||Assets.Count>=10000)throw new IOException("ZIP exceeds import limits.");
                Assets.Add(new(relative,()=>{using var read=entry.Open();using var data=new MemoryStream();read.CopyTo(data);CheckSize(data.Length);return data.ToArray();}));
            }
        }
        else if(Directory.Exists(input))
        {
            foreach(var path in Directory.EnumerateFiles(input,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false}).Where(p=>Path.GetExtension(p).Equals(".uasset",StringComparison.OrdinalIgnoreCase)).Order())
            {var file=path;Assets.Add(new(SafeRelative(Path.GetRelativePath(input,file)),()=>ReadFile(file)));}
        }
        else if(File.Exists(input)&&Path.GetExtension(input).Equals(".uasset",StringComparison.OrdinalIgnoreCase))Assets.Add(new(Path.GetFileName(input),()=>ReadFile(input)));
        else throw new FileNotFoundException("Input must be a .uasset, directory, or ZIP.",input);
    }
    public SourceAsset? Resolve(string reference)
    {
        var package=reference.Split('.')[0];var suffix=package.Replace("/Game/","",StringComparison.Ordinal)+".uasset";
        var exact=Assets.Where(a=>a.Relative.Equals(suffix,StringComparison.OrdinalIgnoreCase)||a.Relative.EndsWith("/"+suffix,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(exact.Length==1)return exact[0];
        var basename=package.Split('/').Last()+".uasset";
        var candidates=Assets.Where(a=>Path.GetFileName(a.Relative).Equals(basename,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(exact.Length>1||candidates.Length>1)throw new NotSupportedException("Ambiguous skeleton dependency: "+reference+". Include the original Content directory structure.");
        return candidates.SingleOrDefault();
    }
    public static Package Load(SourceAsset asset,out byte[] bytes)
    {
        bytes=asset.Read();
        using var header=new FByteArchive(asset.Relative,bytes,new VersionContainer(EGame.GAME_UE5_8));
        var summary=new FPackageFileSummary(header);var engine=summary.SavedByEngineVersion;
        if(engine==null||engine.Major is not (4 or 5)||!Enum.TryParse<EGame>($"GAME_UE{engine.Major}_{engine.Minor}",out var game))throw new NotSupportedException("Missing or unsupported package engine version.");
        if(summary.PackageFlags.HasFlag(EPackageFlags.PKG_FilterEditorOnly)||summary.PackageFlags.HasFlag(EPackageFlags.PKG_UnversionedProperties))throw new NotSupportedException("Cooked / unversioned assets are not supported by this source-animation importer.");
        var archive=new FByteArchive(asset.Relative,bytes,new VersionContainer(game));
        return new Package(archive,null,(FArchive?)null);
    }
    public static string ImportPath(Package package,FPackageIndex? index)
    {
        int dependency=index?.Index??0;var parts=new List<string>();
        while(dependency<0&&parts.Count<32){var import=package.ImportMap[-dependency-1];parts.Insert(0,import.ObjectName.Text);dependency=import.OuterIndex?.Index??0;}
        return string.Join(".",parts);
    }
    private static byte[] ReadFile(string file){CheckSize(new FileInfo(file).Length);return File.ReadAllBytes(file);}
    private static void CheckSize(long size){if(size>64*1024*1024)throw new IOException("Package exceeds 64 MiB limit.");}
    private static string SafeRelative(string value)
    {
        var path=value.Replace('\\','/');
        if(Path.IsPathRooted(path)||path.Split('/').Any(p=>p is ".." or "."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("Unsafe archive path.");
        return path;
    }
    public void Dispose()=>_zip?.Dispose();
}
