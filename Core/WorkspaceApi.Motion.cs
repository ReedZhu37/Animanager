using Animanager.Core.Motion;

namespace Animanager.Core;

public sealed partial class WorkspaceApi
{
    public string MotionPath(string asset)
    {
        var item=Find(asset);if(item.SourceFormat!=".motion")throw new InvalidOperationException("Choose a separated motion asset. Reimport Nodachi into the new motion library first.");
        return Storage.Resolve(item.VirtualPath);
    }
    public object InspectMotion(string asset)
    {
        string path=MotionPath(asset);var m=MotionJson.Load(path);var p=MotionJson.Presentation(path,m);
        return new{asset=Find(asset).Id,manifest=path,m.Clip,m.Duration,m.SourceFps,m.Frames,m.Mapping,m.Provenance,characterBones=161,skinBinds=89,characterPresetSha256=m.Objects["preset"].Sha256,rigSha256=m.Objects["rig"].Sha256,props=m.Props,presentation=p,contacts=p.Contacts??m.Props[0].Contacts,contactEvaluation="metadata only; no IK or retarget",sourceMeshesImported=false};
    }
    public void SetMotionGrip(string asset,float[] position,float[] rotation)
    {
        string path=MotionPath(asset);var m=MotionJson.Load(path);var p=MotionJson.Presentation(path,m);p.GripPosition=position;p.GripRotation=rotation;p.GripConfirmed=true;MotionAssetStore.SavePresentation(path,p);
    }
    public void SetMotionContact(string asset,double start,double end,string state)
    {
        string path=MotionPath(asset);var m=MotionJson.Load(path);var p=MotionJson.Presentation(path,m);MotionAssetStore.SetContact(p,m,start,end,state);MotionAssetStore.SavePresentation(path,p);
    }
    public void SetMotionVisible(string asset,bool visible)
    {
        string path=MotionPath(asset);var m=MotionJson.Load(path);var p=MotionJson.Presentation(path,m);p.Visible=visible;MotionAssetStore.SavePresentation(path,p);
    }
    public void ResetMotionPresentation(string asset)=>MotionAssetStore.SavePresentation(MotionPath(asset),new());
}
