using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Builds the workflow chain for a clip whose content is pre-existing footage instead of a
/// generation: load the embedded video, resample it to the timeline fps (fps_in is the file's own
/// rate, read at runtime from GetVideoComponents), slice the used range to the clip's exact aligned
/// frame count (SwarmFrameWindow pads a short tail by repeating the last frame, so the merge plan's
/// frame math holds by construction), and resize to the timeline dimensions. By default the file's
/// own audio track is trimmed to the same range and attached for cross-clip audio concat; an
/// explicitly audio-disabled architecture can request video-only installation and build no audio
/// trim branch.
/// </summary>
internal sealed class InitVideoClipInstaller(WorkflowGenerator g)
{
    /// <summary>
    /// Installs a initVideoClip clip from its immutable execution plan. Architecture execution intentionally does
    /// not return to <see cref="ClipSpec"/> for embedded-media identity or timeline dimensions.
    /// <paramref name="includeSourceAudio"/> defaults to the existing source-audio behavior; false
    /// omits the audio branch entirely.
    /// </summary>
    public WGNodeData TryInstall(ClipPlan plan, bool includeSourceAudio = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitVideoPlan source = plan.InitVideo;
        ImageFile video = EmbeddedMediaMaterializer.MaterializeInitVideo(g, source);
        if (!plan.HasInitVideo
            || source is null
            || video is null
            || plan.Frames is not int frames
            || frames <= 0
            || source.TargetFramesPerSecond <= 0)
        {
            return null;
        }

        WGNodeData loaded = g.LoadImage(video, $"${{vsinitvideo{plan.ClipId}}}", resize: false);
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
            new JArray(scale.Id, 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = source.TargetWidth,
            Height = source.TargetHeight,
            Frames = frames,
            FPS = fps
        };

        if (includeSourceAudio)
        {
            TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
                StartIndex: source.StartSeconds,
                Duration: frames / (double)fps));
            trim.Audio.TryConnectFromPath(bridge, (JArray)loaded.AttachedAudio.Path);
            output.AttachedAudio = new WGNodeData(
                new JArray(trim.Id, 0),
                g,
                WGNodeData.DT_AUDIO,
                null);
        }

        return output;
    }
}
