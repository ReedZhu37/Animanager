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
                rate=Math.Min(600,rate*2);
                BakeWorldSpaceProp(motion, rate);
                motion.Player.Play(motion.Manifest.Clip);motion.Player.Advance(0);motion.Player.Pause();
            }
            using var document=new GltfDocument();using var state=new GltfState();
            var error=document.AppendFromScene(motion.Root,state);if(error!=Error.Ok)throw new IOException("Motion scene assembly failed: "+error);
            // Retain source sampling frequency for the Blender bridge.
            var json=state.Json;json["extras"]=new Godot.Collections.Dictionary{{"animanagerSourceFps",rate},{"animanagerIndependentProps",true}};state.Json=json;
            error=document.WriteToFilesystem(state,output);if(error!=Error.Ok)throw new IOException("Motion export failed: "+error);return output;
        }
        finally{if(motion.Root.GetParent()!=null)motion.Root.GetParent().RemoveChild(motion.Root);motion.Root.Free();}
    }

    internal static void BakeWorldSpaceProp(MotionPlayback motion, double rate, CancellationToken ct = default)
    {
        motion.Player.Play(motion.Manifest.Clip);
        motion.Player.Advance(0);
        motion.Player.Pause();
        int intervals = Math.Max(1, (int)Math.Ceiling(motion.Manifest.Duration * rate - 1e-5));
        var poses = new List<(double Time, Transform3D Pose)>(intervals + 1);
        for (int index = 0; index <= intervals; index++)
        {
            ct.ThrowIfCancellationRequested();
            double time = (double)motion.Manifest.Duration * index / intervals;
            motion.Player.Seek(time, true);
            motion.Sync();
            poses.Add((time, motion.Root.GlobalTransform.AffineInverse() * motion.Prop.GlobalTransform));
        }

        var animation = motion.Player.GetAnimation(motion.Manifest.Clip);
        var oldPath = motion.Root.GetPathTo(motion.Prop);
        motion.Player.Stop();
        for (int index = animation.GetTrackCount() - 1; index >= 0; index--)
            if (animation.TrackGetPath(index) == oldPath) animation.RemoveTrack(index);
        motion.Prop.Reparent(motion.Root);
        motion.Prop.Transform = poses[0].Pose;
        var path = motion.Root.GetPathTo(motion.Prop);
        int position = animation.AddTrack(Animation.TrackType.Position3D);
        int rotation = animation.AddTrack(Animation.TrackType.Rotation3D);
        int scale = animation.AddTrack(Animation.TrackType.Scale3D);
        foreach (int track in new[] { position, rotation, scale }) animation.TrackSetPath(track, path);
        foreach (var sample in poses)
        {
            animation.PositionTrackInsertKey(position, sample.Time, sample.Pose.Origin);
            animation.RotationTrackInsertKey(rotation, sample.Time, sample.Pose.Basis.Orthonormalized().GetRotationQuaternion());
            animation.ScaleTrackInsertKey(scale, sample.Time, sample.Pose.Basis.Scale);
        }
        motion.Player.ClearCaches();
    }
}
