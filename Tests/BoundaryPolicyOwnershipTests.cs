using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// One typed policy owns boundary behavior: the catalog rule an architecture advertises and the
/// rule <see cref="BoundaryPlanCompiler"/> consumes are derived from the same modes.
/// </summary>
public class BoundaryPolicyOwnershipTests
{
    [Fact]
    public void EveryRegisteredArchitecture_AdvertisesExactlyTheRuleCompilationConsumes()
    {
        foreach (VideoArchitectureDescriptor descriptor in
            VideoArchitectureRegistry.Production.Catalog)
        {
            ArchitectureBoundaryPolicy policy = descriptor.BoundaryPolicy;
            Assert.NotNull(policy);
            Assert.Equal(
                Enum.GetValues<BoundaryExecutionMode>().Order(),
                policy.Modes.Keys.Order());
            foreach (BoundaryExecutionMode mode in Enum.GetValues<BoundaryExecutionMode>())
            {
                Assert.Equal(
                    policy.Modes[mode].ToRuleDecision(),
                    descriptor.BoundaryRules[mode]);
            }
        }
    }

    [Fact]
    public void CompilationHonorsTheAdvertisedConstraints_ForAModuleWithoutAPayloadPolicy()
    {
        // A module that only declares a descriptor: the advertised continue rule must still be
        // the rule that compiles, grid and continuity window included.
        ArchitectureBoundaryModePolicy continueMode = ArchitectureBoundaryModePolicy.Conditional(
            "fixture.boundary.continue",
            "Fixture grid.",
            new BoundaryRuleConstraints(
                FrameStep: 5,
                MinFrames: 10,
                MaxFrames: 30,
                DefaultFrames: 15,
                ContinuityExtraFrames: 3,
                TargetRequiresGeneratedEntry: false,
                TargetRequiresStage: false,
                TargetDisallowsInitialReference: false));
        VideoStagesSpec spec = new(640, 360, 24, false,
        [
            new ClipSpec(0, 49, Constants.AudioSourceNative, [], false, false, false, false,
                null, [], [Stage(10)]) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 22,
            },
            new ClipSpec(1, 49, Constants.AudioSourceNative, [], false, false, false, false,
                null, [], [Stage(11)]),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        ClipPlan[] planned =
        [
            plan.Clips[0] with
            {
                Architecture = plan.Clips[0].Architecture with
                {
                    BoundaryPolicy = new ArchitectureBoundaryPolicy(
                        new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
                        {
                            [BoundaryExecutionMode.Continue] = continueMode,
                        }),
                },
            },
            plan.Clips[1],
        ];

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, planned).Boundaries);

        Assert.Equal(BoundaryExecutionMode.Continue, boundary.Effective);
        Assert.Equal(continueMode.NormalizeOverlap(22), boundary.OverlapFrames);
        Assert.Equal(20, boundary.OverlapFrames);
        Assert.Equal(continueMode.Constraints.FrameStep, boundary.FrameStep);
        Assert.Equal(continueMode.Constraints.MinFrames, boundary.MinFrames);
        Assert.Equal(
            boundary.OverlapFrames + continueMode.Constraints.ContinuityExtraFrames,
            boundary.ContinuityWindowFrames);
    }

    private static StageSpec Stage(int id) =>
        new(id, 1, 1, "pixel-lanczos", "ltx-2", 12, 4.5, "euler", "normal", "Generated",
            ClipStageIndex: id - 10,
            ClipStageRawIndex: id - 10);
}
