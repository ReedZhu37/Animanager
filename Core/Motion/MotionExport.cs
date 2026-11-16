using Godot;

namespace Animanager.Core.Motion;

public static class MotionExport
{
    public static string Glb(string manifestPath,string output,bool worldSpaceProp=false)
    {
        var motion=new MotionPlayback(manifestPath);var tree=Engine.GetMainLoop() as SceneTree??throw new InvalidOperationException("Motion export requires the App runtime.");
        try
        {
            tree.Root.AddChild(motion.Root);motion.Player.Play(motion.Manifest.Clip);motion.Player.Advance(0);motion.Player.Pause();motion.Sync();
            double rate=motion.Manifest.SourceFps;
            if(worldSpaceProp)
            {
                // Bake a standalone prop frame for the FBX bridge; its object-axis conversion is normalized there.
                // Stored hand-relative tracks and character preset stay unchanged.
                rate=Math.Min(600,rate*2);int intervals=(int)Math.Ceiling(motion.Manifest.Duration*rate-1e-5);
                var poses=new List<(double Time,Transform3D Pose)>();
                for(int i=0;i<=intervals;i++)
                {
                    double time=motion.Manifest.Duration*i/intervals;motion.Player.Seek(time,true);motion.Sync();
                    poses.Add((time,motion.Root.GlobalTransform.AffineInverse()*motion.Prop.GlobalTransform));
                }
                var animation=motion.Player.GetAnimation(motion.Manifest.Clip);var oldPath=motion.Root.GetPathTo(motion.Prop);
                motion.Player.Stop();
                for(int i=animation.GetTrackCount()-1;i>=0;i--)if(animation.TrackGetPath(i)==oldPath)animation.RemoveTrack(i);
                motion.Prop.Reparent(motion.Root);motion.Prop.Transform=poses[0].Pose;
                var newPath=motion.Root.GetPathTo(motion.Prop);
                int p=animation.AddTrack(Animation.TrackType.Position3D),r=animation.AddTrack(Animation.TrackType.Rotation3D),s=animation.AddTrack(Animation.TrackType.Scale3D);
                foreach(int track in new[]{p,r,s})animation.TrackSetPath(track,newPath);
                foreach(var sample in poses)
                {
                    animation.PositionTrackInsertKey(p,sample.Time,sample.Pose.Origin);
                    animation.RotationTrackInsertKey(r,sample.Time,sample.Pose.Basis.Orthonormalized().GetRotationQuaternion());
                    animation.ScaleTrackInsertKey(s,sample.Time,sample.Pose.Basis.Scale);
                }
                motion.Player.ClearCaches();motion.Player.Play(motion.Manifest.Clip);motion.Player.Advance(0);motion.Player.Pause();
            }
            using var document=new GltfDocument();using var state=new GltfState();
            var error=document.AppendFromScene(motion.Root,state);if(error!=Error.Ok)throw new IOException("Motion scene assembly failed: "+error);
            // Retain source sampling frequency for the Blender bridge.
            var json=state.Json;json["extras"]=new Godot.Collections.Dictionary{{"animanagerSourceFps",rate},{"animanagerIndependentProps",true}};state.Json=json;
            error=document.WriteToFilesystem(state,output);if(error!=Error.Ok)throw new IOException("Motion export failed: "+error);return output;
        }
        finally{if(motion.Root.GetParent()!=null)motion.Root.GetParent().RemoveChild(motion.Root);motion.Root.Free();}
    }
}
