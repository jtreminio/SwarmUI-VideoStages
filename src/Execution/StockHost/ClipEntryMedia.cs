using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

/// <summary>Points the generator at the media a clip's first stage starts from.</summary>
internal sealed class ClipEntryMedia(
    WorkflowGenerator g,
    HostVideoRootSources rootSources,
    string architectureLabel)
{
    private readonly InitVideoClipInstaller _installer = new(g);

    /// <summary>The clip's init-video footage, conformed to the timeline's dimensions.</summary>
    internal WGNodeData InstallInitVideo(
        ClipPlan clip,
        (int Width, int Height) dimensions,
        bool includeSourceAudio)
    {
        InitVideoPlan source = clip.InitVideo
            ?? throw Invariant.Failure(
                $"{architectureLabel} clip {clip.ClipId} has no init-video plan.");
        ClipPlan installPlan = clip with
        {
            InitVideo = source with
            {
                TargetWidth = dimensions.Width,
                TargetHeight = dimensions.Height,
            },
        };
        return _installer.TryInstall(installPlan, includeSourceAudio)
            ?? throw Invariant.Failure(
                $"clip {clip.ClipId} source video could not be installed.");
    }

    /// <summary>Sets the entry media for a clip that generates footage rather than refining it.</summary>
    internal void SelectGenerated(ClipPlan clip, WGNodeData authoredFirstFrame)
    {
        if (authoredFirstFrame is not null)
        {
            g.CurrentMedia = authoredFirstFrame;
            // The selected model supplies its own VAE during host preparation. Do not attach an
            // unrelated text-root donor VAE to the uploaded image.
            g.CurrentVae = null;
            return;
        }
        if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
        {
            // Upload materialization is deliberately runtime-owned. A missing or malformed first
            // image leaves the text-root plan unchanged and falls back to native text generation
            // without borrowing a host image.
            g.CurrentMedia = null;
            g.CurrentVae = null;
            return;
        }
        g.CurrentMedia = rootSources.Media?.Duplicate()
            ?? throw Invariant.Failure(
                $"clip {clip.ClipId} has no host image to generate from.");
        g.CurrentVae = rootSources.Vae?.Duplicate();
    }
}
