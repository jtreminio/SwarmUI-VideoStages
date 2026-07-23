using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using VideoStages.Planning;

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
    /// <summary>
    /// Installs a sourced clip from its immutable execution plan. LTX execution intentionally does
    /// not return to <see cref="ClipSpec"/> for embedded-media identity or timeline dimensions.
    /// </summary>
    public WGNodeData TryInstall(ClipPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SourceVideoPlan source = plan.SourceVideo;
        ImageFile video = EmbeddedMediaMaterializer.MaterializeSourceVideo(g, source);
        if (!plan.IsSourced
            || source is null
            || video is null
            || plan.Frames is not int frames
            || frames <= 0
            || source.TargetFramesPerSecond <= 0)
        {
            return null;
        }

        WGNodeData loaded = g.LoadImage(video, $"${{vssourcevideo{plan.ClipId}}}", resize: false);
        int fps = source.TargetFramesPerSecond;
        int startFrame = (int)Math.Round(source.StartSeconds * fps);

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
            bridge,
            new JArray(window.Id, 0),
            source.TargetWidth,
            source.TargetHeight,
            crop: "center");

        WGNodeData output = new(
            new JArray(scale.Id, 0), g, WGNodeData.DT_VIDEO, T2IModelClassSorter.CompatLtxv2)
        {
            Width = source.TargetWidth,
            Height = source.TargetHeight,
            Frames = frames,
            FPS = fps
        };

        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: source.StartSeconds,
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
