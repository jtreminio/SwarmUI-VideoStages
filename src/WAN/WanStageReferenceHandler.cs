using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages.WAN;

internal static class WanStageReferenceHandler
{
    internal sealed record WanGuideResolution(WGNodeData StartRaw, WGNodeData EndRaw);

    internal static WanGuideResolution TryResolveClipRefs(
        WorkflowGenerator g,
        StageGuideMediaHelper stageGuideMediaHelper,
        Base2EditPublishedStageRefs base2EditPublishedStageRefs,
        JsonParser.StageSpec stage,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain)
    {
        if (!VideoStageModelCompat.IsWanVideoModel(stage.Model)
            || stage.ClipRefs is not { Count: > 0 })
        {
            return new WanGuideResolution(null, null);
        }

        WGNodeData start = ResolveClipRefSourceMedia(
            g,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs,
            stage.ClipRefs[0],
            refStore,
            postVideoChain);
        WGNodeData end = null;
        if (stage.ClipRefs.Count > 1)
        {
            end = ResolveClipRefSourceMedia(
                g,
                stageGuideMediaHelper,
                base2EditPublishedStageRefs,
                stage.ClipRefs[1],
                refStore,
                postVideoChain);
        }

        return new WanGuideResolution(start, end);
    }

    private static WGNodeData ResolveClipRefSourceMedia(
        WorkflowGenerator g,
        StageGuideMediaHelper stageGuideMediaHelper,
        Base2EditPublishedStageRefs base2EditPublishedStageRefs,
        JsonParser.RefSpec spec,
        StageRefStore refStore,
        LtxPostVideoChain postVideoChain)
    {
        if (StringUtils.Equals(spec.Source, "Upload"))
        {
            return MaterializeUploadedRefImage(g, spec);
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
                Logs.Warning(
                    $"VideoStages: Unsupported or unresolved WAN clip reference source '{spec.Source}'.");
            }
            return null;
        }

        return stageGuideMediaHelper.ResolveGuideMedia(stageRef, postVideoChain);
    }

    private static WGNodeData MaterializeUploadedRefImage(WorkflowGenerator g, JsonParser.RefSpec spec)
    {
        string material = spec.Data?.Trim();
        if (string.IsNullOrWhiteSpace(material))
        {
            material = spec.UploadFileName?.Trim();
        }
        if (string.IsNullOrWhiteSpace(material))
        {
            Logs.Warning("VideoStages: Upload WAN clip reference is missing inline data and a file name.");
            return null;
        }

        if (material.StartsWith("inputs/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("Starred/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                material = T2IParamTypes.FilePathToDataString(
                    g.UserInput.SourceSession,
                    material,
                    "for VideoStages WAN reference image");
            }
            catch (SwarmReadableErrorException ex)
            {
                Logs.Warning(
                    $"VideoStages: Could not resolve uploaded WAN reference image path '{material}': "
                    + ex.Message);
                return null;
            }
        }

        try
        {
            ImageFile img = ImageFile.FromDataString(material);
            return g.LoadImage(img, "${videostageswanrefimage}", false);
        }
        catch (Exception ex)
        {
            Logs.Warning($"VideoStages: Ignoring invalid WAN clip reference image payload: {ex.Message}");
            return null;
        }
    }
}
