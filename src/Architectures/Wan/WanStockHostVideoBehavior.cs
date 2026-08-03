using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.HostVideo;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Adds Wan bounded references, temporal snapping, final-frame conditioning, and 5B cleanup to
/// the shared stock-host path.
/// </summary>
internal sealed class WanStockHostVideoBehavior(
    WorkflowGenerator generator,
    VideoExecutionPlan plan)
{
    internal WGNodeData ResolveFirstFrame(ClipPlan clip)
    {
        NativeFrameReferencePlan reference =
            clip.RequireWanPayload().FirstFrameReference;
        Image image = MaterializeUpload(reference, "WAN first-frame reference");
        return image is null
            ? null
            : generator.LoadImage(
                image,
                "${videostageswanfirstframe}",
                false);
    }

    internal Image ResolveEndFrame(ClipPlan clip, StagePlan stage)
    {
        bool terminalGenerating = ReferenceEquals(
            stage,
            clip.Stages.LastOrDefault(candidate => !candidate.IsPassthrough));
        if (!terminalGenerating)
        {
            return null;
        }
        // An unusable authored reference must not suppress the request-level end frame.
        Image authored = MaterializeUpload(
            clip.RequireWanPayload().LastFrameReference,
            "WAN final-frame reference");
        if (authored is not null)
        {
            return authored;
        }
        return WanVideoEndFramePolicy.ShouldApply(plan, clip, stage)
            ? generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
            : null;
    }

    internal int? ResolveGeneratedFrames(
        ClipPlan clip,
        StagePlan stage,
        int sectionId)
    {
        int? requested = ResolveRequestedFrames(clip, stage, sectionId);
        if (requested is not int frames)
        {
            return null;
        }
        int frameGrid = stage.ResolvedModel.FrameGrid;
        int snapped = StaticGeneratedFrameGrid.SnapDown(
            frames,
            frameGrid,
            stage.ResolvedModel.FrameGridOrigin);
        if (snapped != frames)
        {
            Logs.Info(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} length {frames} snapped to "
                + $"{snapped} — Wan generates in steps of {frameGrid} frames.");
        }
        return snapped;
    }

    internal int? ResolvePassthroughFrames(ClipPlan clip, StagePlan stage) =>
        stage.Input == StageInputKind.PreviousStage
            ? generator.CurrentMedia?.Frames
            : ResolveRequestedFrames(clip, stage, sectionId: null);

    internal void BuildNativeLastFrameConditioning(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        WGNodeData endFrame = generator.LoadImage(
            genInfo.VideoEndFrame,
            "${videostageswanlastframe}",
            false);
        string scaled = generator.CreateNode("ImageScale", new JObject
        {
            ["image"] = endFrame.Path,
            ["width"] = genInfo.Width,
            ["height"] = genInfo.Height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled",
        });
        JArray scaledEnd = [scaled, 0];
        JToken clipVisionEnd = null;
        string compatibilityId =
            genInfo.VideoModel.ModelClass?.CompatClass?.ID;
        bool exactWan22ImageModel = StringUtils.Equals(
            genInfo.VideoModel.ModelClass?.ID,
            WanArchitectureModule.ImageToVideoModelClassId);
        if (!exactWan22ImageModel
            && (compatibilityId == T2IModelClassSorter.CompatWan21_14b.ID
                || compatibilityId
                    == T2IModelClassSorter.CompatWan21_1_3b.ID))
        {
            string targetName = generator.RequireVisionModel(
                "clip_vision_h.safetensors",
                "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/"
                    + "split_files/clip_vision/clip_vision_h.safetensors",
                "64a7ef761bfccbadbaa3da77366aac4185a6c58fa5de5f589b42a65bcc21f161",
                T2IParamTypes.ClipVisionModel);
            string clipLoader = generator.CreateNode(
                "CLIPVisionLoader",
                new JObject
                {
                    ["clip_name"] = targetName,
                });
            string encodedEnd = generator.CreateNode(
                "CLIPVisionEncode",
                new JObject
                {
                    ["clip_vision"] = new JArray(clipLoader, 0),
                    ["image"] = scaledEnd,
                    ["crop"] = "center",
                });
            clipVisionEnd = new JArray(encodedEnd, 0);
        }
        string conditioning = generator.CreateNode(
            "WanFirstLastFrameToVideo",
            new JObject
            {
                ["width"] = genInfo.Width,
                ["height"] = genInfo.Height,
                ["length"] = frames,
                ["positive"] = genInfo.PosCond,
                ["negative"] = genInfo.NegCond,
                ["vae"] = genInfo.Vae.Path,
                ["start_image"] = null,
                ["end_image"] = scaledEnd,
                ["clip_vision_start_image"] = null,
                ["clip_vision_end_image"] = clipVisionEnd,
                ["batch_size"] = 1,
            });
        genInfo.PosCond = [conditioning, 0];
        genInfo.NegCond = [conditioning, 1];
        generator.CurrentMedia = new(
            [conditioning, 2],
            generator,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat)
        {
            Width = (int)genInfo.Width,
            Height = (int)genInfo.Height,
            Frames = frames,
            FPS = plan.FramesPerSecond,
        };
    }

    internal ISet<string> CapturePreHostNodeIds(
        StockHostVideoStagePayload payload,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!string.Equals(
                payload.ModelClassId,
                WanArchitectureModule.Ti2v5bModelClassId,
                StringComparison.OrdinalIgnoreCase)
            || genInfo.StartStep <= 0)
        {
            return null;
        }
        return generator.Workflow
            .Properties()
            .Select(property => property.Name)
            .ToHashSet();
    }

    internal void RunPostHostCleanup(
        ISet<string> preHostNodeIds,
        Exception hostConstructionError)
    {
        if (preHostNodeIds is null)
        {
            return;
        }
        RunPostHostCleanup(
            () => PruneUnusedWan22Latents(preHostNodeIds),
            hostConstructionError);
    }

    /// <summary>
    /// Cleanup failures are authoritative after successful host construction. While a host
    /// exception is already unwinding, cleanup is best-effort so it cannot replace that failure.
    /// </summary>
    internal static void RunPostHostCleanup(
        Action cleanup,
        Exception hostConstructionError)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            cleanup();
        }
        catch (Exception cleanupError) when (hostConstructionError is not null)
        {
            Logs.Warning(
                "VideoStages: failed to prune an unused Wan 5B latent while "
                + $"preserving the original host construction failure: "
                + $"{cleanupError.Message}");
        }
    }

    private int? ResolveRequestedFrames(
        ClipPlan clip,
        StagePlan stage,
        int? sectionId)
    {
        if (clip.Frames is int authored && authored > 0)
        {
            return authored;
        }
        if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
        {
            return stage.Input == StageInputKind.EmptyLatent
                ? generator.UserInput.Get(
                    T2IParamTypes.Text2VideoFrames,
                    81)
                : generator.CurrentMedia?.Frames;
        }
        if (sectionId is int scopedSection)
        {
            return generator.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int scopedFrames,
                sectionId: scopedSection)
                    ? scopedFrames
                    : null;
        }
        return generator.UserInput.TryGet(
            T2IParamTypes.VideoFrames,
            out int hostFrames)
                ? hostFrames
                : null;
    }

    private Image MaterializeUpload(
        NativeFrameReferencePlan reference,
        string descriptor) =>
        NativeFrameReferences.MaterializeUpload(generator, reference, descriptor);

    private void PruneUnusedWan22Latents(ISet<string> preHostNodeIds)
    {
        using WorkflowBridge bridge =
            WorkflowBridge.Create(generator.Workflow);
        string[] unused = [
            .. bridge.Graph.Nodes.Values
                .Where(node =>
                    node.ClassTypeName == "Wan22ImageToVideoLatent"
                    && !preHostNodeIds.Contains(node.Id)
                    && !bridge.Graph
                        .FindInputsConnectedTo(node.FindOutput(0))
                        .Any())
                .Select(node => node.Id),
        ];
        foreach (string nodeId in unused)
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(
                bridge,
                nodeId,
                protectedNodeIds: preHostNodeIds,
                nodeHelpers: generator.NodeHelpers);
        }
    }
}
