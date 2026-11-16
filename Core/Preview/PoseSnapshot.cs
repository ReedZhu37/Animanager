using Godot;
using Animanager.UI.Components;

namespace Animanager.Core.Preview;

/// <summary>The imported pose, before any clip is applied. Also restores unkeyed channels.</summary>
public sealed class PoseSnapshot
{
    private readonly List<(NodePath Path, Transform3D Transform, Transform3D[]? Bones)> _nodes=[];

    public PoseSnapshot(Node3D root)
    {
        foreach(var node in Preview3DViewport.Descendants(root).OfType<Node3D>())
            _nodes.Add((root.GetPathTo(node),node.Transform,node is Skeleton3D s
                ?Enumerable.Range(0,s.GetBoneCount()).Select(s.GetBonePose).ToArray():null));
    }

    public void Restore(Node3D root)
    {
        foreach(var entry in _nodes)
        {
            var node=root.GetNode<Node3D>(entry.Path);node.Transform=entry.Transform;
            if(node is Skeleton3D s && entry.Bones is {} bones)
                for(int i=0;i<bones.Length;i++)s.SetBonePose(i,bones[i]);
        }
    }
}
