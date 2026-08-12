using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>Conforms source footage to its compiled clip window.</summary>
internal sealed class InitVideoClipInstaller(WorkflowGenerator g)
{
    public WGNodeData TryInstall(
        ClipPlan plan,
        WGNodeData previousClipOutput = null,
        bool includeSourceAudio = true)
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
        bool usesPreviousClip = StringUtils.Equals(source.Source, MediaSource.PreviousClip);
        WGNodeData loaded;
        if (usesPreviousClip)
        {
            loaded = previousClipOutput?.Duplicate();
        }
        else
        {
            ImageFile video = UploadedMedia.GetInitVideo(g.UserInput, source);
            loaded = video is null
                ? null
                : g.LoadImage(video, $"${{vsinitvideo{plan.ClipId}}}", resize: false);
        }
        if (loaded is null)
        {
            return null;
        }
        int fps = source.TargetFramesPerSecond;
        int startFrame = (int)Math.Round(source.StartSeconds * fps);

        using WorkflowBridge bridge = BridgeSync.For(g);
        JArray conformedPath;
        if (usesPreviousClip
            && loaded.FPS?.Type == JTokenType.Integer
            && loaded.FPS.Value<int>() == fps)
        {
            conformedPath = (JArray)loaded.Path;
        }
        else
        {
            SwarmVideoResampleFPSNode resample = bridge.AddNode(
                new SwarmVideoResampleFPSNode().With(
                    FpsIn: usesPreviousClip ? loaded.FPS.Value<double>() : 0,
                    FpsOut: (double)fps,
                    Method: "linear"));
            resample.ImagesInput.TryConnectFromPath(bridge, loaded.Path);
            if (!usesPreviousClip)
            {
                // LoadImage's video branch reads the file's real rate at runtime.
                resample.FpsIn.TryConnectFromPath(bridge, (JArray)loaded.FPS);
            }
            conformedPath = new JArray(resample.Id, 0);
        }

        SwarmFrameWindowNode window = bridge.AddNode(new SwarmFrameWindowNode().With(
            StartFrame: startFrame,
            FrameCount: frames));
        window.ImagesInput.TryConnectFromPath(bridge, conformedPath);

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

        if (includeSourceAudio && loaded.AttachedAudio is WGNodeData sourceAudio)
        {
            TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
                StartIndex: source.StartSeconds,
                Duration: frames / (double)fps));
            trim.Audio.TryConnectFromPath(bridge, (JArray)sourceAudio.Path);
            output.AttachedAudio = new WGNodeData(
                new JArray(trim.Id, 0),
                g,
                WGNodeData.DT_AUDIO,
                null);
        }

        return output;
    }
}
