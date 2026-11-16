using Animanager.Core.Models;
using Animanager.Core.Storage;

namespace Animanager.Core.Motion;

public static class MotionAssetStore
{
    public static async Task<bool> Import(string path,AssetDatabase db,IStorageProvider storage,CancellationToken ct)
    {
        var manifest=MotionJson.Load(path);string digest=MotionJson.Hash(File.ReadAllBytes(path));
        if(db.HasHash(digest))return false;
        foreach(var item in manifest.Objects.Values.DistinctBy(o=>o.Path))
        {
            ct.ThrowIfCancellationRequested();string target="motion/"+item.Path;
            if(await storage.ExistsAsync(target,ct))
            {
                var existing=await storage.AcquireLocalFileAsync(target,ct);
                if(MotionJson.Hash(await File.ReadAllBytesAsync(existing,ct))!=item.Sha256)throw new IOException("Stored motion object checksum mismatch.");
            }
            else await storage.PutFileAsync(MotionJson.ResolveObject(path,item,false),target,ct);
        }
        string relative="motion/"+digest+".motion.json";
        await storage.PutFileAsync(path,relative,ct);ct.ThrowIfCancellationRequested();
        var asset=new AssetItem{Name=manifest.Name,VirtualPath=relative,SourceFormat=".motion",FileHash=digest,Type="Animation",Tags="Nodachi UE5 separated-motion",Size=new FileInfo(path).Length+new[]{"body","prop"}.Sum(k=>manifest.Objects[k].Bytes)};
        return db.Insert(asset);
    }
    public static void SavePresentation(string path,MotionPresentation presentation)
    {
        var manifest=MotionJson.Load(path);MotionJson.ValidatePresentation(presentation,manifest);
        var previous=MotionJson.Presentation(path,manifest);presentation.PreviousRevision=previous.Revision;presentation.Revision=Guid.NewGuid().ToString("N");
        var history=Path.Combine(Path.GetDirectoryName(path)!,"presentations",Path.GetFileName(path));
        MotionJson.Atomic(Path.Combine(history,presentation.Revision+".json"),presentation);
        MotionJson.Atomic(MotionJson.PresentationPath(path),presentation);
    }
    public static void SetContact(MotionPresentation p,MotionManifest m,double start,double end,string state)
    {
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end>m.Duration||start>=end||state is not ("unknown" or "right" or "left" or "both" or "free"))throw new ArgumentException("Use a valid clip interval and unknown/right/left/both/free.");
        var old=p.Contacts??m.Props[0].Contacts;var result=new List<MotionContact>();
        foreach(var c in old)
        {
            if(c.End<=start||c.Start>=end){result.Add(c);continue;}
            if(c.Start<start)result.Add(new(){Start=c.Start,End=start,State=c.State,Evidence=c.Evidence});
            if(c.End>end)result.Add(new(){Start=end,End=c.End,State=c.State,Evidence=c.Evidence});
        }
        result.Add(new(){Start=start,End=end,State=state,Evidence="user-authored"});p.Contacts=result.OrderBy(c=>c.Start).ToList();MotionJson.ValidatePresentation(p,m);
    }
}
