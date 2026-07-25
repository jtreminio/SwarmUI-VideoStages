using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
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
    public void Sourced_leading_architecture_does_not_claim_exclusive_root_phases()
    {
        VideoExecutionPlan plan = MixedSourcedLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider sourced = new(new("sourced-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = new(generator, [sourced, future]);

        host.DispatchHostPhase(ArchitectureHostPhase.DropCoreOutput, plan);

        Assert.Empty(sourced.HostPhases);
        ArchitectureHostPhaseContext rootPhase = Assert.Single(future.HostPhases);
        Assert.Equal(ArchitectureHostPhaseScope.RootOwnerOnly, rootPhase.Scope);
        Assert.Equal(new ArchitectureId("future-arch"), rootPhase.RootOwnerArchitectureId);
        Assert.Same(future.Resizer, host.GetRootMediaResizer(plan));
    }

    [Fact]
    public void Fan_out_host_phase_reaches_all_active_architectures_but_keeps_one_root_owner()
    {
        VideoExecutionPlan plan = MixedSourcedLeadingPlan();
        WorkflowGenerator generator = Generator();
        RecordingProvider sourced = new(new("sourced-arch"));
        RecordingProvider future = new(new("future-arch"));
        VideoArchitectureExecutionHost host = new(generator, [sourced, future]);

        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors, plan);

        ArchitectureHostPhaseContext sourcedPhase = Assert.Single(sourced.HostPhases);
        ArchitectureHostPhaseContext futurePhase = Assert.Single(future.HostPhases);
        Assert.Equal(ArchitectureHostPhaseScope.AllActiveArchitectures, sourcedPhase.Scope);
        Assert.Equal(new ArchitectureId("future-arch"), sourcedPhase.RootOwnerArchitectureId);
        Assert.Equal(sourcedPhase, futurePhase);
    }

    [Fact]
    public void Timeline_preparation_uses_the_same_sourced_aware_root_owner()
    {
        VideoExecutionPlan plan = MixedSourcedLeadingPlan();
        RecordingFactory sourced = new(new("sourced-arch"));
        RecordingFactory future = new(new("future-arch"));
        ArchitectureRuntimeSessionFactoryRegistry registry = new([sourced, future]);
        AudioRuntimeSources audio = EmptyAudio();
        RootExecutionPolicy policy = new(plan);

        registry.PrepareTimeline(new(plan, audio, policy));

        Assert.Equal([false], sourced.RootOwnership);
        Assert.Equal([true], future.RootOwnership);
    }

    [Fact]
    public void Request_preflight_asks_every_active_architecture_and_fails_closed_without_mutating()
    {
        VideoExecutionPlan plan = MixedSourcedLeadingPlan();
        List<string> calls = [];
        RecordingProvider sourced = new(new("sourced-arch"), calls);
        RecordingProvider future = new(
            new("future-arch"),
            calls,
            preflightError: "future runtime unavailable");
        WorkflowGenerator generator = Generator(withRefineSource: true);
        JObject before = (JObject)generator.Workflow.DeepClone();
        WGNodeData beforeMedia = generator.CurrentMedia;
        VideoArchitectureExecutionHost host = new(generator, [sourced, future]);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            host.PreflightRequest(plan));

        Assert.Contains("future runtime unavailable", error.Message);
        Assert.Equal(["preflight:sourced-arch", "preflight:future-arch"], calls);
        Assert.Empty(sourced.HostPhases);
        Assert.Empty(future.HostPhases);
        Assert.Same(beforeMedia, generator.CurrentMedia);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
    }

    private static VideoExecutionPlan MixedSourcedLeadingPlan()
    {
        VideoArchitectureDescriptor sourcedArchitecture = Descriptor("sourced-arch");
        VideoArchitectureDescriptor futureArchitecture = Descriptor("future-arch");
        ClipPlan sourced = Clip(
            0,
            ClipInputKind.SourceVideo,
            StageInputKind.SourceVideo,
            sourcedArchitecture,
            isSourced: true);
        ClipPlan generated = Clip(
            1,
            ClipInputKind.RootMedia,
            StageInputKind.RootMedia,
            futureArchitecture,
            isSourced: false);
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.ImageToVideo,
                RootUse.GeneratedClipDonor,
                HostCoreDisposition.Handoff,
                TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition.MakeAvailableToTimeline),
            [sourced, generated],
            [
                new(
                    0,
                    BoundaryExecutionMode.Cut,
                    BoundaryExecutionMode.Cut,
                    0,
                    0,
                    RequiresRuntimeMergeValidation: false,
                    BoundaryFallback.None)
            ],
            []);
    }

    private static ClipPlan Clip(
        int id,
        ClipInputKind clipInput,
        StageInputKind stageInput,
        VideoArchitectureDescriptor architecture,
        bool isSourced)
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
                IntermediateOutputPolicy.NotEligible,
                PreserveConfiguredAudioTrackSave: false));
        return new(
            id,
            25,
            clipInput,
            isSourced,
            isSourced ? new("data", "source.mp4", 0, 512, 512, 24) : null,
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
        ModelProfileId profileId = new($"{id}-profile");
        return new(
            architectureId,
            id,
            profileId,
            [ArchitectureEntryMode.ImageToVideo, ArchitectureEntryMode.SourceVideo],
            [AudioSourceKind.Native],
            [new(profileId, profileId.Value, ModelProfileCapability.None, [])],
            new(
                ArchitectureCapability.GeneratedEntry | ArchitectureCapability.SourcedEntry,
                ClipCapability.SourceVideo,
                StageCapability.ImageInput | StageCapability.VideoInput,
                OutputCapability.Video),
            new Dictionary<BoundaryExecutionMode, RuleDecision>());
    }

    private static WorkflowGenerator Generator(bool withRefineSource = false)
    {
        T2IParamInput input = new(null);
        if (withRefineSource)
        {
            input.Set(
                VideoStagesExtension.RefineSourceVideo,
                new Image([0x01, 0x02, 0x03], MediaType.VideoMp4));
        }
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

    private sealed record TestPayload(ArchitectureId ArchitectureId) :
        IArchitectureClipPayload,
        IArchitectureStagePayload;

    private sealed class RecordingProvider(
        ArchitectureId architectureId,
        ICollection<string> calls = null,
        string preflightError = null) :
        IArchitectureGenerationSessionFactoryProvider,
        IArchitectureHostPhaseParticipant,
        IArchitectureRootMediaResizerProvider
    {
        public ArchitectureId ArchitectureId => architectureId;

        internal List<ArchitectureHostPhaseContext> HostPhases { get; } = [];

        internal RecordingResizer Resizer { get; } = new();

        public IReadOnlyList<PlanDiagnostic> PreflightRequest(
            ArchitectureRequestPreflightContext context)
        {
            calls?.Add($"preflight:{architectureId}");
            return preflightError is null
                ? []
                : [new(PlanDiagnosticSeverity.Error, "test.preflight", preflightError)];
        }

        public void ExecuteHostPhase(ArchitectureHostPhaseContext context) =>
            HostPhases.Add(context);

        public IArchitectureGenerationSessionFactory CreateFactory() =>
            new RecordingFactory(architectureId, calls);

        public IArchitectureRootMediaResizer CreateRootMediaResizer() => Resizer;
    }

    private sealed class RecordingFactory(
        ArchitectureId architectureId,
        ICollection<string> calls = null) : IArchitectureGenerationSessionFactory
    {
        public ArchitectureId ArchitectureId => architectureId;

        public IArchitectureBoundaryAssembler BoundaryAssembler => null;

        internal List<bool> RootOwnership { get; } = [];

        public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
        {
            calls?.Add($"prepare:{architectureId}");
            RootOwnership.Add(context.OwnsGeneratedRoot);
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            throw new NotSupportedException();

        public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
        {
        }
    }

    private sealed class RecordingResizer : IArchitectureRootMediaResizer
    {
        public bool TryGetRootStageResolution(out int width, out int height)
        {
            width = 0;
            height = 0;
            return false;
        }

        public void ApplyConfiguredRootStageResolutionToCurrentMedia()
        {
        }

        public void ApplyConfiguredRootStageResolutionToSurvivingRootMedia()
        {
        }

        public void ApplyCurrentMediaResolution(int width, int height)
        {
        }

        public void SetCurrentMediaDimensions(int width, int height)
        {
        }
    }
}
