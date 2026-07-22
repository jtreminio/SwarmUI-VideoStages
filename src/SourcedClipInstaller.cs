using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using VideoStages.Generated;

namespace VideoStages;

/// <summary>
/// Builds the workflow chain for a clip whose content is pre-existing footage instead of a
/// generation: load the embedded video, resample it to the timeline fps (fps_in is the file's own
/// rate, read at runtime from GetVideoComponents), slice the used range to the clip's exact aligned
/// frame count (SwarmFrameWindow pads a short tail by repeating the last frame, so the merge plan's
/// frame math holds by construction), and resize to the timeline dimensions. The file's own audio
/// track is trimmed to the same range and attached for the cross-clip audio concat.
/// </summary>
internal sealed class SourcedClipInstaller(WorkflowGenerator g)
{
    public WGNodeData TryInstall(ClipSpec clip, VideoStagesSpec spec)
    {
        ImageFile video = VideoStagesSpecParser.MaterializeSourceVideoForClip(g, clip);
        if (video is null || clip.Frames is not int frames || frames <= 0 || spec.FPS <= 0)
        {
            return null;
        }

        WGNodeData loaded = g.LoadImage(video, $"${{vssourcevideo{clip.Id}}}", resize: false);
        int fps = spec.FPS;
        int startFrame = (int)Math.Round(clip.SourceVideo.StartSeconds * fps);

        using WorkflowBridge bridge = BridgeSync.For(g);
        SwarmVideoResampleFPSNode resample = bridge.AddNode(new SwarmVideoResampleFPSNode().With(
            FpsOut: (double)fps,
            Method: "linear"));
        resample.ImagesInput.TryConnectFromPath(bridge, loaded.Path);
        // LoadImage's video branch always sets FPS (a GetVideoComponents output ref) and
        // AttachedAudio — the file's real rate and track are read at runtime, not probed here.
        resample.FpsIn.TryConnectFromPath(bridge, (JArray)loaded.FPS);

        SwarmFrameWindowNode window = bridge.AddNode(new SwarmFrameWindowNode().With(
            ImagesInput: resample.Images,
            StartFrame: startFrame,
            FrameCount: frames));

        ImageScaleNode scale = ImageScaleReuse.Create(
            bridge, new JArray(window.Id, 0), spec.Width, spec.Height, crop: "center");

        WGNodeData output = new(
            new JArray(scale.Id, 0), g, WGNodeData.DT_VIDEO, T2IModelClassSorter.CompatLtxv2)
        {
            Width = spec.Width,
            Height = spec.Height,
            Frames = frames,
            FPS = fps
        };

        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: clip.SourceVideo.StartSeconds,
            Duration: frames / (double)fps));
        trim.Audio.TryConnectFromPath(bridge, (JArray)loaded.AttachedAudio.Path);
        output.AttachedAudio = new WGNodeData(
            new JArray(trim.Id, 0),
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae?.Compat ?? T2IModelClassSorter.CompatLtxv2);

        return output;
    }
}
