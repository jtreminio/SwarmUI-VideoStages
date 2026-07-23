using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Applies the compiled voice-reference policy. The plan owns whether a sample is a drive-video
/// audio track or a clip upload, including the optional upload fallback for a missing drive sample.
/// </summary>
internal sealed class VoiceRefApplicator(WorkflowGenerator g)
{
    private const string VoiceRefSampleKeyPrefix = "videostages.voiceref.sample.";
    internal const string VoiceRefStageAudioKeyPrefix = "videostages.voiceref.stageaudio.";

    internal void ApplyVoiceRefTokens(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipPlan clip,
        StageFrame stageFrame,
        bool isGeneratingStage)
    {
        AudioVoiceReferencePlan voiceReference =
            (clip.ArchitecturePayload as Ltx2ClipPayload)?.VoiceReference
            ?? throw new InvalidOperationException(
                $"Clip {clip.ClipId} has no LTX voice-reference plan.");
        if (!voiceReference.IsRequested
            || genInfo.Model is null
            || genInfo.VideoModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || genInfo.PosCond is null
            || genInfo.NegCond is null)
        {
            return;
        }

        JArray audioLatentPath = ResolveVoiceRefAudioLatent(clip, voiceReference, isGeneratingStage);
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

    private JArray ResolveVoiceRefAudioLatent(
        ClipPlan clip,
        AudioVoiceReferencePlan voiceReference,
        bool isGeneratingStage)
    {
        if (!isGeneratingStage
            && VideoGraphHelpers.TryGetCachedPath(
                g, null, $"{VoiceRefStageAudioKeyPrefix}{clip.ClipId}", out JArray staged))
        {
            return staged;
        }
        string sampleKey = $"{VoiceRefSampleKeyPrefix}{clip.ClipId}";
        if (VideoGraphHelpers.TryGetCachedPath(g, null, sampleKey, out JArray cached))
        {
            return cached;
        }
        if (g.CurrentAudioVae is null)
        {
            Logs.Warning("VideoStages: planned voice-reference audio needs an audio VAE; skipping ref tokens.");
            return null;
        }

        JArray samplePath;
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            samplePath = ResolveVoiceRefSampleAudio(bridge, clip.ClipId, voiceReference);
        }
        if (samplePath is null)
        {
            return null;
        }
        WGNodeData sample = new(samplePath, g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
        JArray encoded = sample.EncodeToLatent(g.CurrentAudioVae).Path;
        VideoGraphHelpers.CachePath(g, sampleKey, encoded);
        return encoded;
    }

    private JArray ResolveVoiceRefSampleAudio(
        WorkflowBridge bridge,
        int clipId,
        AudioVoiceReferencePlan voiceReference)
    {
        if (voiceReference.Kind == AudioVoiceReferenceKind.IcLoraDriveVideo)
        {
            JArray driveAudio = ResolveDriveAudio(bridge, clipId, voiceReference);
            if (driveAudio is not null)
            {
                return driveAudio;
            }
            return LoadUploadedVoiceReference(voiceReference.FallbackMedia);
        }
        if (voiceReference.Kind == AudioVoiceReferenceKind.ClipUpload)
        {
            return LoadUploadedVoiceReference(voiceReference.Media);
        }
        return null;
    }

    private JArray ResolveDriveAudio(
        WorkflowBridge bridge,
        int clipId,
        AudioVoiceReferencePlan voiceReference)
    {
        if (!voiceReference.HasConfiguredSample
            || voiceReference.IcLoraEntryIndex is not int entryIndex
            || string.IsNullOrWhiteSpace(voiceReference.Media?.Data))
        {
            Logs.Warning(
                "VideoStages: planned IC-LoRA drive-audio voice reference has no drive media; "
                + "using the planned clip voice-reference fallback when available.");
            return null;
        }
        if (voiceReference.DriveMediaKind == IcLoraUploadedMediaKind.Image)
        {
            throw new SwarmUserErrorException(
                "An IC-LoRA drive image has no audio to use as a voice reference. Upload a "
                + "drive video with sound, or set the clip's Audio source to Voice Reference.");
        }
        _ = new IcLoraDriveMediaResolver(g).GetOrCreateUploadedDriveImages(
            bridge,
            clipId,
            entryIndex,
            voiceReference.DriveMediaKind ?? IcLoraUploadedMediaKind.Unknown,
            voiceReference.Media.Data);
        return VideoGraphHelpers.TryGetCachedPath(
            g,
            bridge,
            $"{IcLoraDriveMediaResolver.UploadedDriveAudioKeyPrefix}{clipId}.{entryIndex}",
            out JArray driveAudio)
            ? driveAudio
            : null;
    }

    private JArray LoadUploadedVoiceReference(AudioMediaIdentityPlan media)
    {
        if (media is null || string.IsNullOrWhiteSpace(media.Data))
        {
            Logs.Warning(
                "VideoStages: planned voice-reference media is missing; skipping ref tokens.");
            return null;
        }
        AudioFile uploaded = EmbeddedMediaMaterializer.MaterializeAudio(g, media);
        if (uploaded is null)
        {
            Logs.Warning(
                "VideoStages: planned voice-reference media could not be materialized; skipping ref tokens.");
            return null;
        }
        return new JArray(g.CreateAudioLoadNode(uploaded, "${vsvoiceref}"), 0);
    }
}
