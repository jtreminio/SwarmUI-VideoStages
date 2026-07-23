using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Planning;

namespace VideoStages.Tests;

/// <summary>Explicit test fixture for pure plan tests that do not install host model metadata.</summary>
internal static class TestPlanCompiler
{
    internal static VideoExecutionPlan Compile(VideoStagesSpec spec) =>
        Compile(spec, RootEnvironment.FromSpec(spec));

    internal static VideoExecutionPlan Compile(
        VideoStagesSpec spec,
        RootEnvironment rootEnvironment)
        => VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            ResolveLtx(spec));

    internal static ArchitecturePlanningResult ResolveLtx(VideoStagesSpec spec)
    {
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor;
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips ?? [])
        {
            if (clip.Stages is not { Count: > 0 })
            {
                clips.TryAdd(clip.Id, new(
                    clip.Id,
                    NoneArchitectureModule.Instance,
                    NoneArchitecture.Descriptor,
                    new Dictionary<int, ResolvedVideoModel>()));
                continue;
            }
            Dictionary<int, ResolvedVideoModel> stages = [];
            foreach (StageSpec stage in clip.Stages)
            {
                stages[stage.ClipStageRawIndex] = new(
                    stage.Model,
                    descriptor.Id,
                    new("ltx-2.3"),
                    "ltxv2",
                    descriptor);
            }
            clips.TryAdd(clip.Id, new(
                clip.Id,
                Ltx2ArchitectureModule.Instance,
                descriptor,
                stages));
        }
        return new ArchitecturePlanningResult(clips, []);
    }
}
