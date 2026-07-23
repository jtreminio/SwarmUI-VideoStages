using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

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
        // Windows BOTH av channels to the retake span (preserved frames + their audio stay locked, the
        // window regenerates). No-op for plain clips with no retake window.
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

        if (genInfo.DoFirstFrameLatentSwap is not null)
        {
            ApplyFirstFrameLatentSwap(genInfo);
        }
    }

    private void ApplyFirstFrameLatentSwap(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ReplaceVideoLatentFramesNode replace = bridge.AddNode(new ReplaceVideoLatentFramesNode().With(
            Index: 0));
        if (g.CurrentMedia?.Path is JArray destPath)
        {
            replace.Destination.ConnectFromPath(bridge, destPath);
        }
        if (genInfo.DoFirstFrameLatentSwap is JArray sourcePath)
        {
            replace.Source.TryConnectFromPath(bridge, sourcePath);
        }

        NormalizeVideoLatentStartNode normalize = bridge.AddNode(new NormalizeVideoLatentStartNode().With(
            StartFrameCount: 4,
            ReferenceFrameCount: 5,
            LatentInput: replace.LATENT));

        g.CurrentMedia = g.CurrentMedia.WithPath(normalize.Latent);
    }

    private void CropGuidesAfterSampler(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame)
    {
        bool shouldRestoreAudioVideoLatent =
            g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO;

        // Voice-ref clips: the crop branches from the PRE-ref-token conditioning — in the official
        // LipDub graph the ref-token wrap feeds only the sampler's guider and never flows into
        // LTXVCropGuides. (This also bypasses a retake window-mask wrap, an unsupported combo.)
        if (stageFrame.VoiceRefActive && stageFrame.VoiceRefPreWrapPosCond is not null)
        {
            genInfo.PosCond = stageFrame.VoiceRefPreWrapPosCond;
            genInfo.NegCond = stageFrame.VoiceRefPreWrapNegCond;
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
            INodeOutput concatAudioSource = audioLatentSource;
            if (stageFrame.VoiceRefActive && audioLatentSource is not null)
            {
                // Official LipDub refine carry: crop → SetAudioRefTokens(this stage's generated
                // audio) → the next stage's conditioning, with the concat taking the FROZEN audio so
                // refinement preserves the generated speech instead of renoising it. The stash lets
                // the next stage's own wrap reuse this audio as its speaker context.
                LTXVSetAudioRefTokensNode refTokens = bridge.AddNode(new LTXVSetAudioRefTokensNode());
                refTokens.PositiveInput.ConnectToUntyped(crop.Positive);
                refTokens.NegativeInput.ConnectToUntyped(crop.Negative);
                refTokens.AudioLatent.ConnectToUntyped(audioLatentSource);
                bridge.SyncNode(refTokens);
                genInfo.PosCond = WorkflowBridge.ToPath(refTokens.Positive);
                genInfo.NegCond = WorkflowBridge.ToPath(refTokens.Negative);
                g.NodeHelpers[
                    $"{VoiceRefApplicator.VoiceRefStageAudioKeyPrefix}"
                    + $"{stageFrame.ClipContext.PlannedClip.ClipId}"] =
                    WorkflowBridge.ToPath(audioLatentSource)
                        .ToString(Newtonsoft.Json.Formatting.None);
                concatAudioSource = refTokens.FrozenAudio;
            }

            LTXVConcatAVLatentNode concat = bridge.AddNode(new LTXVConcatAVLatentNode().With(
                VideoLatent: crop.Latent));
            concat.AudioLatent.TryConnectToUntyped(concatAudioSource);

            g.CurrentMedia = g.CurrentMedia.WithPath(
                concat.Latent,
                WGNodeData.DT_LATENT_AUDIOVIDEO,
                genInfo.Model.Compat);
            return;
        }

        g.CurrentMedia = g.CurrentMedia.WithPath(crop.Latent, null, genInfo.Model.Compat);
    }
}
