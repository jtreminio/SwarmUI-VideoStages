using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Generated;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Loads and conforms init-video footage to its compiled clip window.</summary>
internal sealed class InitVideoClipInstaller(WorkflowGenerator g)
{
    public WGNodeData TryInstall(ClipPlan plan, bool includeSourceAudio = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitVideoPlan source = plan.InitVideo;
        if (plan.EntryMode != ArchitectureEntryMode.InitVideo
            || source is null
            || plan.Frames is not int frames
            || frames <= 0
            || source.TargetFramesPerSecond <= 0)
        {
            return null;
        }
        ImageFile video = EmbeddedMediaMaterializer.MaterializeInitVideo(g, source);
        if (video is null)
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
