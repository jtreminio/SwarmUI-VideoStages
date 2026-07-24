using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record ResolvedIcLoraDrive(
    JArray Images,
    int? ControlNetIndex,
    bool IsStillImage);

/// <summary>Resolves planned drive identities into graph media and materializes embedded uploads.</summary>
internal sealed class IcLoraVisualGuideResolver(WorkflowGenerator g)
{
    private const string UploadedDriveImagesKeyPrefix = "videostages.iclora.upload.";

    internal bool TryResolve(
        WorkflowBridge bridge,
        ClipPlan clip,
        StagePlan stage,
        IcLoraPlan entry,
        WGNodeData stageInput,
        out ResolvedIcLoraDrive drive)
    {
        drive = null;
        if (!entry.MediaContract.ConsumesVisual)
        {
            return false;
        }
        switch (entry.MediaInput.Source)
        {
            case IcLoraMediaSourceKind.Upload:
            {
                JArray images = GetOrCreateUploadedDriveImages(
                    bridge,
                    clip.ClipId,
                    entry);
                if (images is null)
                {
                    return false;
                }
                drive = new(
                    images,
                    null,
                    entry.DriveMedia.Kind == IcLoraDriveMediaKind.Image);
                return true;
            }
            case IcLoraMediaSourceKind.Incoming:
                if (stageInput is null || !IsImageStream(stageInput))
                {
                    throw new SwarmUserErrorException(
                        $"VideoStages: planned IC-LoRA Incoming visual media is unavailable for stage "
                        + $"{stage.ClipStageRawIndex}.");
                }
                drive = new(
                    new JArray(stageInput.Path[0], stageInput.Path[1]),
                    null,
                    entry.MediaInput.Kind == IcLoraDriveMediaKind.Image);
                return true;
            case IcLoraMediaSourceKind.ControlNet:
                if (entry.MediaInput.ControlNetIndex is not int index
                    || !new ControlNetCapture(g).TryGetCapturedCoreControlImage(
                        index,
                        out WGNodeData controlImage))
                {
                    Logs.Warning(
                        $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} requires ControlNet "
                        + $"{(entry.MediaInput.ControlNetIndex ?? -1) + 1} drive media, but it is unavailable; "
                        + "applying the model patch without a guide.");
                    return false;
                }
                drive = new(
                    new JArray(controlImage.Path[0], controlImage.Path[1]),
                    index,
                    false);
                return true;
            case IcLoraMediaSourceKind.LoaderOnly:
                return false;
            default:
                Logs.Warning(
                    $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} has no usable drive-media "
                    + "identity; applying the model patch without a guide.");
                return false;
        }
    }

    internal JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        IcLoraPlan entry) => GetOrCreateUploadedDriveImages(
            bridge,
            clipId,
            entry.EntryIndex,
            entry.DriveMedia.Kind,
            entry.DriveMedia.Data);

    internal JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        int entryIndex,
        IcLoraDriveMediaKind mediaKind,
        string data)
    {
        string key = $"{UploadedDriveImagesKeyPrefix}{clipId}.{entryIndex}";
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }
        if (string.IsNullOrWhiteSpace(data))
        {
            Logs.Warning(
                $"VideoStages: planned IC-LoRA entry {entryIndex} requires uploaded drive media, "
                + "but the planned media identity is empty; applying the model patch without a guide.");
            return null;
        }

        JArray path;
        if (mediaKind == IcLoraDriveMediaKind.Image)
        {
            SwarmLoadImageB64Node loadImage =
                bridge.AddNode(new SwarmLoadImageB64Node().With(
                    ImageBase64: VideoGraphHelpers.StripDataUriPrefix(data)));
            path = WorkflowBridge.ToPath(loadImage.IMAGE);
        }
        else if (mediaKind == IcLoraDriveMediaKind.Video)
        {
            SwarmLoadVideoB64Node load =
                bridge.AddNode(new SwarmLoadVideoB64Node().With(
                    VideoBase64: VideoGraphHelpers.StripDataUriPrefix(data)));
            GetVideoComponentsNode components =
                bridge.AddNode(new GetVideoComponentsNode());
            components.Video.ConnectToUntyped(load.VIDEO);
            path = WorkflowBridge.ToPath(components.Images);
        }
        else
        {
            Logs.Warning(
                $"VideoStages: planned IC-LoRA entry {entryIndex} has unsupported uploaded drive-media "
                + "kind; applying the model patch without a guide.");
            return null;
        }
        VideoGraphHelpers.CachePath(g, key, path);
        return path;
    }

    internal JArray ResizeToStageDimensions(
        WorkflowBridge bridge,
        JArray images,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (genInfo.Width is null || genInfo.Height is null)
        {
            return images;
        }
        ResizeImageMaskNodeNode resize =
            bridge.AddNode(new ResizeImageMaskNodeNode()).With(
                ResizeType: "scale dimensions",
                ScaleMethod: "lanczos");
        resize.Input.TryConnectFromPath(bridge, images);
        resize.ExtraInputs["resize_type.width"] = genInfo.Width.DeepClone();
        resize.ExtraInputs["resize_type.height"] = genInfo.Height.DeepClone();
        resize.ExtraInputs["resize_type.crop"] = "center";
        // Raw ExtraInputs edits are not auto-synced by the bridge.
        bridge.SyncNode(resize);
        return WorkflowBridge.ToPath(resize.Resized);
    }

    private static bool IsImageStream(WGNodeData media) =>
        media.DataType == WGNodeData.DT_IMAGE
        || media.DataType == WGNodeData.DT_VIDEO;
}
