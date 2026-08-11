using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Wan;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Tests;

/// <summary>Explicit test fixture for pure plan tests that do not install host model metadata.</summary>
internal static class TestPlanCompiler
{
    internal static StageCorePlan DefaultStageCore { get; } = new(
        1,
        1,
        1,
        "",
        "",
        new(StageUpscaleMode.None, 1, "", ""),
        []);

    internal static VideoExecutionPlan Compile(TimelineSpec spec) =>
        Compile(spec, RootEnvironment.FromSpec(spec));

    internal static VideoExecutionPlan Compile(
        TimelineSpec spec,
        RootEnvironment rootEnvironment)
        => VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            ResolveLtx(spec));

    internal static ArchitecturePlanningResult ResolveLtx(TimelineSpec spec) =>
        Resolve(spec, _ => Ltx2ArchitectureModule.Instance);

    internal static ArchitecturePlanningResult Resolve(
        TimelineSpec spec,
        Func<ClipSpec, IVideoArchitectureModule> moduleFor,
        Func<ClipSpec, VideoArchitectureDescriptor> descriptorFor = null)
    {
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips ?? [])
        {
            if (clip.Stages is not { Count: > 0 })
            {
                clips[clip.Id] = new(
                    clip.Id,
                    null,
                    NoneArchitecture.Descriptor,
                    new Dictionary<int, ResolvedVideoModel>());
                continue;
            }
            IVideoArchitectureModule module = moduleFor(clip);
            VideoArchitectureDescriptor descriptor =
                descriptorFor?.Invoke(clip) ?? module.Descriptor;
            ModelProfileId profileId = ProfileFor(descriptor);
            clips[clip.Id] = new(
                clip.Id,
                module,
                descriptor,
                clip.Stages.ToDictionary(
                    stage => stage.ClipStageRawIndex,
                    stage => TestResolvedVideoModel.Create(stage.Model, profileId, descriptor)));
        }
        return new ArchitecturePlanningResult(clips, []);
    }

    private static ModelProfileId ProfileFor(VideoArchitectureDescriptor descriptor) =>
        descriptor.Id == Ltx2ArchitectureModule.ArchitectureId
            ? Ltx2ArchitectureModule.Ltx23ProfileId
            : descriptor.Id == WanArchitectureModule.ArchitectureId
                ? WanArchitectureModule.ImageToVideoProfileId
                : HostVideoArchitectureModule.ProfileId;
}
