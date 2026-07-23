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
        StageFrame stageFrame)
    {
        IcLoraPlan audioReference = stageFrame.Stage.RequireLtx2Payload().IcLoras
            .SingleOrDefault(entry =>
                entry.MediaContract.Consumption
                    == IcLoraDriveMediaConsumption.AudioReference);
        if (audioReference is null
            || genInfo.Model is null
            || genInfo.VideoModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || genInfo.PosCond is null
            || genInfo.NegCond is null)
        {
            return;
        }

        JArray audioLatentPath = ResolveAudioReferenceLatent(clip, audioReference);
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
        IcLoraPlan entry)
    {
        string sampleKey = $"{SampleKeyPrefix}{clip.ClipId}.{entry.EntryIndex}";
        if (VideoGraphHelpers.TryGetCachedPath(g, null, sampleKey, out JArray cached))
        {
            return cached;
        }
        if (g.CurrentAudioVae is null)
        {
            throw new SwarmUserErrorException(
                "VideoStages: this audio-consuming IC-LoRA requires an LTX audio VAE, "
                + "but none is available for the selected model.");
        }

        JArray samplePath;
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            samplePath = ResolveDriveAudio(bridge, entry);
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

    private JArray ResolveDriveAudio(
        WorkflowBridge bridge,
        IcLoraPlan entry)
    {
        IcLoraDriveMediaPlan media = entry.DriveMedia;
        if (!media.IsConfigured)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: IC-LoRA entry {entry.EntryIndex} requires audio or video Drive Media.");
        }
        if (media.Kind == IcLoraDriveMediaKind.Audio)
        {
            return LoadUploadedAudio(media);
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
        return WorkflowBridge.ToPath(components.Audio);
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
