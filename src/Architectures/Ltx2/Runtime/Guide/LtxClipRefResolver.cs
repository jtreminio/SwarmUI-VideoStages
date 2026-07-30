using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxClipRefResolver(
    WorkflowGenerator g,
    LtxStageGuideMediaResolver guideMediaResolver,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs)
{
    internal List<ResolvedClipRef> ResolveStageClipRefs(
        ClipPlan clip,
        StagePlan stage,
        bool isTextToVideo,
        StageRefStore refStore,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia)
    {
        ArgumentNullException.ThrowIfNull(clip);
        IReadOnlyList<ImageReferencePlan> refs =
            stage.RequireLtx2Payload().FrameReferences;
        List<ResolvedClipRef> resolved = [];
        for (int i = 0; i < refs.Count; i++)
        {
            ImageReferencePlan reference = refs[i];
            if (isTextToVideo
                && reference.SourceKind != ImageReferenceSourceKind.Upload)
            {
                continue;
            }
            WGNodeData raw = ResolveClipRefSourceMedia(reference, refStore, postVideoChain);
            if (raw is null)
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: Stage {stage.StageId} clip reference {i} ({reference.RawSource}) could not be resolved; "
                    + "skipping.");
                continue;
            }

            WGNodeData prepared = PrimaryGuideMatchesScaledSource(g, raw, sourceMedia)
                ? sourceMedia
                : raw;
            resolved.Add(new ResolvedClipRef(prepared, reference, reference.Strength));
        }

        return resolved;
    }

    internal static ResolvedClipRef ExtractPrimaryGuideClipRef(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (clipRef.Reference.FrameOrigin == ImageReferenceFrameOrigin.Start
                && clipRef.Reference.Frame == 1)
            {
                return clipRef;
            }
        }

        return null;
    }

    internal static List<ResolvedClipRef> RemovePrimaryGuideClipRef(
        IReadOnlyList<ResolvedClipRef> clipRefs,
        ResolvedClipRef primaryGuideClipRef)
    {
        if (primaryGuideClipRef is null)
        {
            return [.. clipRefs];
        }

        List<ResolvedClipRef> remaining = [];
        bool removedPrimary = false;
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!removedPrimary && ReferenceEquals(clipRef, primaryGuideClipRef))
            {
                removedPrimary = true;
                continue;
            }

            remaining.Add(clipRef);
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

    private WGNodeData ResolveClipRefSourceMedia(
        ImageReferencePlan reference,
        StageRefStore refStore,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (reference.SourceKind == ImageReferenceSourceKind.Upload)
        {
            return MaterializeUploadedRefImage(reference);
        }

        StageRefStore.StageRef stageRef = reference.SourceKind switch
        {
            ImageReferenceSourceKind.Base => refStore.Base,
            ImageReferenceSourceKind.Refiner => refStore.Refiner,
            ImageReferenceSourceKind.Base2Edit
                when reference.Base2EditStageIndex is int editStage
                    && base2EditPublishedStageRefs.TryGetStageRef(editStage, out StageRefStore.StageRef editRef)
                => editRef,
            _ => null,
        };

        if (stageRef is null)
        {
            if (!string.IsNullOrWhiteSpace(reference.RawSource))
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: Unsupported or unresolved clip reference source '{reference.RawSource}'.");
            }
            return null;
        }

        return guideMediaResolver.ResolveGuideMedia(stageRef, postVideoChain);
    }

    private WGNodeData MaterializeUploadedRefImage(ImageReferencePlan reference)
    {
        ImageFile img = ImageReference.MaterializeUploadedRefImage(
            g,
            reference.InlineData,
            reference.UploadFileName,
            "clip reference image");
        return img is null ? null : g.LoadImage(img, "${videostagesrefimage}", false);
    }
}
