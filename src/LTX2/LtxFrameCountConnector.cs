using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxFrameCountConnector
{
    public static JArray CloneConnection(JArray connection) =>
        connection is null ? null : new JArray(connection[0], connection[1]);

    public static void ApplyToExistingSources(WorkflowGenerator g, JArray framesConnection)
    {
        if (framesConnection is null)
        {
            return;
        }

        g.RunOnNodesOfClass(LtxNodeTypes.EmptyLTXVLatentVideo, (_, videoData) =>
        {
            if (videoData["inputs"] is JObject videoInputs)
            {
                videoInputs["length"] = CloneConnection(framesConnection);
            }
        });
        g.RunOnNodesOfClass(LtxNodeTypes.LTXVEmptyLatentAudio, (_, audioData) =>
        {
            if (audioData["inputs"] is JObject audioInputs)
            {
                SetFrameCountInput(audioInputs, framesConnection);
            }
        });
    }

    public static void SetFrameCountInput(JObject inputs, JArray framesConnection)
    {
        if (inputs is null || framesConnection is null)
        {
            return;
        }

        string key;
        if (inputs.ContainsKey("frames_number"))
        {
            key = "frames_number";
        }
        else if (inputs.ContainsKey("length"))
        {
            key = "length";
        }
        else
        {
            key = "frames_number";
        }
        inputs[key] = CloneConnection(framesConnection);
    }
}
