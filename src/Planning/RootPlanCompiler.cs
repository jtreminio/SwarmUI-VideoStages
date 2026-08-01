namespace VideoStages.Planning;

/// <summary>Plans ownership of the host root independently from clip and graph execution.</summary>
internal static class RootPlanCompiler
{
    internal static RootPlan Compile(RootEnvironment environment, IReadOnlyList<ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            return new RootPlan(environment.HostKind, false, false, false);
        }

        bool hasGeneratedClip = clips.Any(clip => clip.InitVideo is null);
        bool initVideoLeadWithGeneratedClips = clips[0].InitVideo is not null && hasGeneratedClip;
        bool discardsRoot = environment.HostKind == HostRootKind.TextToVideoRoot
            || !hasGeneratedClip;
        return new RootPlan(
            environment.HostKind,
            discardsRoot,
            !discardsRoot && initVideoLeadWithGeneratedClips,
            discardsRoot || environment.CanHandoffHostCore);
    }
}
