using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages;

internal class VoiceRefApplicator(WorkflowGenerator g)
{
    private const string VoiceRefSampleKeyPrefix = "videostages.voiceref.sample.";
    internal const string VoiceRefStageAudioKeyPrefix = "videostages.voiceref.stageaudio.";

    /// <summary>
    /// Wraps this stage's conditioning in an LTXVSetAudioRefTokens node for voice-reference clips:
    /// the sample provides speaker identity as context tokens, and the model GENERATES the speech
    /// matching the prompt — so the clip's audio is never injected as a locked sampling track
    /// (ClipAudioWorkflowHelper resolves a voice-ref source to null). The sample is the flagged
    /// entry's drive-video audio, else the clip's "Voice Reference" upload. Refine stages prefer the
    /// previous stage's generated audio latent (stashed by CropGuidesAfterSampler), matching the
    /// official LipDub two-stage graph. No-op off LTX-2 or when no sample resolves.
    /// </summary>
    public void ApplyVoiceRefTokens(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipSpec clip,
        StageFrame stageFrame,
        bool isGeneratingStage)
    {
        if (!clip.UsesVoiceRefAudio
            || genInfo?.Model is null
            || genInfo.VideoModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || genInfo.PosCond is null
            || genInfo.NegCond is null)
        {
            return;
        }

        JArray audioLatentPath = ResolveVoiceRefAudioLatent(clip, isGeneratingStage);
        if (audioLatentPath is null)
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        stageFrame.VoiceRefPreWrapPosCond = genInfo.PosCond;
        stageFrame.VoiceRefPreWrapNegCond = genInfo.NegCond;
        LTXVSetAudioRefTokensNode refTokens = bridge.AddNode(new LTXVSetAudioRefTokensNode());
        refTokens.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        refTokens.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        refTokens.AudioLatent.TryConnectFromPath(bridge, audioLatentPath);
        bridge.SyncNode(refTokens);
        genInfo.PosCond = WorkflowBridge.ToPath(refTokens.Positive);
        genInfo.NegCond = WorkflowBridge.ToPath(refTokens.Negative);
        stageFrame.VoiceRefActive = true;
    }

    // The encoded sample latent is built once per clip and reused by every stage's wrap.
    private JArray ResolveVoiceRefAudioLatent(ClipSpec clip, bool isGeneratingStage)
    {
        if (!isGeneratingStage
            && VideoGraphHelpers.TryGetCachedPath(g, null, $"{VoiceRefStageAudioKeyPrefix}{clip.Id}", out JArray staged))
        {
            return staged;
        }
        string sampleKey = $"{VoiceRefSampleKeyPrefix}{clip.Id}";
        if (VideoGraphHelpers.TryGetCachedPath(g, null, sampleKey, out JArray cached))
        {
            return cached;
        }
        if (g.CurrentAudioVae is null)
        {
            Logs.Warning("VideoStages: voice-reference audio needs an audio VAE; skipping ref tokens.");
            return null;
        }
        JArray samplePath;
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            samplePath = ResolveVoiceRefSampleAudio(bridge, clip);
        }
        if (samplePath is null)
        {
            return null;
        }
        // Encoded outside any bridge scope (EncodeToLatent writes to the workflow directly).
        WGNodeData sample = new(samplePath, g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
        JArray encoded = sample.EncodeToLatent(g.CurrentAudioVae).Path;
        VideoGraphHelpers.CachePath(g, sampleKey, encoded);
        return encoded;
    }

    private JArray ResolveVoiceRefSampleAudio(WorkflowBridge bridge, ClipSpec clip)
    {
        if (clip.VoiceRefDriveEntry is IcLoraSpec driveEntry)
        {
            if (VideoGraphHelpers.IsImageDataUri(driveEntry.Video.Data))
            {
                throw new SwarmUserErrorException(
                    "An IC-LoRA drive image has no audio to use as a voice reference. Upload a "
                    + "drive video with sound, or set the clip's Audio source to Voice Reference.");
            }
            int entryIdx = 0;
            while (entryIdx < clip.IcLoras.Count && !ReferenceEquals(clip.IcLoras[entryIdx], driveEntry))
            {
                entryIdx++;
            }
            _ = new IcLoraApplicator(g).GetOrCreateUploadedDriveImages(bridge, clip.Id, entryIdx, driveEntry.Video);
            return VideoGraphHelpers.TryGetCachedPath(
                g, bridge, $"{IcLoraApplicator.UploadedDriveAudioKeyPrefix}{clip.Id}.{entryIdx}", out JArray driveAudio)
                ? driveAudio
                : null;
        }
        AudioFile uploaded = VideoStagesSpecParser.MaterializeUploadedAudioForClip(g, clip);
        if (uploaded is null)
        {
            Logs.Warning(
                "VideoStages: clip audio is 'Voice Reference' but no audio file was uploaded.");
            return null;
        }
        return new JArray(g.CreateAudioLoadNode(uploaded, "${vsvoiceref}"), 0);
    }
}
