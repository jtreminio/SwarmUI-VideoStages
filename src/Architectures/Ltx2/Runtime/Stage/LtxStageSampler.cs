using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageSampler(WorkflowGenerator g)
{
    internal void Execute(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame)
    {
        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string explicitSampler = g.UserInput.Get(
            ComfyUIBackendExtension.SamplerParam,
            null,
            sectionId: genInfo.ContextID,
            includeBase: false);
        string explicitScheduler = g.UserInput.Get(
            ComfyUIBackendExtension.SchedulerParam,
            null,
            sectionId: genInfo.ContextID,
            includeBase: false);

        g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
        LtxAudioMaskResizer.ApplyCurrentAudioMaskDimensions(g.CurrentMedia);
        // Crop audio and video together so preserved retake media stays synchronized.
        new LtxAudioWindowMasker(g).Apply(genInfo, stageFrame);
        string samplerNode = g.CreateKSampler(
            genInfo.Model.Path,
            genInfo.PosCond,
            genInfo.NegCond,
            g.CurrentMedia.Path,
            genInfo.VideoCFG.Value,
            genInfo.Steps,
            genInfo.StartStep,
            10000,
            genInfo.Seed,
            returnWithLeftoverNoise: false,
            addNoise: true,
            sigmin: 0.002,
            sigmax: 1000,
            previews: previewType,
            id: stageFrame.ClaimedSamplerId,
            defsampler: genInfo.DefaultSampler,
            defscheduler: genInfo.DefaultScheduler,
            hadSpecialCond: genInfo.HadSpecialCond,
            explicitSampler: explicitSampler,
            explicitScheduler: explicitScheduler,
            sectionId: genInfo.ContextID
        );

        g.CurrentMedia = g.CurrentMedia.WithPath([samplerNode, 0]);
        g.CurrentMedia.Frames = genInfo.Frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = genInfo.VideoFPS ?? g.CurrentMedia.FPS;

        if (stageFrame.NeedsCropGuidesAfterSampler)
        {
            CropGuidesAfterSampler(genInfo, stageFrame);
        }

    }

    private void CropGuidesAfterSampler(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame)
    {
        bool shouldRestoreAudioVideoLatent =
            g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO;

        // Crop against the original conditioning; audio-reference tokens only guide sampling.
        if (stageFrame.AudioReferenceActive
            && stageFrame.AudioReferencePreWrapPosCond is not null)
        {
            genInfo.PosCond = stageFrame.AudioReferencePreWrapPosCond;
            genInfo.NegCond = stageFrame.AudioReferencePreWrapNegCond;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput cropLatentSource;
        INodeOutput audioLatentSource = null;
        if (shouldRestoreAudioVideoLatent)
        {
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (g.CurrentMedia?.Path is JArray avPath)
            {
                separate.AvLatent.ConnectFromPath(bridge, avPath);
            }
            cropLatentSource = separate.VideoLatent;
            audioLatentSource = separate.AudioLatent;
        }
        else
        {
            cropLatentSource = g.CurrentMedia?.Path is JArray latentPath
                ? bridge.ResolvePath(latentPath)
                : null;
        }

        LTXVCropGuidesNode crop = bridge.AddNode(new LTXVCropGuidesNode());
        crop.ConnectConditioning(bridge, genInfo);
        crop.LatentInput.ConnectToUntyped(cropLatentSource);

        genInfo.SetConditioning(crop);

        if (shouldRestoreAudioVideoLatent)
        {
            LTXVConcatAVLatentNode concat = bridge.AddNode(new LTXVConcatAVLatentNode().With(
                VideoLatent: crop.Latent));
            concat.AudioLatent.TryConnectToUntyped(audioLatentSource);

            g.CurrentMedia = g.CurrentMedia.WithPath(
                concat.Latent,
                WGNodeData.DT_LATENT_AUDIOVIDEO,
                genInfo.Model.Compat);
            return;
        }

        g.CurrentMedia = g.CurrentMedia.WithPath(crop.Latent, null, genInfo.Model.Compat);
    }
}
