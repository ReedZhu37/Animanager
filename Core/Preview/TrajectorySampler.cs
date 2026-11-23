using Godot;
using Animanager.UI.Components;

namespace Animanager.Core.Preview;

internal static class TrajectorySampler
{
    internal static (Node3D Root, AnimationPlayer Player) Create(Node3D stage, Node3D model,
        AnimationPlayer player, PoseSnapshot baseline, string clip)
    {
        var clone = (Node3D)model.Duplicate(0);
        try
        {
            clone.Name = "TrajectorySampler";
            clone.Hide();
            clone.ProcessMode = Node.ProcessModeEnum.Disabled;
            var sampler = clone.GetNode<AnimationPlayer>(model.GetPathTo(player));
            foreach (var mixer in Preview3DViewport.Descendants(clone).OfType<AnimationMixer>()) mixer.Active = false;
            sampler.Autoplay = "";
            sampler.Stop();
            foreach (var library in sampler.GetAnimationLibraryList()) sampler.RemoveAnimationLibrary(library);

            var animation = (Animation)player.GetAnimation(clip).Duplicate(true);
            animation.LoopMode = Animation.LoopModeEnum.None;
            for (int track = 0; track < animation.GetTrackCount(); track++)
                if (animation.TrackGetType(track) is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D))
                    animation.TrackSetEnabled(track, false);
            var libraryCopy = new AnimationLibrary();
            libraryCopy.AddAnimation("sample", animation);
            sampler.AddAnimationLibrary("", libraryCopy);
            sampler.RootMotionTrack = new NodePath("");
            sampler.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            sampler.PlaybackAutoCapture = false;
            sampler.Active = true;
            stage.AddChild(clone);
            baseline.Restore(clone);
            sampler.Play("sample", 0);
            sampler.Advance(0);
            sampler.Pause();
            return (clone, sampler);
        }
        catch
        {
            clone.Free();
            throw;
        }
    }
}
