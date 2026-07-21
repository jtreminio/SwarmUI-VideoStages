using ComfyTyped.Core;
using ComfyTyped.Generated;

namespace VideoStages;

/// <summary>Rewires SwarmSaveAnimationWS nodes onto a new video (and optionally audio) output.
/// Callers supply the match predicate and, for audio, an optional per-match hook that runs before the
/// audio reconnect (e.g. to track stale upstream nodes for cleanup).</summary>
internal static class SaveAnimationRetargeter
{
    public static void Retarget(
        WorkflowBridge bridge,
        Func<SwarmSaveAnimationWSNode, bool> matches,
        INodeOutput newImages,
        INodeOutput newAudio,
        bool retargetAudio,
        Action<SwarmSaveAnimationWSNode> onMatch = null)
    {
        foreach (SwarmSaveAnimationWSNode save in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            if (!matches(save))
            {
                continue;
            }

            save.Images.ConnectToUntyped(newImages);
            if (retargetAudio)
            {
                onMatch?.Invoke(save);
                if (!save.Audio.TryConnectToUntyped(newAudio))
                {
                    save.Audio.Clear();
                }
            }
            bridge.SyncNode(save);
        }
    }
}
