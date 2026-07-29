using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns request-global frame interpolation for the assembled timeline. The host parameter has no
/// per-clip selector, so preflight admits only a single clip and runtime applies the transform once
/// to the final decoded video.
/// </summary>
internal sealed class TimelineFrameInterpolator(WorkflowGenerator g)
{
    private const string Rife = "RIFE";
    private const string Film = "FILM";
    private const string Gimm = "GIMM-VFI";
    private const string FrameInterpsFeature = "frameinterps";
    private const string GimmFeature = "frameinterps_gimmvfi";

    internal IReadOnlyList<PlanDiagnostic> Preflight(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!TryResolveConfiguration(out Configuration config, out string error))
        {
            return error is null ? [] : [Refuse(error)];
        }

        List<PlanDiagnostic> diagnostics = [];
        if (plan.Clips.Count != 1)
        {
            string boundaryLabel = plan.Boundaries.Count == 1 ? "boundary" : "boundaries";
            diagnostics.Add(Refuse(
                "'Video Frame Interpolation' is request-global and can only be applied to a "
                + $"single completed clip. This timeline has {plan.Clips.Count} clips joined by "
                + $"{plan.Boundaries.Count} {boundaryLabel}; interpolating the assembled video "
                + "would synthesize frames across authored boundaries."));
        }
        string[] missingFeatures = RequiredFeatures(config.Method)
            .Where(feature => !g.Features.Contains(feature))
            .ToArray();
        if (missingFeatures.Length > 0)
        {
            diagnostics.Add(Refuse(
                $"Video frame interpolation method '{config.Method}' requires backend feature(s) "
                + $"{string.Join(", ", missingFeatures.Select(feature => $"'{feature}'"))}."));
        }
        return diagnostics;
    }

    internal void Apply()
    {
        if (!TryResolveConfiguration(out Configuration config, out string error))
        {
            if (error is null)
            {
                return;
            }
            throw new SwarmUserErrorException($"VideoStages: {error}");
        }

        WGNodeData media = g.CurrentMedia;
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (media?.DataType != WGNodeData.DT_VIDEO
            || media.Path is not JArray { Count: 2 } path
            || bridge.ResolvePath(path) is null)
        {
            throw new SwarmUserErrorException(
                "VideoStages: frame interpolation requires a resolvable decoded final video.");
        }
        if (media.Frames is not int frames || frames <= 0
            || media.GetRawFPS() is not int fps || fps <= 0)
        {
            throw new SwarmUserErrorException(
                "VideoStages: frame interpolation requires literal positive final frame-count "
                + "and frame-rate metadata.");
        }
        // A single frame has no interval to interpolate. Preserve its path and metadata exactly.
        if (frames == 1)
        {
            return;
        }

        int interpolatedFrames;
        int interpolatedFps;
        try
        {
            interpolatedFrames = checked((frames - 1) * config.Multiplier + 1);
            interpolatedFps = checked(fps * config.Multiplier);
        }
        catch (OverflowException)
        {
            throw new SwarmUserErrorException(
                "VideoStages: frame interpolation metadata exceeds the supported integer range.");
        }

        if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            && !g.UserInput.Get(T2IParamTypes.DoNotSave, false))
        {
            media.SaveOutput(
                g.CurrentVae,
                g.CurrentAudioVae,
                StableNodeIds.Id(g, StableNodeIds.PreInterpolationSave));
        }
        JArray interpolated = g.DoInterpolation(path, config.Method, config.Multiplier);
        g.CurrentMedia = media.WithPath(interpolated, WGNodeData.DT_VIDEO);
        g.CurrentMedia.Frames = interpolatedFrames;
        g.CurrentMedia.FPS = interpolatedFps;
    }

    private bool TryResolveConfiguration(
        out Configuration configuration,
        out string error)
    {
        configuration = default;
        error = null;
        if (!g.UserInput.TryGet(
                ComfyUIBackendExtension.VideoFrameInterpolationMultiplier,
                out int multiplier))
        {
            return false;
        }
        if (multiplier == 1)
        {
            return false;
        }
        if (multiplier is < 2 or > 10)
        {
            error = $"'Video Frame Interpolation Multiplier' must be between 2 and 10 when "
                + $"enabled, but was '{multiplier}'.";
            return false;
        }
        if (!g.UserInput.TryGet(
                ComfyUIBackendExtension.VideoFrameInterpolationMethod,
                out string method)
            || string.IsNullOrWhiteSpace(method))
        {
            error = "'Video Frame Interpolation Method' must be explicitly selected when the "
                + "multiplier is greater than 1.";
            return false;
        }
        if (method is not (Rife or Film or Gimm))
        {
            error = $"'Video Frame Interpolation Method' '{method}' is not supported. "
                + $"Choose {Rife}, {Film}, or {Gimm}.";
            return false;
        }
        configuration = new(method, multiplier);
        return true;
    }

    private static IEnumerable<string> RequiredFeatures(string method) =>
        method == Gimm
            ? [FrameInterpsFeature, GimmFeature]
            : [FrameInterpsFeature];

    private static PlanDiagnostic Refuse(string message) => new(
        PlanDiagnosticSeverity.Error,
        "timeline.frame-interpolation.unsupported",
        message);

    private readonly record struct Configuration(string Method, int Multiplier);
}
