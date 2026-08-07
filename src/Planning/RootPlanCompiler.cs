using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>
/// The narrow bit of host state required to plan root ownership. It holds only immutable facts;
/// runtime media and graph nodes remain outside the plan compiler.
/// </summary>
internal sealed record RootEnvironment(
    HostRootKind HostKind,
    bool CanInterceptHostCore = false)
{
    internal static HostRootKind HostKindFor(TimelineSpec spec) =>
        spec.IsTextToVideo ? HostRootKind.TextToVideo : HostRootKind.ImageToVideo;

    internal static RootEnvironment FromSpec(
        TimelineSpec spec,
        bool canInterceptHostCore = false) =>
        new(HostKindFor(spec), canInterceptHostCore);
}

/// <summary>Plans ownership of the host root independently from clip and graph execution.</summary>
internal static class RootPlanCompiler
{
    internal static RootPlan Compile(RootEnvironment environment, IReadOnlyList<ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            return new RootPlan(
                HostKind: environment.HostKind,
                DiscardsRoot: false,
                UsesGeneratedClipDonor: false,
                InterceptsHostCore: false,
                UsesStageHandoff: false,
                DropsTextToVideoRootDonor: false);
        }

        bool hasGeneratedClip = clips.Any(clip => clip.InitVideo is null);
        bool firstClipHasInitVideo = clips[0].InitVideo is not null;
        bool initVideoLeadWithGeneratedClips = firstClipHasInitVideo && hasGeneratedClip;
        bool discardsRoot = environment.HostKind == HostRootKind.TextToVideo
            || !hasGeneratedClip;
        bool interceptsHostCore = discardsRoot || environment.CanInterceptHostCore;
        return new RootPlan(
            HostKind: environment.HostKind,
            DiscardsRoot: discardsRoot,
            UsesGeneratedClipDonor: !discardsRoot && initVideoLeadWithGeneratedClips,
            InterceptsHostCore: interceptsHostCore,
            UsesStageHandoff: interceptsHostCore && !firstClipHasInitVideo,
            DropsTextToVideoRootDonor: environment.HostKind == HostRootKind.TextToVideo
                && initVideoLeadWithGeneratedClips);
    }
}
