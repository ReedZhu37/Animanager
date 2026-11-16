using Godot;
using System.Globalization;
using Animanager.Core.Motion;
using System.Text.Json;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private void RegisterMotionCommands()
    {
        Bind("motion inspect",nameof(MotionInspect),"Separated motion: source, preset hashes, grip and contact metadata. <asset>");
        Bind("motion grip",nameof(MotionGrip),"Save visual grip offset in meters and Euler degrees: <asset> <x,y,z> <x,y,z>");
        Bind("motion contact",nameof(MotionContact),"Record contact interval (no IK): <asset> <start_seconds> <end_seconds> <unknown|right|left|both|free>");
        Bind("motion visible",nameof(MotionVisible),"Save prop visibility: <asset> <bool>");
        Bind("motion reset",nameof(MotionReset),"Reset presentation; preserve source tracks and revision history. <asset>");
        Bind("motion sample",nameof(MotionSample),"Seek selected motion and print independent prop world pose/contact: <seconds>");
    }
    public void MotionInspect(string asset)=>Guard(()=>Info(JsonSerializer.Serialize(_api.InspectMotion(asset),MotionJson.Options)));
    private static Vector3 Triple(string text)
    {
        var values=text.Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();if(values.Length!=3||values.Any(v=>!float.IsFinite(v)))throw new ArgumentException("Use three finite comma-separated numbers.");return new(values[0],values[1],values[2]);
    }
    public void MotionGrip(string asset,string position,string rotation)=>Guard(()=>
    {
        var p=Triple(position);var q=Quaternion.FromEuler(Triple(rotation)*Mathf.Pi/180).Normalized();_api.SetMotionGrip(asset,[p.X,p.Y,p.Z],[q.X,q.Y,q.Z,q.W]);
        Info("Grip saved; motion keyframes and character preset unchanged.");ReloadMotion(asset);
    });
    public void MotionContact(string asset,double start,double end,string state)=>Guard(()=>{_api.SetMotionContact(asset,start,end,state);Info("Contact interval saved (observe-only; no IK applied).");ReloadMotion(asset);});
    public void MotionVisible(string asset,bool visible)=>Guard(()=>{_api.SetMotionVisible(asset,visible);Info("Prop visibility saved.");ReloadMotion(asset);});
    public void MotionReset(string asset)=>Guard(()=>{_api.ResetMotionPresentation(asset);Info("Presentation reset; original tracks retained.");ReloadMotion(asset);});
    private void ReloadMotion(string asset){if(_api.Find(asset).Id==SelectedId)Load(asset);}
    public void MotionSample(double seconds)=>Guard(()=>
    {
        var m=_preview.Motion??throw new InvalidOperationException("Load a separated motion asset first.");
        if(!double.IsFinite(seconds)||seconds<0||seconds>_preview.Duration)throw new ArgumentOutOfRangeException(nameof(seconds));
        _preview.Seek(seconds);m.Sync();var world=m.Prop.GlobalTransform;var q=world.Basis.Orthonormalized().GetRotationQuaternion();var tip=m.Grip.GlobalTransform*new Vector3(0,0,.8f);
        Info(JsonSerializer.Serialize(new{time=seconds,prop=m.Manifest.Props[0].Id,anchor=m.Manifest.Props[0].Anchor,contact=m.Contact,position=new[]{world.Origin.X,world.Origin.Y,world.Origin.Z},rotation=new[]{q.X,q.Y,q.Z,q.W},diagnosticTip=new[]{tip.X,tip.Y,tip.Z},gripConfirmed=m.Presentation.GripConfirmed,characterBones=_preview.BoneCount},MotionJson.Options));
    });
}
