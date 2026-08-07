using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Authoring;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Runtime.Guide;

internal sealed record ResolvedIcLoraDrive(
    JArray Images,
    int? ControlNetIndex,
    bool IsStillImage);

internal sealed class IcLoraVisualGuideResolver(WorkflowGenerator g)
{
    internal bool TryResolve(
        WorkflowBridge bridge,
        ClipPlan clip,
        StagePlan stage,
        IcLoraPlan entry,
        WGNodeData stageInput,
        out ResolvedIcLoraDrive drive)
    {
        drive = null;
        if (entry.Drive.Stream != IcLoraDriveData.Visual)
        {
            return false;
        }
        switch (entry.Drive.Source)
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
                    entry.Drive.MediaKind == IcLoraDriveMediaKind.Image);
                return true;
            }
            case IcLoraMediaSourceKind.Incoming:
                if (stageInput is null || !IsImageStream(stageInput))
                {
                    RequestWarnings.Track(
                        g.UserInput,
                        $"VideoStages: planned IC-LoRA Incoming visual media is unavailable for stage "
                        + $"{stage.ClipStageRawIndex}; applying the model patch without a guide.");
                    return false;
                }
                drive = new(
                    new JArray(stageInput.Path[0], stageInput.Path[1]),
                    null,
                    entry.Drive.MediaKind == IcLoraDriveMediaKind.Image);
                return true;
            case IcLoraMediaSourceKind.ControlNet:
                if (entry.Drive.ControlNetIndex is not int index
                    || !new LtxControlNetMediaNormalizer(g).TryGetNormalizedControlImage(
                        index,
                        out WGNodeData controlImage))
                {
                    RequestWarnings.Track(
                        g.UserInput,
                        $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} requires ControlNet "
                        + $"{(entry.Drive.ControlNetIndex ?? -1) + 1} drive media, but it is unavailable; "
                        + "applying the model patch without a guide.");
                    return false;
                }
                drive = new(
                    new JArray(controlImage.Path[0], controlImage.Path[1]),
                    index,
                    false);
                return true;
            default:
                RequestWarnings.Track(
                    g.UserInput,
                    $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} has no usable drive-media "
                    + "identity; applying the model patch without a guide.");
                return false;
        }
    }

    private JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        IcLoraPlan entry)
    {
        int entryIndex = entry.EntryIndex;
        string key = LtxRuntimeKeyScope.IcLoraUploadedDriveImages(clipId, entryIndex);
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }
        if (string.IsNullOrWhiteSpace(entry.Drive.Upload?.Data))
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: planned IC-LoRA entry {entryIndex} requires uploaded drive media, "
                + "but the planned media identity is empty; applying the model patch without a guide.");
            return null;
        }

        JArray path;
        if (entry.Drive.MediaKind == IcLoraDriveMediaKind.Image)
        {
            SwarmLoadImageB64Node loadImage =
                bridge.AddNode(new SwarmLoadImageB64Node().With(
                    ImageBase64: UploadedMedia.GetRefImage(
                        g.UserInput,
                        entry.Drive.Upload.Data,
                        entry.Drive.Upload.FileName,
                        IcLoraDriveDescriptor.Image(clipId)).AsBase64));
            path = WorkflowBridge.ToPath(loadImage.IMAGE);
        }
        else if (entry.Drive.MediaKind == IcLoraDriveMediaKind.Video)
        {
            SwarmLoadVideoB64Node load =
                bridge.AddNode(new SwarmLoadVideoB64Node().With(
                    VideoBase64: UploadedMedia.GetVideo(
                        g.UserInput,
                        entry.Drive.Upload.Data,
                        entry.Drive.Upload.FileName,
                        IcLoraDriveDescriptor.Video(clipId)).AsBase64));
            GetVideoComponentsNode components =
                bridge.AddNode(new GetVideoComponentsNode());
            components.Video.ConnectToUntyped(load.VIDEO);
            path = WorkflowBridge.ToPath(components.Images);
        }
        else
        {
            RequestWarnings.Track(
                g.UserInput,
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
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ReferenceFramingMode referenceFraming)
    {
        if (genInfo.Width is null || genInfo.Height is null)
        {
            return images;
        }
        int width = Math.Max(1, genInfo.Width.Value<int>());
        int height = Math.Max(1, genInfo.Height.Value<int>());
        return ReferenceFramingGraph.Frame(
            bridge,
            images,
            width,
            height,
            referenceFraming,
            unwrapExistingFraming: false);
    }

    private static bool IsImageStream(WGNodeData media) =>
        media.DataType == WGNodeData.DT_IMAGE
        || media.DataType == WGNodeData.DT_VIDEO;
}
