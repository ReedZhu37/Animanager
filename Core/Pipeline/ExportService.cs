using Godot;
using Animanager.UI.Components;

namespace Animanager.Core.Pipeline;

public static partial class ExportService
{
    public static string Export(Preview3DViewport preview,string folder,string name,bool animationOnly)
    {
        if(preview.Model==null) throw new InvalidOperationException("请先加载一个可预览的资产。");
        Directory.CreateDirectory(folder);
        var safeName=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
        var path=Path.Combine(folder,safeName+(animationOnly?".res":".glb"));
        for(int index=2;File.Exists(path);index++)path=Path.Combine(folder,$"{safeName}_{index}"+(animationOnly?".res":".glb"));
        if(!animationOnly&&preview.Motion is {} motion)return Animanager.Core.Motion.MotionExport.Glb(motion.ManifestPath,path);
        if (!animationOnly && preview.AppliedRetarget != null)
        {
            var copy = (Node3D)preview.Model.Duplicate();
            try
            {
                copy.Transform = Transform3D.Identity;
                foreach (var skeleton in Preview3DViewport.Descendants(copy).OfType<Skeleton3D>()) skeleton.ResetBonePoses();
                foreach (var mesh in Preview3DViewport.Descendants(copy).OfType<MeshInstance3D>())
                    if (!mesh.HasMeta("animanager_prop_visual")) mesh.MaterialOverride = null;
                WriteScene(path, copy, 1 / preview.FrameStep, copy.HasNode(Retarget.RetargetBaker.PropNodeName), preview.Clips.Length);
                return path;
            }
            finally { copy.Free(); }
        }
        Error error;
        if(animationOnly)
        {
            if(preview.Player==null || preview.Clips.Length==0) throw new InvalidOperationException("当前资产没有可导出的动画。");
            using var library=new AnimationLibrary();
            foreach(var clip in preview.Clips)
            {
                var key=clip.Replace('/','_').Replace(':','_').Replace('.','_');
                var unique=key;for(int i=2;library.HasAnimation(unique);i++)unique=key+"_"+i;
                library.AddAnimation(unique,(Animation)preview.Player.GetAnimation(clip).Duplicate(true));
            }
            error=ResourceSaver.Save(library,path);
        }
        else
        {
            using var document=new GltfDocument();using var state=new GltfState();
            var overrides=Preview3DViewport.Descendants(preview.Model).OfType<MeshInstance3D>().ToDictionary(m=>m,m=>m.MaterialOverride);
            try
            {
                foreach(var mesh in overrides.Keys)mesh.MaterialOverride=null;
                error=document.AppendFromScene(preview.Model,state);
                if(error==Error.Ok)error=document.WriteToFilesystem(state,path);
            }
            finally{foreach(var pair in overrides)pair.Key.MaterialOverride=pair.Value;}
        }
        if(error!=Error.Ok)throw new IOException("导出失败："+error);
        return path;
    }
}
