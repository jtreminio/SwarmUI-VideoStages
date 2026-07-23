using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Orchestrates graph resolution and media ownership for a multi-clip timeline. Boundary planning and
/// graph construction live in dedicated collaborators so this class remains the runner-facing facade.
/// </summary>
internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    public BoundaryBudgetResolution Apply(
        IReadOnlyList<WGNodeData> clipOutputsInOrder,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        if (clipOutputsInOrder is null || clipOutputsInOrder.Count < 2)
        {
            return new(boundaries ?? [], Degraded: false, Reason: null);
        }

        int sumFrames = 0;
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            sumFrames += clip?.Frames ?? 0;
        }

        BoundaryBudgetResolution runtimeBoundaries =
            BoundaryOverlapPlanner.ValidateRuntime(clipOutputsInOrder, boundaries);
        using WorkflowBridge bridge = BridgeSync.For(g);
        List<INodeOutput> videoOutputs = ResolveOutputs(bridge, clipOutputsInOrder.Select(clip => clip?.Path));
        if (videoOutputs.Count != clipOutputsInOrder.Count)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: timeline assembly could resolve only {videoOutputs.Count} of "
                + $"{clipOutputsInOrder.Count} planned clip video outputs.");
        }

        if (runtimeBoundaries.Degraded)
        {
            Logs.Warning(
                $"VideoStages: overlap boundaries degraded to cuts because "
                + $"{runtimeBoundaries.Reason}.");
        }
        BoundaryOverlapPlan overlapPlan =
            BoundaryOverlapPlanner.ToOverlapPlan(runtimeBoundaries.Boundaries);

        INodeOutput mergedVideo = overlapPlan is null
            ? MultiClipVideoGraphAssembler.MergeCut(bridge, videoOutputs)
            : MultiClipVideoGraphAssembler.MergeWithOverlaps(bridge, clipOutputsInOrder, videoOutputs, overlapPlan);

        IReadOnlyList<INodeOutput> audioOutputs =
            MultiClipAudioGraphAssembler.ResolveOrPadTimelineAudio(
                bridge,
                clipOutputsInOrder,
                g.CurrentAudioVae);
        INodeOutput mergedAudio = audioOutputs.Count > 0
            ? MultiClipAudioGraphAssembler.Merge(bridge, clipOutputsInOrder, audioOutputs, overlapPlan)
            : null;

        WGNodeData template = clipOutputsInOrder[0];
        g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(mergedVideo), g, WGNodeData.DT_VIDEO, template.Compat)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = clipOutputsInOrder.All(clip => clip?.Frames is > 0)
                ? sumFrames - (overlapPlan?.RemovedFrames ?? 0)
                : template.Frames,
            FPS = template.FPS
        };
        if (mergedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                WorkflowBridge.ToPath(mergedAudio),
                g,
                WGNodeData.DT_AUDIO,
                template.AttachedAudio?.Compat ?? g.CurrentAudioVae?.Compat);
        }
        return runtimeBoundaries;
    }

    private static List<INodeOutput> ResolveOutputs(WorkflowBridge bridge, IEnumerable<JArray> paths)
    {
        List<INodeOutput> outputs = [];
        foreach (JArray path in paths)
        {
            INodeOutput output = bridge.ResolvePath(path);
            if (output is not null)
            {
                outputs.Add(output);
            }
        }
        return outputs;
    }
}
