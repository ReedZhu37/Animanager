using System.Numerics;
using System.Text.Json;
using Animanager.Core.Motion;

namespace Animanager.CueImporter;

internal static class MotionVerification
{
    public static int Run(string folder,string legacyLibrary)
    {
        var legacy=new Dictionary<string,string>();
        foreach(var report in Directory.EnumerateFiles(Path.Combine(legacyLibrary,"uasset"),"import-report.json",SearchOption.AllDirectories))
        {
            using var d=JsonDocument.Parse(File.ReadAllText(report));foreach(var item in d.RootElement.GetProperty("items").EnumerateArray())legacy[item.GetProperty("source").GetString()!]=Path.Combine(legacyLibrary,item.GetProperty("path").GetString()!);
        }
        var rows=new List<object>();double maxPosition=0,maxAngle=0,maxTip=0;long samples=0;int animated=0,fallback=0;string? meshHash=null,rigHash=null;int failed=0;
        foreach(var file in Directory.EnumerateFiles(folder,"*.motion.json").Order())
        {
            string error="";long tested=0;double position=0,angle=0,tip=0;
            try
            {
                var m=MotionJson.Load(file);meshHash??=m.Objects["preset"].Sha256;rigHash??=m.Objects["rig"].Sha256;
                if(meshHash!=m.Objects["preset"].Sha256||rigHash!=m.Objects["rig"].Sha256)throw new Exception("Preset changed between clips.");
                var supplied=MotionJson.Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Baseline","UE5_Manny.glb")));
                if(supplied!=meshHash)throw new Exception("Preset bytes changed from official baseline.");
                string Obj(string key)=>MotionJson.ResolveObject(file,m.Objects[key],false);
                var body=MotionGlb.Read(Obj("body"));var prop=MotionGlb.Read(Obj("prop"));var source=MotionGlb.Read(Obj("source"));var old=MotionGlb.Read(legacy[m.Provenance["source"]]);
                var bn=body.ByName;var sn=source.ByName;var on=old.ByName;
                if(bn.ContainsKey("Nodachi_Weapon_R")||bn.Count!=162||prop.Nodes.Length!=1||prop.Channels.Any(c=>c.Node!=0))throw new Exception("Body/prop separation failed.");
                foreach(var key in new[]{"body","prop","source"}){var (json,_)=MotionGlb.Unpack(File.ReadAllBytes(Obj(key)));if(json.ContainsKey("meshes")||json.ContainsKey("skins")||json.ContainsKey("materials"))throw new Exception("Animation stores model data.");}
                void Compare(Matrix4x4 a,Matrix4x4 b)
                {
                    Matrix4x4.Decompose(a,out _,out var aq,out var ap);Matrix4x4.Decompose(b,out _,out var bq,out var bp);
                    position=Math.Max(position,Vector3.Distance(ap,bp));double dot=Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(aq),Quaternion.Normalize(bq))),0,1);angle=Math.Max(angle,2*Math.Acos(dot)*180/Math.PI);
                    foreach(var point in new[]{new Vector3(0,0,.8f),new Vector3(.15f,0,0)})tip=Math.Max(tip,Vector3.Distance(Vector3.Transform(point,a),Vector3.Transform(point,b)));tested++;
                }
                foreach(double t in new[]{0d,m.Duration*.25,m.Duration*.5,m.Duration*.75,m.Duration})
                    foreach(var bone in bn.Keys.Where(n=>n!="MotionCoordinateBasis"))Compare(body.WorldAt(bn[bone],t),old.WorldAt(on[bone],t));
                // Every source frame and interval midpoint: endpoints alone cannot verify a complete spin.
                for(int frame=0;frame<=2*(m.Frames-1);frame++)
                {
                    double t=m.Duration*frame/(2*(m.Frames-1));var combined=prop.LocalAt(0,t)*body.WorldAt(bn["hand_r"],t);
                    Compare(combined,old.WorldAt(on["Nodachi_Weapon_R"],t));Compare(combined,source.WorldAt(sn["Nodachi_Weapon_R"],t));
                }
                if(position>.0001||angle>.1||tip>.0001)throw new Exception($"Tolerance exceeded: {position} m / {angle} deg / {tip} m tip.");
                if(m.Props[0].State=="rest-fallback")fallback++;if(m.Props[0].State=="animated")animated++;
                if(m.Props[0].Contacts.Single().State!="unknown")throw new Exception("Inferred a contact not present in the source.");
            }
            catch(Exception ex){error=ex.ToString();failed++;}
            rows.Add(new{file=Path.GetFileName(file),valid=error.Length==0,samples=tested,position,angle,tip,error});samples+=tested;maxPosition=Math.Max(maxPosition,position);maxAngle=Math.Max(maxAngle,angle);maxTip=Math.Max(maxTip,tip);
            if(rows.Count%25==0)Console.WriteLine($"MOTION_COMPARE {rows.Count}: failures={failed}");
        }
        MotionJson.Atomic(Path.Combine(folder,"motion-validation.json"),new{version=1,comparison="legacy Godot-importable GLB and decoded source; all source keys plus midpoints for weapon, five times for body; no Unreal runtime evaluation",clips=rows.Count,failed,samples,animated,fallback,maxPosition,maxAngle,maxTip,meshHash,rigHash,results=rows});
        Console.WriteLine($"MOTION_VALIDATED clips={rows.Count} failed={failed} samples={samples} position={maxPosition} angle={maxAngle} tip={maxTip}");return failed==0?0:1;
    }
}
