namespace VideoStages.Planning;

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
                DropsTextToVideoRootDonor: false,
                DiscardsTextToVideoRoot: false);
        }

        bool hasGeneratedClip = clips.Any(clip => clip.InitVideo is null);
        bool firstClipHasInitVideo = clips[0].InitVideo is not null;
        bool initVideoLeadWithGeneratedClips = firstClipHasInitVideo && hasGeneratedClip;
        bool discardsRoot = environment.HostKind == HostRootKind.TextToVideoRoot
            || !hasGeneratedClip;
        bool interceptsHostCore = discardsRoot || environment.CanHandoffHostCore;
        return new RootPlan(
            HostKind: environment.HostKind,
            DiscardsRoot: discardsRoot,
            UsesGeneratedClipDonor: !discardsRoot && initVideoLeadWithGeneratedClips,
            InterceptsHostCore: interceptsHostCore,
            UsesStageHandoff: interceptsHostCore && !firstClipHasInitVideo,
            DropsTextToVideoRootDonor: environment.HostKind == HostRootKind.TextToVideoRoot
                && initVideoLeadWithGeneratedClips,
            DiscardsTextToVideoRoot: environment.HostKind == HostRootKind.TextToVideoRoot
                && discardsRoot);
    }
}
