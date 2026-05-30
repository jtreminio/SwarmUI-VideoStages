using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages.LTX2;

internal sealed class LtxClipRefResolver(
    WorkflowGenerator g,
    StageGuideMediaHelper stageGuideMediaHelper,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs)
{
    internal List<ResolvedClipRef> ResolveStageClipRefs(
        ClipSpec clip,
        StageSpec stage,
        StageRefStore refStore,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia)
    {
        bool isTextToVideo = g.GetVideoStagesSpec().IsTextToVideo;
        IReadOnlyList<ImageRefSpec> refs = clip.ImageRefs;
        IReadOnlyList<double> strengths = stage.ImageRefStrengths;
        if (refs.Count == 0 && !stage.ImageRefWasExplicit)
        {
            ImageRefSpec defaultRef = ResolveDefaultImageToVideoRef(isTextToVideo, refStore);
            if (defaultRef is not null)
            {
                refs = [defaultRef];
                strengths = [1.0];
            }
        }
        List<ResolvedClipRef> resolved = [];
        for (int i = 0; i < refs.Count; i++)
        {
            ImageRefSpec spec = refs[i];
            if (isTextToVideo
                && !StringUtils.Equals(spec.Source, "Upload"))
            {
                continue;
            }
            double strength = i < strengths.Count
                ? strengths[i]
                : Constants.DefaultStageRefStrength;
            WGNodeData raw = ResolveClipRefSourceMedia(spec, refStore, postVideoChain);
            if (raw is null)
            {
                Logs.Warning(
                    $"VideoStages: Stage {stage.Id} clip reference {i} ({spec.Source}) could not be resolved; "
                    + "skipping.");
                continue;
            }

            WGNodeData prepared;
            if (PrimaryGuideMatchesScaledSource(g, raw, sourceMedia))
            {
                prepared = sourceMedia;
            }
            else
            {
                prepared = stageGuideMediaHelper.PrepareGuideMedia(raw, sourceMedia, scaleToSourceSize: false);
            }
            resolved.Add(new ResolvedClipRef(prepared, spec, strength));
        }

        return resolved;
    }

    internal static ResolvedClipRef ExtractPrimaryGuideClipRef(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!clipRef.Spec.FromEnd && clipRef.Spec.Frame == 1)
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

    private ImageRefSpec ResolveDefaultImageToVideoRef(bool isTextToVideo, StageRefStore refStore)
    {
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _) || isTextToVideo)
        {
            return null;
        }

        if (refStore.Refiner is not null)
        {
            return new ImageRefSpec("Refiner", 1, false, null);
        }
        if (refStore.Base is not null)
        {
            return new ImageRefSpec("Base", 1, false, null);
        }
        return null;
    }

    private WGNodeData ResolveClipRefSourceMedia(
        ImageRefSpec spec,
        StageRefStore refStore,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (StringUtils.Equals(spec.Source, "Upload"))
        {
            return MaterializeUploadedRefImage(spec);
        }

        StageRefStore.StageRef stageRef = null;
        string src = spec.Source?.Trim() ?? "";
        if (StringUtils.Equals(src, "Base"))
        {
            stageRef = refStore.Base;
        }
        else if (StringUtils.Equals(src, "Refiner"))
        {
            stageRef = refStore.Refiner;
        }
        else if (ImageReference.TryParseBase2EditStageIndex(src, out int editStage))
        {
            _ = base2EditPublishedStageRefs.TryGetStageRef(editStage, out stageRef);
        }

        if (stageRef is null)
        {
            if (!string.IsNullOrWhiteSpace(src))
            {
                Logs.Warning($"VideoStages: Unsupported or unresolved clip reference source '{spec.Source}'.");
            }
            return null;
        }

        return stageGuideMediaHelper.ResolveGuideMedia(stageRef, postVideoChain);
    }

    private WGNodeData MaterializeUploadedRefImage(ImageRefSpec spec)
    {
        ImageFile img = ImageReference.MaterializeUploadedRefImage(g, spec, "clip reference image");
        return img is null ? null : g.LoadImage(img, "${videostagesrefimage}", false);
    }
}
