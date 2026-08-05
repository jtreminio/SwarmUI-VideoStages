using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class IcLoraAudioReferenceApplicator(WorkflowGenerator g)
{
    internal void ApplyAudioReferenceTokens(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipPlan clip,
        StageFrame stageFrame,
        WGNodeData incomingMedia)
    {
        IcLoraPlan audioReference = stageFrame.Stage.RequireLtx2Payload().IcLoras
            .SingleOrDefault(entry => entry.HasAudioReference);
        if (audioReference is null
            || genInfo.Model is null
            || genInfo.PosCond is null
            || genInfo.NegCond is null)
        {
            return;
        }

        JArray audioLatentPath = ResolveAudioReferenceLatent(
            clip,
            audioReference,
            incomingMedia,
            stageFrame.Stage.ClipStageRawIndex);
        if (audioLatentPath is null)
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        stageFrame.AudioReferencePreWrapPosCond = genInfo.PosCond;
        stageFrame.AudioReferencePreWrapNegCond = genInfo.NegCond;
        LTXVSetAudioRefTokensNode refTokens = bridge.AddNode(new LTXVSetAudioRefTokensNode());
        refTokens.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        refTokens.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        refTokens.AudioLatent.TryConnectFromPath(bridge, audioLatentPath);
        genInfo.PosCond = WorkflowBridge.ToPath(refTokens.Positive);
        genInfo.NegCond = WorkflowBridge.ToPath(refTokens.Negative);
        stageFrame.AudioReferenceActive = true;
    }

    private JArray ResolveAudioReferenceLatent(
        ClipPlan clip,
        IcLoraPlan entry,
        WGNodeData incomingMedia,
        int rawStageIndex)
    {
        string sampleKey = LtxRuntimeKeyScope.IcLoraAudioReference(
            clip.ClipId,
            entry.EntryIndex,
            entry.Drive.Source == IcLoraMediaSourceKind.Incoming
                ? rawStageIndex
                : null);
        if (VideoGraphHelpers.TryGetCachedPath(g, null, sampleKey, out JArray cached))
        {
            return cached;
        }
        WGNodeData sample;
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            sample = ResolveDriveAudio(bridge, entry, incomingMedia);
        }
        if (sample is null)
        {
            return null;
        }
        JArray encoded = EnsureAudioReferenceLatent(sample);
        VideoGraphHelpers.CachePath(g, sampleKey, encoded);
        return encoded;
    }

    internal JArray EnsureAudioReferenceLatent(WGNodeData sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.DataType == WGNodeData.DT_LATENT_AUDIO)
        {
            return sample.Path is JArray latentPath
                ? new JArray(latentPath[0], latentPath[1])
                : null;
        }
        if (g.CurrentAudioVae is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                "VideoStages: this audio-consuming IC-LoRA requires an LTX audio VAE, "
                + "but none is available for the selected model; skipping its audio reference.");
            return null;
        }
        return sample.EncodeToLatent(g.CurrentAudioVae).Path;
    }

    internal WGNodeData ResolveDriveAudio(
        WorkflowBridge bridge,
        IcLoraPlan entry,
        WGNodeData incomingMedia)
    {
        if (entry.Drive.Source == IcLoraMediaSourceKind.Incoming)
        {
            if (incomingMedia?.AttachedAudio is not WGNodeData incomingAudio)
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: IC-LoRA entry {entry.EntryIndex} requests Incoming Audio, "
                    + "but the incoming media has no attached audio; skipping its audio reference.");
                return null;
            }
            return incomingAudio;
        }
        if (entry.Drive.Source != IcLoraMediaSourceKind.Upload)
        {
            throw Invariant.Failure(
                $"IC-LoRA entry {entry.EntryIndex} has no supported audio drive source.");
        }
        UploadedMediaSpec media = entry.Drive.Upload;
        if (string.IsNullOrWhiteSpace(media?.Data))
        {
            throw Invariant.Failure(
                $"IC-LoRA entry {entry.EntryIndex} requires audio or video Drive Media.");
        }
        if (entry.Drive.MediaKind == IcLoraDriveMediaKind.Audio)
        {
            JArray uploadedAudio = LoadUploadedAudio(media);
            return uploadedAudio is null ? null : new(
                uploadedAudio,
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat);
        }
        if (entry.Drive.MediaKind != IcLoraDriveMediaKind.Video)
        {
            throw Invariant.Failure(
                "This IC-LoRA requires audio or video Drive Media for its speaker reference.");
        }
        SwarmLoadVideoB64Node load =
            bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: VideoGraphHelpers.StripDataUriPrefix(media.Data)));
        GetVideoComponentsNode components = bridge.AddNode(new GetVideoComponentsNode());
        components.Video.ConnectToUntyped(load.VIDEO);
        return components.Audio.ToWGNodeData(g, WGNodeData.DT_AUDIO);
    }

    private JArray LoadUploadedAudio(UploadedMediaSpec media)
    {
        AudioFile uploaded = UploadedMedia.GetAudio(
            g.UserInput,
            media);
        return new JArray(g.CreateAudioLoadNode(uploaded, "${vsicloraaudio}"), 0);
    }
}
