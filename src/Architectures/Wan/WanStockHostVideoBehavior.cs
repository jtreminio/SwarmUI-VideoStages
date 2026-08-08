using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Execution.Graph;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.Wan;

internal sealed class WanStockHostVideoBehavior(
    WorkflowGenerator generator,
    VideoExecutionPlan plan)
{
    internal WGNodeData ResolveFirstFrame(ClipPlan clip)
    {
        NativeFrameReferencePlan reference =
            clip.RequireWanPayload().FirstFrameReference;
        Image image = NativeFrameReferences.MaterializeUpload(
            generator,
            reference,
            "WAN first-frame reference");
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
        Image authored = NativeFrameReferences.MaterializeUpload(
            generator,
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
                + $"{snapped} — WAN Video generates in steps of {frameGrid} frames.");
        }
        return snapped;
    }

    internal int? ResolvePassthroughFrames(ClipPlan clip, StagePlan stage) =>
        stage.Input == StageInputKind.PreviousStage
            ? generator.CurrentMedia?.Frames
            : ResolveRequestedFrames(clip, stage, sectionId: null);

    internal void BuildNativeLastFrameConditioning(
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        WGNodeData endFrame = generator.LoadImage(
            genInfo.VideoEndFrame,
            "${videostageswanlastframe}",
            false);
        int width = (int)genInfo.Width;
        int height = (int)genInfo.Height;
        using WorkflowBridge bridge = BridgeSync.For(generator);
        ImageScaleNode scaled = ImageScaleReuse.Create(
            bridge,
            endFrame.Path,
            width,
            height,
            crop: "disabled");
        WanFirstLastFrameToVideoNode conditioning = bridge.AddNode(
            new WanFirstLastFrameToVideoNode().With(
                Width: width,
                Height: height,
                Length: frames,
                BatchSize: 1,
                EndImage: scaled.IMAGE));
        conditioning.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        conditioning.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        conditioning.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        if (NeedsClipVisionEndFrame(stage, genInfo))
        {
            CLIPVisionLoaderNode clipLoader = bridge.AddNode(
                new CLIPVisionLoaderNode().With(
                    ClipName: generator.RequireVisionModel(
                        "clip_vision_h.safetensors",
                        "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/"
                            + "split_files/clip_vision/clip_vision_h.safetensors",
                        "64a7ef761bfccbadbaa3da77366aac4185a6c58fa5de5f589b42a65bcc21f161",
                        T2IParamTypes.ClipVisionModel)));
            CLIPVisionEncodeNode encoded = bridge.AddNode(
                new CLIPVisionEncodeNode().With(
                    ClipVision: clipLoader.CLIPVISION,
                    Image: scaled.IMAGE,
                    Crop: CLIPVisionEncodeNode.CropValues.Center));
            conditioning.ClipVisionEndImage.ConnectTo(encoded.CLIPVISIONOUTPUT);
        }
        genInfo.PosCond = conditioning.Positive.ToPath();
        genInfo.NegCond = conditioning.Negative.ToPath();
        generator.CurrentMedia = new(
            conditioning.Latent.ToPath(),
            generator,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat)
        {
            Width = width,
            Height = height,
            Frames = frames,
            FPS = plan.FramesPerSecond,
        };
    }

    /// <summary>Wan 2.1 conditions the end frame through CLIP vision; Wan 2.2's image profile does not.</summary>
    private static bool NeedsClipVisionEndFrame(
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (stage.ResolvedModel.ModelProfileId == WanArchitectureModule.ImageToVideoProfileId)
        {
            return false;
        }
        string compatibilityId = genInfo.VideoModel.ModelClass?.CompatClass?.ID;
        return compatibilityId == T2IModelClassSorter.CompatWan21_14b.ID
            || compatibilityId == T2IModelClassSorter.CompatWan21_1_3b.ID;
    }

    internal ISet<string> CapturePreHostNodeIds(
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (stage.ResolvedModel.ModelProfileId != WanArchitectureModule.Ti2v5bProfileId
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

    /// <summary>Cleanup errors win after success but cannot replace an existing host failure.</summary>
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
