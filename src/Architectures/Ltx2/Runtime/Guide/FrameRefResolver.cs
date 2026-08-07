using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class FrameRefResolver(
    WorkflowGenerator g,
    LtxStageGuideMediaResolver guideMediaResolver)
{
    internal List<ResolvedFrameRef> ResolveStageFrameRefs(
        ClipPlan clip,
        StagePlan stage,
        bool isTextToVideo,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain,
        WGNodeData sourceMedia,
        int incomingHandleFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(clip);
        IReadOnlyList<FrameRefPlan> refs =
            stage.RequireLtx2Payload().FrameReferences;
        List<ResolvedFrameRef> resolved = [];
        for (int i = 0; i < refs.Count; i++)
        {
            FrameRefPlan reference = refs[i];
            if (incomingHandleFrames > 0
                && reference.FrameOrigin == FrameRefEdge.Start)
            {
                reference = reference with
                {
                    Frame = reference.Frame + incomingHandleFrames,
                };
            }
            if (isTextToVideo
                && reference.SourceKind != FrameRefSourceKind.Upload)
            {
                continue;
            }
            WGNodeData raw = ResolveFrameRefSourceMedia(reference, refStore, postVideoChain);
            if (raw is null)
            {
                RequestWarnings.Track(
                    g.UserInput,
                    $"VideoStages: Stage {stage.StageId} frame reference {i} ({reference.RawSource}) could not be resolved; "
                    + "skipping.");
                continue;
            }

            WGNodeData prepared = PrimaryGuideMatchesScaledSource(g, raw, sourceMedia)
                ? sourceMedia
                : raw;
            resolved.Add(new ResolvedFrameRef(prepared, reference, reference.Strength));
        }

        return resolved;
    }

    internal static ResolvedFrameRef ExtractPrimaryGuideFrameRef(IReadOnlyList<ResolvedFrameRef> frameRefs)
    {
        foreach (ResolvedFrameRef frameRef in frameRefs)
        {
            if (frameRef.Reference.IsOpeningFrame)
            {
                return frameRef;
            }
        }

        return null;
    }

    internal static List<ResolvedFrameRef> RemovePrimaryGuideFrameRef(
        IReadOnlyList<ResolvedFrameRef> frameRefs,
        ResolvedFrameRef primaryGuideFrameRef)
    {
        if (primaryGuideFrameRef is null)
        {
            return [.. frameRefs];
        }

        List<ResolvedFrameRef> remaining = [];
        bool removedPrimary = false;
        foreach (ResolvedFrameRef frameRef in frameRefs)
        {
            if (!removedPrimary && ReferenceEquals(frameRef, primaryGuideFrameRef))
            {
                removedPrimary = true;
                continue;
            }

            remaining.Add(frameRef);
        }

        return remaining;
    }

    internal static bool PrimaryGuideMatchesScaledSource(
        WorkflowGenerator g,
        WGNodeData primaryGuideMedia,
        WGNodeData sourceMedia)
    {
        if (primaryGuideMedia?.Path is not JArray { Count: 2 } primaryGuidePath
            || sourceMedia?.Path is not JArray { Count: 2 } sourcePath)
        {
            return false;
        }

        if (WorkflowBridge.Create(g.Workflow).NodeAt<ImageScaleNode>(sourcePath)
            is not ImageScaleNode scale
            || scale.Image.Connection is not INodeOutput scaleSource)
        {
            return false;
        }

        return scaleSource.Node.Id == $"{primaryGuidePath[0]}"
            && scaleSource.SlotIndex == (int)primaryGuidePath[1];
    }

    private WGNodeData ResolveFrameRefSourceMedia(
        FrameRefPlan reference,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain)
    {
        if (reference.SourceKind == FrameRefSourceKind.Upload)
        {
            return GetRefImage(reference);
        }

        StageRefStore.StageRef stageRef = reference.SourceKind switch
        {
            FrameRefSourceKind.Base => refStore.Base,
            FrameRefSourceKind.Refiner => refStore.Refiner,
            FrameRefSourceKind.Base2Edit
                when reference.Base2EditStageIndex is int editStage
                    && refStore.TryGetBase2EditStageRef(editStage, out StageRefStore.StageRef editRef)
                => editRef,
            _ => null,
        };

        if (stageRef is null)
        {
            if (!string.IsNullOrWhiteSpace(reference.RawSource))
            {
                RequestWarnings.Track(
                    g.UserInput,
                    $"VideoStages: Unsupported or unresolved frame reference source '{reference.RawSource}'.");
            }
            return null;
        }

        return guideMediaResolver.ResolveGuideMedia(stageRef, postVideoChain);
    }

    private WGNodeData GetRefImage(FrameRefPlan reference)
    {
        ImageFile img = UploadedMedia.GetRefImage(
            g.UserInput,
            reference.InlineData,
            reference.UploadFileName,
            "frame reference image");
        return g.LoadImage(img, "${videostagesrefimage}", false);
    }
}
