using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// One typed policy owns boundary behavior: the catalog rule an architecture advertises and the
/// rule <see cref="BoundaryPlanCompiler"/> consumes are the same rule.
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
                Enum.GetValues<BoundaryJoinType>().Order(),
                policy.Rules.Keys.Order());
            foreach (BoundaryJoinType mode in Enum.GetValues<BoundaryJoinType>())
            {
                Assert.Equal(policy.Rules[mode], descriptor.BoundaryRules[mode]);
            }
        }
    }

    [Fact]
    public void CompilationHonorsTheAdvertisedConstraints_ForAModuleWithoutAPayloadPolicy()
    {
        // A module that only declares a descriptor: the advertised continue rule must still be
        // the rule that compiles, grid and continuity window included.
        RuleDecision continueMode = RuleDecision.Conditional(
            "fixture.boundary.continue",
            "Fixture grid.",
            RuleScope.Boundary,
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
                        new Dictionary<BoundaryJoinType, RuleDecision>
                        {
                            [BoundaryJoinType.Continue] = continueMode,
                        }),
                },
            },
            plan.Clips[1],
        ];

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, planned).Boundaries);

        Assert.Equal(BoundaryJoinType.Continue, boundary.Effective);
        Assert.Equal(
            BoundaryPlanCompiler.NormalizeOverlap(continueMode, 22),
            boundary.OverlapFrames);
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
