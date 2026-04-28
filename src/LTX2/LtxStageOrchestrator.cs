using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages.LTX2;

internal sealed class LtxStageOrchestrator(
    WorkflowGenerator g,
    LtxStageExecutor stageExecutor,
    RootVideoStageTakeover rootVideoStageTakeover,
    StageGuideMediaHelper stageGuideMediaHelper,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs)
{
    internal bool TryRunLocalLtxPath(
        JsonParser.StageSpec stage,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChain postVideoChain)
    {
        if (!ShouldUseLocalLtxv2Path(genInfo, sourceMedia))
        {
            return false;
        }

        List<ResolvedClipRef> clipRefs = ResolveStageClipRefs(stage, refStore, postVideoChain, sourceMedia);
        ResolvedClipRef primaryGuideClipRef = ExtractPrimaryGuideClipRef(clipRefs);
        clipRefs = RemovePrimaryGuideClipRef(clipRefs, primaryGuideClipRef);
        double guideMergeStrength = 1.0;
        if (primaryGuideClipRef is not null)
        {
            guideMergeStrength = primaryGuideClipRef.Strength;
        }

        bool replacesTextToVideoRoot = rootVideoStageTakeover.ShouldReplaceTextToVideoRootStage(stage);
        bool skipGuideReinjection = primaryGuideClipRef is null
            && (replacesTextToVideoRoot
                || clipRefs is { Count: > 0 }
                || ShouldSkipGeneratedGuideReinjection(stage, sourceMedia, guideReference, genInfo, postVideoChain));

        WGNodeData guideMedia = ResolveLocalGuideMedia(
            primaryGuideClipRef,
            skipGuideReinjection,
            sourceMedia,
            priorOutputPath,
            postVideoChain);

        stageExecutor.RunStage(
            stage,
            genInfo,
            sourceMedia,
            guideMedia,
            skipGuideReinjection,
            applySourceVideoLatent,
            postVideoChain,
            clipRefs,
            guideMergeStrength);
        return true;
    }

    private static bool ShouldUseLocalLtxv2Path(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        return genInfo.VideoModel?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
            && (sourceMedia?.DataType == WGNodeData.DT_VIDEO || sourceMedia?.DataType == WGNodeData.DT_IMAGE);
    }

    private List<ResolvedClipRef> ResolveStageClipRefs(
        JsonParser.StageSpec stage,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain,
        WGNodeData sourceMedia)
    {
        IReadOnlyList<JsonParser.RefSpec> refs = stage.ClipRefs ?? [];
        IReadOnlyList<double> strengths = stage.RefStrengths ?? [];
        if (refs.Count == 0 && !stage.ImageReferenceWasExplicit)
        {
            JsonParser.RefSpec defaultRef = ResolveDefaultImageToVideoRef(refStore);
            if (defaultRef is not null)
            {
                refs = [defaultRef];
                strengths = [1.0];
            }
        }
        List<ResolvedClipRef> resolved = [];
        bool textToVideoRootWorkflow = RootVideoStageTakeover.IsTextToVideoRootWorkflow(g);
        for (int i = 0; i < refs.Count; i++)
        {
            JsonParser.RefSpec spec = refs[i];
            if (textToVideoRootWorkflow && !string.Equals(spec.Source, "Upload", StringComparison.OrdinalIgnoreCase))
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
            if (PrimaryGuideMatchesScaledSource(raw, sourceMedia))
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

    private JsonParser.RefSpec ResolveDefaultImageToVideoRef(StageRefStore refStore)
    {
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _)
            || RootVideoStageTakeover.IsTextToVideoRootWorkflow(g))
        {
            return null;
        }

        if (refStore.Refiner is not null)
        {
            return new JsonParser.RefSpec("Refiner", 1, false, null);
        }
        if (refStore.Base is not null)
        {
            return new JsonParser.RefSpec("Base", 1, false, null);
        }
        return null;
    }

    private static ResolvedClipRef ExtractPrimaryGuideClipRef(IReadOnlyList<ResolvedClipRef> clipRefs)
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

    private static List<ResolvedClipRef> RemovePrimaryGuideClipRef(
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

    private WGNodeData ResolveLocalGuideMedia(
        ResolvedClipRef primaryGuideClipRef,
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChain postVideoChain)
    {
        if (primaryGuideClipRef is null)
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection, sourceMedia, postVideoChain);
        }

        if (primaryGuideClipRef.Image?.Path is JArray guidePath
            && priorOutputPath is not null
            && JToken.DeepEquals(guidePath, priorOutputPath))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        if (PrimaryGuideMatchesScaledSource(primaryGuideClipRef.Image, sourceMedia))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        return stageGuideMediaHelper.PrepareGuideMedia(primaryGuideClipRef.Image, sourceMedia, scaleToSourceSize: true);
    }

    private bool PrimaryGuideMatchesScaledSource(WGNodeData primaryGuideMedia, WGNodeData sourceMedia)
    {
        if (primaryGuideMedia?.Path is not JArray primaryGuidePath
            || sourceMedia?.Path is not JArray sourcePath
            || sourcePath.Count != 2)
        {
            return false;
        }

        if (g.Workflow[$"{sourcePath[0]}"] is not JObject sourceNode
            || !StringUtils.NodeTypeMatches(sourceNode, NodeTypes.ImageScale)
            || sourceNode["inputs"] is not JObject sourceInputs
            || sourceInputs["image"] is not JArray scaledSourceInput)
        {
            return false;
        }

        return JToken.DeepEquals(primaryGuidePath, scaledSourceInput);
    }

    private WGNodeData ResolveDefaultLocalGuideMedia(
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        LtxPostVideoChain postVideoChain)
    {
        if (skipGuideReinjection)
        {
            return null;
        }

        if (postVideoChain is not null
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(sourceMedia, postVideoChain))
        {
            WGNodeData detachedGuideVae = postVideoChain.CreateStageInputVae() ?? g.CurrentVae;
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }

        return sourceMedia;
    }

    private WGNodeData ResolveClipRefSourceMedia(
        JsonParser.RefSpec spec,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain)
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
        else if (ImageReferenceSyntax.TryParseBase2EditStageIndex(src, out int editStage))
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

    private WGNodeData MaterializeUploadedRefImage(JsonParser.RefSpec spec)
    {
        string material = spec.Data?.Trim();
        if (string.IsNullOrWhiteSpace(material))
        {
            material = spec.UploadFileName?.Trim();
        }
        if (string.IsNullOrWhiteSpace(material))
        {
            Logs.Warning("VideoStages: Upload clip reference is missing inline data and a file name.");
            return null;
        }

        if (material.StartsWith("inputs/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("Starred/", StringComparison.OrdinalIgnoreCase))
        {
            if (g.UserInput?.SourceSession is null)
            {
                Logs.Warning(
                    "VideoStages: reference image uses a server-side path but no session is available; "
                    + "cannot load the file.");
                return null;
            }

            try
            {
                material = T2IParamTypes.FilePathToDataString(
                    g.UserInput.SourceSession,
                    material,
                    "for VideoStages reference image");
            }
            catch (SwarmReadableErrorException ex)
            {
                Logs.Warning(
                    $"VideoStages: Could not resolve uploaded reference image path '{material}': {ex.Message}");
                return null;
            }
        }

        try
        {
            ImageFile img = ImageFile.FromDataString(material);
            return g.LoadImage(img, "${videostagesrefimage}", false);
        }
        catch (Exception ex)
        {
            Logs.Warning($"VideoStages: Ignoring invalid clip reference image payload: {ex.Message}");
            return null;
        }
    }

    private bool ShouldSkipGeneratedGuideReinjection(
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        StageRefStore.StageRef guideReference,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        LtxPostVideoChain postVideoChain)
    {
        return stage.ImageReference == "Generated"
            && postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(guideReference?.Media, postVideoChain)
            && !string.IsNullOrWhiteSpace(guideReference?.Vae?.Compat?.ID)
            && guideReference.Vae.Compat.ID == genInfo.VideoModel?.ModelClass?.CompatClass?.ID;
    }
}
