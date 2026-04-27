using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxAudioMaskResizer
{
    internal static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!RootVideoStageResizer.TryGetConfiguredRootStageResolution(g, out int width, out int height))
        {
            return;
        }

        UpdateAllAudioMaskDimensions(g, width, height);
    }

    internal static void ApplyCurrentAudioMaskDimensions(WGNodeData media)
    {
        if (media?.Gen is not WorkflowGenerator g
            || !media.Width.HasValue
            || !media.Height.HasValue
            || media.Path is not { Count: 2 } mediaPath
            || !g.Workflow.TryGetValue($"{mediaPath[0]}", out JToken concatToken)
            || concatToken is not JObject concatNode
            || $"{concatNode["class_type"]}" != LtxNodeTypes.LTXVConcatAVLatent
            || concatNode["inputs"] is not JObject concatInputs
            || concatInputs["audio_latent"] is not JArray audioLatentPath
            || !TryGetSolidMaskInputsForAudioLatentPath(g, audioLatentPath, out JObject solidMaskInputs))
        {
            return;
        }

        solidMaskInputs["width"] = media.Width.Value;
        solidMaskInputs["height"] = media.Height.Value;
    }

    private static void UpdateAllAudioMaskDimensions(WorkflowGenerator g, int width, int height)
    {
        foreach (WorkflowNode setMaskNode in WorkflowUtils.NodesOfType(g.Workflow, NodeTypes.SetLatentNoiseMask))
        {
            if (!IsAudioNoiseMaskNode(g, setMaskNode.Node)
                || !TryGetSolidMaskInputsForSetMaskNode(g, setMaskNode.Node, out JObject solidMaskInputs))
            {
                continue;
            }

            solidMaskInputs["width"] = width;
            solidMaskInputs["height"] = height;
        }
    }

    private static bool IsAudioNoiseMaskNode(WorkflowGenerator g, JObject setMaskNode)
    {
        if (setMaskNode["inputs"] is not JObject inputs
            || inputs["samples"] is not JArray samplesPath
            || samplesPath.Count != 2
            || !g.Workflow.TryGetValue($"{samplesPath[0]}", out JToken samplesToken)
            || samplesToken is not JObject samplesNode)
        {
            return false;
        }

        string classType = $"{samplesNode["class_type"]}";
        return classType == LtxNodeTypes.LTXVAudioVAEEncode || classType == "VAEEncodeAudio";
    }

    private static bool TryGetSolidMaskInputsForAudioLatentPath(WorkflowGenerator g, JArray audioLatentPath, out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (audioLatentPath is not { Count: 2 }
            || !g.Workflow.TryGetValue($"{audioLatentPath[0]}", out JToken setMaskToken)
            || setMaskToken is not JObject setMaskNode
            || $"{setMaskNode["class_type"]}" != NodeTypes.SetLatentNoiseMask)
        {
            return false;
        }

        return TryGetSolidMaskInputsForSetMaskNode(g, setMaskNode, out solidMaskInputs);
    }

    private static bool TryGetSolidMaskInputsForSetMaskNode(WorkflowGenerator g, JObject setMaskNode, out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (setMaskNode["inputs"] is not JObject setMaskInputs
            || setMaskInputs["mask"] is not JArray solidMaskPath
            || solidMaskPath.Count != 2
            || !g.Workflow.TryGetValue($"{solidMaskPath[0]}", out JToken solidMaskToken)
            || solidMaskToken is not JObject solidMaskNode
            || $"{solidMaskNode["class_type"]}" != NodeTypes.SolidMask
            || solidMaskNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        solidMaskInputs = inputs;
        return true;
    }
}
