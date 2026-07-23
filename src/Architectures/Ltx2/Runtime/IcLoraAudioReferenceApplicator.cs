using ComfyTyped.Core;
using ComfyTyped.Generated;
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
/// Applies the audio stream consumed by an applicable IC-LoRA. Drive video frames are deliberately
/// never exposed here; the normal clip entry path remains the sole source of generated visuals.
/// </summary>
internal sealed class IcLoraAudioReferenceApplicator(WorkflowGenerator g)
{
    private const string SampleKeyPrefix = "videostages.iclora.audio-reference.";

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
            || genInfo.VideoModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
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
        bridge.SyncNode(refTokens);
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
        string sampleKey = entry.MediaInput.Source == IcLoraMediaSourceKind.Incoming
            ? $"{SampleKeyPrefix}{clip.ClipId}.{entry.EntryIndex}.{rawStageIndex}"
            : $"{SampleKeyPrefix}{clip.ClipId}.{entry.EntryIndex}";
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
            throw new SwarmUserErrorException(
                "VideoStages: this audio-consuming IC-LoRA requires an LTX audio VAE, "
                + "but none is available for the selected model.");
        }
        return sample.EncodeToLatent(g.CurrentAudioVae).Path;
    }

    private WGNodeData ResolveDriveAudio(
        WorkflowBridge bridge,
        IcLoraPlan entry,
        WGNodeData incomingMedia)
    {
        if (entry.MediaInput.Source == IcLoraMediaSourceKind.Incoming)
        {
            if (incomingMedia?.AttachedAudio is not WGNodeData incomingAudio)
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: IC-LoRA entry {entry.EntryIndex} requests Incoming Audio, "
                    + "but the incoming media has no attached audio.");
            }
            return incomingAudio;
        }
        if (entry.MediaInput.Source != IcLoraMediaSourceKind.Upload)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: IC-LoRA entry {entry.EntryIndex} has no supported audio drive source.");
        }
        IcLoraDriveMediaPlan media = entry.DriveMedia;
        if (!media.IsConfigured)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: IC-LoRA entry {entry.EntryIndex} requires audio or video Drive Media.");
        }
        if (media.Kind == IcLoraDriveMediaKind.Audio)
        {
            return new(
                LoadUploadedAudio(media),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat);
        }
        if (media.Kind != IcLoraDriveMediaKind.Video)
        {
            throw new SwarmUserErrorException(
                "This IC-LoRA requires audio or video Drive Media for its speaker reference.");
        }
        SwarmLoadVideoB64Node load =
            bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: VideoGraphHelpers.StripDataUriPrefix(media.Data)));
        GetVideoComponentsNode components = bridge.AddNode(new GetVideoComponentsNode());
        components.Video.ConnectToUntyped(load.VIDEO);
        bridge.SyncNode(load);
        bridge.SyncNode(components);
        return components.Audio.ToWGNodeData(g, WGNodeData.DT_AUDIO);
    }

    private JArray LoadUploadedAudio(IcLoraDriveMediaPlan media)
    {
        AudioFile uploaded = EmbeddedMediaMaterializer.MaterializeAudio(
            g,
            new UploadedMediaSpec(media.Data, media.FileName));
        if (uploaded is null)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the IC-LoRA audio Drive Media could not be loaded. "
                + "Choose a valid audio or video file.");
        }
        return new JArray(g.CreateAudioLoadNode(uploaded, "${vsicloraaudio}"), 0);
    }
}
