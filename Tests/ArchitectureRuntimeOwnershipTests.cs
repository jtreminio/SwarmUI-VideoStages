using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ArchitectureRuntimeOwnershipTests
{
    [Fact]
    public void Request_context_caches_root_owner()
    {
        // Root-owner resolution happens in the constructor, so this context is never prepared.
        VideoExecutionPlanContext context = new(
            MixedInitVideoLeadingPlan(),
            () => throw new InvalidOperationException("This test never prepares the context."));

        Assert.Equal(
            new ArchitectureId("future-arch"),
            context.RootOwnerArchitectureId);
    }

    [Fact]
    public void InitVideo_leading_architecture_does_not_claim_root_audio_mask_sizing()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoExecutionPlanContext request = PreparedContext(
            generator,
            plan,
            [initVideoClip, future]);

        request.ApplyRootAudioMaskDimensions();

        Assert.Empty(initVideoClip.LifecycleCalls);
        Assert.Equal(["audio-mask"], future.LifecycleCalls);
    }

    [Fact]
    public void ControlNet_capture_reaches_all_active_architectures_with_one_root_owner()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoExecutionPlanContext request = PreparedContext(
            generator,
            plan,
            [initVideoClip, future]);

        request.CaptureControlNetPreprocessors();

        Assert.Equal(["control-net"], initVideoClip.LifecycleCalls);
        Assert.Equal(["control-net"], future.LifecycleCalls);
        Assert.Equal([false], initVideoClip.ControlNetRootOwnership);
        Assert.Equal([true], future.ControlNetRootOwnership);
    }

    [Fact]
    public void Timeline_session_creation_uses_the_same_init_video_aware_root_owner()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoExecutionPlanContext request = PreparedContext(
            Generator(),
            plan,
            [initVideoClip, future]);

        Assert.Throws<NotSupportedException>(() => request.RunConfiguredStages());

        Assert.Equal([false], initVideoClip.RootOwnership);
        Assert.Equal([true], future.RootOwnership);
    }

    [Fact]
    public void Request_preflight_asks_every_active_architecture_and_fails_closed_without_mutating()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        List<string> calls = [];
        RecordingProvider initVideoClip = new(new("init-video-arch"), calls);
        RecordingProvider future = new(
            new("future-arch"),
            calls,
            preflightError: "future runtime unavailable");
        WorkflowGenerator generator = Generator();
        JObject before = (JObject)generator.Workflow.DeepClone();
        WGNodeData beforeMedia = generator.CurrentMedia;
        VideoArchitectureExecutionHost host = new(generator, plan, [initVideoClip, future]);
        VideoExecutionPlanContext request = new(plan, () => host);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            request.PrepareRequest());
        SwarmUserErrorException repeated = Assert.Throws<SwarmUserErrorException>(() =>
            request.PrepareRequest());

        Assert.Contains("future runtime unavailable", error.Message);
        Assert.Same(error, repeated);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Single(
            request.PreflightDiagnostics,
            diagnostic => diagnostic.Code == "test.preflight");
        Assert.Equal(["preflight:init-video-arch", "preflight:future-arch"], calls);
        Assert.Empty(initVideoClip.LifecycleCalls);
        Assert.Empty(future.LifecycleCalls);
        Assert.Same(beforeMedia, generator.CurrentMedia);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
    }

    [Fact]
    public void Prepared_context_reuses_active_providers_for_lifecycle_calls()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        List<string> calls = [];
        RecordingProvider initVideoClip = new(new("init-video-arch"), calls);
        RecordingProvider future = new(new("future-arch"), calls);
        WorkflowGenerator generator = Generator();
        int bindingCount = 0;
        VideoExecutionPlanContext request = new(
            plan,
            () =>
            {
                bindingCount++;
                return new(generator, plan, [initVideoClip, future]);
            });

        request.PrepareRequest();
        request.PrepareRequest();
        request.CaptureBaseReference();
        request.CaptureRefinerReference();

        Assert.Equal(1, bindingCount);
        Assert.Equal(
            ["preflight:init-video-arch", "preflight:future-arch"],
            calls);
        Assert.Equal(["base-reference", "refiner-reference"], initVideoClip.LifecycleCalls);
        Assert.Equal(["base-reference", "refiner-reference"], future.LifecycleCalls);
    }

    [Fact]
    public void Unprepared_context_rejects_mutation_at_the_context_boundary()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = new(
            Generator(),
            plan,
            [initVideoClip, future]);

        VideoExecutionPlanContext request = new(plan, () => host);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            request.CaptureBaseReference());

        Assert.Contains("preflight must complete first", error.Message);
        Assert.Empty(initVideoClip.LifecycleCalls);
        Assert.Empty(future.LifecycleCalls);
    }

    [Fact]
    public void PrepareRequest_rejects_an_execution_host_for_a_different_plan()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        VideoExecutionPlan otherPlan = MixedInitVideoLeadingPlan();
        VideoArchitectureExecutionHost host = new(
            Generator(),
            otherPlan,
            [
                new RecordingProvider(new("init-video-arch")),
                new RecordingProvider(new("future-arch")),
            ]);
        VideoExecutionPlanContext request = new(plan, () => host);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            request.PrepareRequest);

        Assert.Contains("different video execution plan", error.Message);
        Assert.Equal(VideoExecutionState.Failed, request.State);
    }

    [Fact]
    public void Execution_host_rejects_duplicate_runtime_provider_bindings()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        RecordingProvider first = new(new("init-video-arch"));
        RecordingProvider duplicate = new(new("init-video-arch"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureExecutionHost(
                Generator(),
                plan,
                [first, duplicate]));

        Assert.Contains("Duplicate generation runtime provider", error.Message);
    }

    [Fact]
    public void Lifecycle_failure_is_sticky_and_blocks_later_mutation()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        InvalidOperationException phaseFailure = new("provider mutation failed");
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(
            new("future-arch"),
            lifecycleFailure: phaseFailure);
        WorkflowGenerator generator = Generator();
        VideoArchitectureExecutionHost host = new(
            generator,
            plan,
            [initVideoClip, future]);
        VideoExecutionPlanContext request = new(plan, () => host);
        request.PrepareRequest();

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() =>
            request.CaptureBaseReference());
        InvalidOperationException repeated = Assert.Throws<InvalidOperationException>(() =>
            request.CaptureRefinerReference());

        Assert.Same(phaseFailure, first);
        Assert.Same(first, repeated);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Single(initVideoClip.LifecycleCalls);
        Assert.Single(future.LifecycleCalls);
    }

    private static VideoExecutionPlan MixedInitVideoLeadingPlan()
    {
        VideoArchitectureDescriptor initVideoArchitecture = Descriptor("init-video-arch");
        VideoArchitectureDescriptor futureArchitecture = Descriptor("future-arch");
        ClipPlan initVideoClip = Clip(
            0,
            ArchitectureEntryMode.InitVideo,
            StageInputKind.InitVideo,
            initVideoArchitecture);
        ClipPlan generated = Clip(
            1,
            ArchitectureEntryMode.ImageToVideo,
            StageInputKind.RootMedia,
            futureArchitecture);
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.ImageToVideo,
                IgnoresHostRootOutput: false,
                UsesGeneratedClipDonor: true,
                InterceptsHostCore: true,
                UsesStageHandoff: false,
                DropsTextToVideoRootDonor: false),
            [initVideoClip, generated],
            [
                new(
                    0,
                    BoundaryJoinType.Cut,
                    0,
                    0,
                    BoundaryFallbackReason.None)
            ],
            []);
    }

    private static ClipPlan Clip(
        int id,
        ArchitectureEntryMode entryMode,
        StageInputKind stageInput,
        VideoArchitectureDescriptor architecture)
    {
        TestPayload payload = new(architecture.Id);
        StagePlan stage = new(
            id,
            0,
            0,
            stageInput,
            IsPassthrough: false,
            payload,
            IsIntermediateStage: false);
        return new(
            id,
            25,
            entryMode,
            entryMode == ArchitectureEntryMode.InitVideo
                ? new("data:video/mp4;base64,QUJD", "source.mp4", 0, 512, 512, 24)
                : null,
            [stage],
            Audio: new(
                new(AudioSourceKind.Disabled, null, false, null),
                AudioLengthOwner.Timeline,
                [],
                []),
            SavesAudioTrack: false)
        {
            Architecture = architecture,
            ArchitecturePayload = payload,
        };
    }

    private static VideoArchitectureDescriptor Descriptor(string id)
    {
        ArchitectureId architectureId = new(id);
        return new(
            architectureId,
            id,
            [AudioSourceKind.Native],
            [ArchitectureEntryMode.ImageToVideo, ArchitectureEntryMode.InitVideo],
            ArchitectureFeature.None,
            new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryJoinType, RuleDecision>()));
    }

    private static WorkflowGenerator Generator()
    {
        T2IParamInput input = new(null);
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            bridge.AddStub("UnitTest_RootVideo", "root")
                .WithOutputs(WGNodeData.DT_IMAGE);
        }
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            Workflow = workflow,
        };
        generator.CurrentMedia = new(
            new JArray("root", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null);
        return generator;
    }

    private static VideoExecutionPlanContext PreparedContext(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionProvider> providers)
    {
        VideoArchitectureExecutionHost host = new(generator, plan, providers);
        VideoExecutionPlanContext request = new(plan, () => host);
        request.PrepareRequest();
        return request;
    }

    private sealed record TestPayload(ArchitectureId ArchitectureId) :
        IArchitectureClipPayload,
        IArchitectureStagePayload
    {
        public StageCorePlan Core => TestPlanCompiler.DefaultStageCore;
    }

    private sealed class RecordingProvider(
        ArchitectureId architectureId,
        ICollection<string> calls = null,
        string preflightError = null,
        Exception lifecycleFailure = null) :
        IArchitectureGenerationSessionProvider
    {
        public ArchitectureId ArchitectureId => architectureId;

        internal List<string> LifecycleCalls { get; } = [];

        internal List<bool> ControlNetRootOwnership { get; } = [];

        internal List<bool> RootOwnership { get; } = [];

        public IReadOnlyList<PlanDiagnostic> PreflightRequest(
            ArchitectureRequestPreflightContext context)
        {
            calls?.Add($"preflight:{architectureId}");
            return preflightError is null
                ? []
                : [new(PlanDiagnosticSeverity.Error, "test.preflight", preflightError)];
        }

        public void CaptureControlNetPreprocessors(bool ownsHostRoot)
        {
            ControlNetRootOwnership.Add(ownsHostRoot);
            Record("control-net");
        }

        public void CaptureBaseReference(VideoExecutionPlan plan) => Record("base-reference");

        public void CaptureRefinerReference(VideoExecutionPlan plan) =>
            Record("refiner-reference");

        public void ApplyRootAudioMaskDimensions() => Record("audio-mask");

        private void Record(string call)
        {
            LifecycleCalls.Add(call);
            if (lifecycleFailure is not null)
            {
                throw lifecycleFailure;
            }
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context)
        {
            RootOwnership.Add(context.OwnsGeneratedRoot);
            return new RecordingSession(architectureId);
        }
    }

    private sealed class RecordingSession(ArchitectureId architectureId) :
        IVideoGenerationSession
    {
        public ArchitectureId ArchitectureId => architectureId;

        public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context) =>
            throw new NotSupportedException();
    }
}
