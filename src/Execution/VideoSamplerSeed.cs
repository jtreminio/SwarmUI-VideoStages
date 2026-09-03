using System.Globalization;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Execution;

internal static class VideoSamplerSeed
{
    internal const string MetadataKey = "videostages_source_sampler_seed";

    internal static long FromSourceSampler(
        WorkflowGenerator generator,
        WGNodeData source,
        long fallback) =>
        FromGraph(generator, source)
        ?? FromMetadata(generator.UserInput)
        ?? fallback;

    internal static void RecordOutputFromMedia(
        WorkflowGenerator generator,
        WGNodeData media)
    {
        if (FromGraph(generator, media) is long seed)
        {
            generator.UserInput.ExtraMeta[MetadataKey] =
                seed.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static long? FromGraph(
        WorkflowGenerator generator,
        WGNodeData source)
    {
        if (source?.Path is not JArray sourcePath)
        {
            return null;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ComfyNode sourceNode = bridge.ResolvePath(sourcePath)?.Node;
        if (sourceNode is null)
        {
            return null;
        }
        SwarmKSamplerNode sampler = sourceNode as SwarmKSamplerNode
            ?? bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(sourceNode);
        return sampler is not null
            && sampler.NoiseSeed.Connection is null
            ? sampler.NoiseSeed.LiteralAsLong()
            : null;
    }

    private static long? FromMetadata(T2IParamInput input)
    {
        if (!input.ExtraMeta.TryGetValue(MetadataKey, out object raw))
        {
            return null;
        }
        return long.TryParse(
            $"{raw}",
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long seed)
                ? seed
                : null;
    }
}
