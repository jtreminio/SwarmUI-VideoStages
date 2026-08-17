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

    internal WGNodeData InstallInitVideo(
        ArchitectureClipRuntimeContext context,
        bool includeSourceAudio)
    {
        ClipPlan clip = context.Clip;
        if (clip.InitVideo is null)
        {
            throw Invariant.Failure(
                $"{architectureLabel} clip {clip.ClipId} has no init-video plan.");
        }
        return _installer.TryInstall(
            clip,
            context.PreviousTimelineClipOutput?.ToHostMedia(g),
            includeSourceAudio)
            ?? throw Invariant.Failure(
                $"clip {clip.ClipId} source video could not be installed.");
    }

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
