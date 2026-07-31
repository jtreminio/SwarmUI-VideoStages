namespace VideoStages.Planning;

/// <summary>Plans ownership of the host root independently from clip and graph execution.</summary>
internal static class RootPlanCompiler
{
    internal static RootPlan Compile(RootEnvironment environment, IReadOnlyList<ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            return new RootPlan(environment.HostKind, RootUse.None, HostCoreDisposition.Keep,
                TimelineOutputDisposition.PreserveHostOutput, NativeAudioDisposition.KeepHostAudio);
        }

        bool hasGeneratedClip = clips.Any(clip => clip.SourceVideo is null);
        bool sourcedLeadWithGeneratedClips = clips[0].SourceVideo is not null && hasGeneratedClip;
        return new RootPlan(
            environment.HostKind,
            environment.HostKind == HostRootKind.TextToVideoRoot || !hasGeneratedClip
                ? RootUse.Discard
                : sourcedLeadWithGeneratedClips ? RootUse.GeneratedClipDonor : RootUse.ClipZeroSeed,
            environment.HostKind == HostRootKind.TextToVideoRoot || !hasGeneratedClip
                ? HostCoreDisposition.Drop
                : environment.CanHandoffHostCore ? HostCoreDisposition.Handoff : HostCoreDisposition.Keep,
            TimelineOutputDisposition.PublishTimelineOutput,
            environment.HostKind == HostRootKind.TextToVideoRoot || !hasGeneratedClip
                ? NativeAudioDisposition.DiscardWithRoot
                : NativeAudioDisposition.MakeAvailableToTimeline);
    }
}
