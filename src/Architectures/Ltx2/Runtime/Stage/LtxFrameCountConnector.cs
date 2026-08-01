using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal static class LtxFrameCountConnector
{
    public static void ApplyToExistingSources(WorkflowGenerator g, JArray framesConnection)
    {
        if (framesConnection is null)
        {
            return;
        }

        g.RunOnNodesOfClass(EmptyLTXVLatentVideoNode.ClassType, (_, videoData) =>
        {
            if (videoData["inputs"] is JObject videoInputs)
            {
                videoInputs["length"] = framesConnection.DeepClone() as JArray;
            }
        });
        g.RunOnNodesOfClass(LTXVEmptyLatentAudioNode.ClassType, (_, audioData) =>
        {
            if (audioData["inputs"] is JObject audioInputs)
            {
                audioInputs["frames_number"] = framesConnection.DeepClone() as JArray;
            }
        });
    }
}
