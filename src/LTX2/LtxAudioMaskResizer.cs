using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxAudioMaskResizer(
    WorkflowGenerator g,
    RootVideoStageResizer rootVideoStageResizer)
{
    internal void ApplyRootAudioMaskDimensionsAfterNativeVideo()
    {
        if (!rootVideoStageResizer.TryGetConfiguredRootStageResolution(out int width, out int height))
        {
            return;
        }

        UpdateAllAudioMaskDimensions(width, height);
    }

    internal void ApplyCurrentAudioMaskDimensions(WGNodeData media)
    {
        if (media?.Gen is not WorkflowGenerator generator
            || !media.Width.HasValue
            || !media.Height.HasValue
            || media.Path is not { Count: 2 } mediaPath
            || !generator.Workflow.TryGetValue($"{mediaPath[0]}", out JToken concatToken)
            || concatToken is not JObject concatNode
            || !StringUtils.NodeTypeMatches(concatNode, LtxNodeTypes.LTXVConcatAVLatent)
            || concatNode["inputs"] is not JObject concatInputs
            || concatInputs["audio_latent"] is not JArray audioLatentPath
            || !TryGetSolidMaskInputsForAudioLatentPath(
                generator,
                audioLatentPath,
                out JObject solidMaskInputs))
        {
            return;
        }

        int w = media.Width.Value;
        int h = media.Height.Value;
        solidMaskInputs["width"] = w;
        solidMaskInputs["height"] = h;
    }

    private void UpdateAllAudioMaskDimensions(int width, int height)
    {
        foreach (
            WorkflowNode setMaskNode in WorkflowUtils.NodesOfType(
                g.Workflow,
                NodeTypes.SetLatentNoiseMask))
        {
            if (setMaskNode.Node["inputs"] is not JObject setMaskInputs
                || !IsAudioSamplesFromSetMaskInputs(setMaskInputs)
                || !TryGetSolidMaskInputsFromSetMaskInputs(g, setMaskInputs, out JObject solidMaskInputs))
            {
                continue;
            }

            solidMaskInputs["width"] = width;
            solidMaskInputs["height"] = height;
        }
    }

    private bool IsAudioSamplesFromSetMaskInputs(JObject setMaskInputs)
    {
        if (setMaskInputs["samples"] is not JArray samplesPath
            || samplesPath.Count != 2
            || !g.Workflow.TryGetValue($"{samplesPath[0]}", out JToken samplesToken)
            || samplesToken is not JObject samplesNode)
        {
            return false;
        }

        return StringUtils.NodeTypeMatches(samplesNode, LtxNodeTypes.LTXVAudioVAEEncode)
            || StringUtils.NodeTypeMatches(samplesNode, NodeTypes.VAEEncodeAudio);
    }

    private static bool TryGetSolidMaskInputsForAudioLatentPath(
        WorkflowGenerator g,
        JArray audioLatentPath,
        out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (audioLatentPath is not { Count: 2 }
            || !g.Workflow.TryGetValue($"{audioLatentPath[0]}", out JToken setMaskToken)
            || setMaskToken is not JObject setMaskNode
            || !StringUtils.NodeTypeMatches(setMaskNode, NodeTypes.SetLatentNoiseMask))
        {
            return false;
        }

        if (setMaskNode["inputs"] is not JObject setMaskInputs)
        {
            return false;
        }

        return TryGetSolidMaskInputsFromSetMaskInputs(g, setMaskInputs, out solidMaskInputs);
    }

    private static bool TryGetSolidMaskInputsFromSetMaskInputs(
        WorkflowGenerator g,
        JObject setMaskInputs,
        out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (setMaskInputs["mask"] is not JArray solidMaskPath
            || solidMaskPath.Count != 2
            || !g.Workflow.TryGetValue($"{solidMaskPath[0]}", out JToken solidMaskToken)
            || solidMaskToken is not JObject solidMaskNode
            || !StringUtils.NodeTypeMatches(solidMaskNode, NodeTypes.SolidMask)
            || solidMaskNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        solidMaskInputs = inputs;
        return true;
    }
}
