using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ArchitectureRuntimeOwnershipTests
{
    [Fact]
    public void Request_context_caches_active_architectures_and_root_owner()
    {
        VideoExecutionPlanContext context = new(MixedInitVideoLeadingPlan());

        Assert.Equal(
            [new ArchitectureId("init-video-arch"), new ArchitectureId("future-arch")],
            context.ActiveArchitectureIds);
        Assert.Equal(
            new ArchitectureId("future-arch"),
            context.RootOwnerArchitectureId);
    }

    [Fact]
    public void InitVideo_leading_architecture_does_not_claim_exclusive_root_phases()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = PreparedHost(
            generator,
            plan,
            [initVideoClip, future]);

        host.DispatchHostPhase(ArchitectureHostPhase.DropCoreOutput);

        Assert.Empty(initVideoClip.HostPhases);
        ArchitectureHostPhaseContext rootPhase = Assert.Single(future.HostPhases);
        Assert.Equal(new ArchitectureId("future-arch"), rootPhase.RootOwnerArchitectureId);
    }

    [Fact]
    public void Fan_out_host_phase_reaches_all_active_architectures_but_keeps_one_root_owner()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = PreparedHost(
            generator,
            plan,
            [initVideoClip, future]);

        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors);

        ArchitectureHostPhaseContext initVideoPhase = Assert.Single(initVideoClip.HostPhases);
        ArchitectureHostPhaseContext futurePhase = Assert.Single(future.HostPhases);
        Assert.Equal(new ArchitectureId("future-arch"), initVideoPhase.RootOwnerArchitectureId);
        Assert.Equal(initVideoPhase, futurePhase);
    }

    [Fact]
    public void Timeline_preparation_uses_the_same_init_video_aware_root_owner()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        RecordingFactory initVideoClip = new(new("init-video-arch"));
        RecordingFactory future = new(new("future-arch"));
        ArchitectureRuntimeSessionFactoryRegistry registry = new(
            [initVideoClip, future],
            new VideoExecutionPlanContext(plan));
        AudioRuntimeSources audio = EmptyAudio();
        RootExecutionPolicy policy = new(plan);

        registry.PrepareTimeline(new(plan, audio, policy));

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
        VideoExecutionPlanContext request = new(plan, _ => host);

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
        Assert.Empty(initVideoClip.HostPhases);
        Assert.Empty(future.HostPhases);
        Assert.Same(beforeMedia, generator.CurrentMedia);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
    }

    [Fact]
    public void Prepared_context_binds_active_providers_once_and_reuses_them_for_host_phases()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        List<string> calls = [];
        RecordingProvider initVideoClip = new(new("init-video-arch"), calls);
        RecordingProvider future = new(new("future-arch"), calls);
        WorkflowGenerator generator = Generator();
        int bindingCount = 0;
        VideoExecutionPlanContext request = new(
            plan,
            _ =>
            {
                bindingCount++;
                return new(generator, plan, [initVideoClip, future]);
            });

        request.PrepareRequest();
        request.PrepareRequest();
        VideoArchitectureExecutionHost host = request.RequirePreparedExecutionHost();
        host.DispatchHostPhase(ArchitectureHostPhase.CaptureBaseReference);
        host.DispatchHostPhase(ArchitectureHostPhase.CaptureRefinerReference);

        Assert.Equal(1, bindingCount);
        Assert.Equal(
            ["preflight:init-video-arch", "preflight:future-arch"],
            calls);
        Assert.Equal(2, initVideoClip.HostPhases.Count);
        Assert.Equal(2, future.HostPhases.Count);
    }

    [Fact]
    public void Unprepared_host_rejects_mutation_at_the_host_boundary()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = new(
            Generator(),
            plan,
            [initVideoClip, future]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            host.DispatchHostPhase(ArchitectureHostPhase.CaptureBaseReference));

        Assert.Contains("not bound to a prepared", error.Message);
        Assert.Empty(initVideoClip.HostPhases);
        Assert.Empty(future.HostPhases);
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
    public void Host_phase_failure_is_sticky_and_blocks_later_mutation()
    {
        VideoExecutionPlan plan = MixedInitVideoLeadingPlan();
        InvalidOperationException phaseFailure = new("provider mutation failed");
        RecordingProvider initVideoClip = new(new("init-video-arch"));
        RecordingProvider future = new(
            new("future-arch"),
            hostPhaseFailure: phaseFailure);
        WorkflowGenerator generator = Generator();
        VideoArchitectureExecutionHost host = new(
            generator,
            plan,
            [initVideoClip, future]);
        VideoExecutionPlanContext request = new(plan, _ => host);
        request.PrepareRequest();

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() =>
            host.DispatchHostPhase(ArchitectureHostPhase.CaptureBaseReference));
        InvalidOperationException repeated = Assert.Throws<InvalidOperationException>(() =>
            host.DispatchHostPhase(ArchitectureHostPhase.CaptureRefinerReference));

        Assert.Same(phaseFailure, first);
        Assert.Same(first, repeated);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Single(initVideoClip.HostPhases);
        Assert.Single(future.HostPhases);
    }

    private static VideoExecutionPlan MixedInitVideoLeadingPlan()
    {
        VideoArchitectureDescriptor initVideoArchitecture = Descriptor("init-video-arch");
        VideoArchitectureDescriptor futureArchitecture = Descriptor("future-arch");
        ClipPlan initVideoClip = Clip(
            0,
            ClipInputKind.InitVideo,
            StageInputKind.InitVideo,
            initVideoArchitecture,
            hasInitVideo: true);
        ClipPlan generated = Clip(
            1,
            ClipInputKind.RootMedia,
            StageInputKind.RootMedia,
            futureArchitecture,
            hasInitVideo: false);
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.ImageToVideo,
                RootUse.GeneratedClipDonor,
                HostCoreDisposition.Handoff,
                NativeAudioDisposition.MakeAvailableToTimeline),
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
        ClipInputKind clipInput,
        StageInputKind stageInput,
        VideoArchitectureDescriptor architecture,
        bool hasInitVideo)
    {
        TestPayload payload = new(architecture.Id);
        StagePlan stage = new(
            id,
            0,
            0,
            stageInput,
            IsPassthrough: false,
            payload,
            new(
                IsTimelineTerminal: false,
                IntermediateOutputEligibility.NotEligible,
                PreserveConfiguredAudioTrackSave: false));
        return new(
            id,
            25,
            clipInput,
            hasInitVideo,
            hasInitVideo ? new("data", "source.mp4", 0, 512, 512, 24) : null,
            [stage],
            Audio: null)
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

    private static AudioRuntimeSources EmptyAudio() => new(
        null,
        new Dictionary<int, WGNodeData>(),
        new Dictionary<int, WGNodeData>());

    private static VideoArchitectureExecutionHost PreparedHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers)
    {
        VideoArchitectureExecutionHost host = new(generator, plan, providers);
        VideoExecutionPlanContext request = new(plan, _ => host);
        request.PrepareRequest();
        return request.RequirePreparedExecutionHost();
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
        Exception hostPhaseFailure = null) :
        IArchitectureGenerationSessionFactoryProvider,
        IArchitectureHostPhaseParticipant
    {
        public ArchitectureId ArchitectureId => architectureId;

        internal List<ArchitectureHostPhaseContext> HostPhases { get; } = [];

        public IReadOnlyList<PlanDiagnostic> PreflightRequest(
            ArchitectureRequestPreflightContext context)
        {
            calls?.Add($"preflight:{architectureId}");
            return preflightError is null
                ? []
                : [new(PlanDiagnosticSeverity.Error, "test.preflight", preflightError)];
        }

        public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
        {
            HostPhases.Add(context);
            if (hostPhaseFailure is not null)
            {
                throw hostPhaseFailure;
            }
        }

        public IArchitectureGenerationSessionFactory CreateFactory() =>
            new RecordingFactory(architectureId, calls);
    }

    private sealed class RecordingFactory(
        ArchitectureId architectureId,
        ICollection<string> calls = null) : IArchitectureGenerationSessionFactory
    {
        public ArchitectureId ArchitectureId => architectureId;

        internal List<bool> RootOwnership { get; } = [];

        public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
        {
            calls?.Add($"prepare:{architectureId}");
            RootOwnership.Add(context.OwnsGeneratedRoot);
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            throw new NotSupportedException();
    }
}
