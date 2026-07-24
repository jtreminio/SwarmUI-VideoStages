using System.Text.Json;
using VideoStages.Planning;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

public class PlanningCompilerComponentTests
{
    [Fact]
    public void RootPlanCompiler_PlansGlobalRefineOwnershipWithoutClipExecutionState()
    {
        RootPlan plan = RootPlanCompiler.Compile(
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true, HasGlobalRefineSource: true),
            [GeneratedClip(0, Stage(10))]);

        Assert.Equal(HostRootKind.GlobalRefineSource, plan.HostKind);
        Assert.Equal(RootUse.GlobalRefineReplacement, plan.Use);
        Assert.Equal(HostCoreDisposition.Handoff, plan.CoreDisposition);
        Assert.Equal(NativeAudioDisposition.UseGlobalRefineAudio, plan.NativeAudioDisposition);
    }

    [Fact]
    public void BoundaryPlanCompiler_ReportsContinueFallbackAsImmutableResult()
    {
        VideoStagesSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 16,
                BoundaryOutCarryAudio = true,
            },
            SourcedClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        BoundaryPlanningResult result = BoundaryPlanCompiler.Compile(spec.Clips, plan.Clips);

        BoundaryPlan boundary = Assert.Single(result.Boundaries);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(BoundaryFallback.TargetIsSourcedVideo, boundary.Fallback);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.False(boundary.CarryAudio);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-targetissourcedvideo");
    }

    [Fact]
    public void BoundaryPlanCompiler_CarriesAudioOnlyForEffectiveOverlappedBoundary()
    {
        VideoStagesSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutCrossfade,
                BoundaryOutOverlap = 16,
                BoundaryOutCarryAudio = true,
            },
            GeneratedClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, plan.Clips).Boundaries);

        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Effective);
        Assert.True(boundary.CarryAudio);
    }

    [Fact]
    public void BoundaryPlanCompiler_UsesArchitecturePolicyForRequirementsAndFrameGrid()
    {
        VideoStagesSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 18,
            },
            SourcedClip(1, Stage(11)) with
            {
                ImageRefs = [new ImageRefSpec("Upload", 1, false, "first.png")],
            },
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        FakeBoundaryPolicy policy = new();
        ClipPlan[] planned =
        [
            plan.Clips[0] with
            {
                ArchitecturePayload = new FakeBoundaryPayload(policy),
            },
            plan.Clips[1],
        ];

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, planned).Boundaries);

        Assert.Equal(BoundaryExecutionMode.Continue, boundary.Effective);
        Assert.Equal(15, boundary.OverlapFrames);
        Assert.Equal(18, boundary.ContinuityWindowFrames);
        Assert.Equal(5, boundary.FrameStep);
    }

    [Fact]
    public void FocusedReferencePromptAndLoraCompilers_PlanStageAdornment()
    {
        StageSpec stage = Stage(11, rawIndex: 1) with
        {
            ImageReference = "edit4",
            ImageRefStrengths = [0.25],
            ControlNetStrength = 0.6,
            Loras = [new LoraRef("stage", 0.5)],
        };
        ClipSpec clip = GeneratedClip(0, Stage(10), stage) with
        {
            Loras = [new LoraRef("clip", 0.75)],
            PromptWindows = [new PromptWindowSpec("prompt", 0, 1)],
            ImageRefs = [new ImageRefSpec("Upload", 1, false, "ref.png", "data:image/png;base64,QQ==")],
            IcLoras =
            [
                new IcLoraSpec(
                    IcLoraWeights.AutoModelToken,
                    Constants.ControlNetSourceTwo,
                    1,
                    1,
                    Constants.IcLoraControlCanny,
                    null,
                    DriveData: IcLoraDriveData.Visual,
                    Stage: 1),
            ],
        };

        PromptRelayPlan prompt = PromptRelayPlanCompiler.Compile(clip, 24);
        GuideReferencePlan guide = GuideReferencePlanCompiler.Compile(stage.ImageReference);
        var loras = NormalLoraPlanCompiler.Compile(clip, stage);
        var icLoras = IcLoraPlanCompiler.Compile(clip, stage);
        var references = ImageReferencePlanCompiler.Compile(clip, stage);

        Assert.Equal(PromptRelayMode.Relay, prompt.Mode);
        Assert.Equal(new GuideReferencePlan(GuideReferenceKind.Base2Edit, "edit4", 4), guide);
        Assert.Collection(
            loras,
            lora => Assert.Equal("clip", lora.Name),
            lora => Assert.Equal("stage", lora.Name));
        Assert.Equal(IcLoraMediaSourceKind.ControlNet, Assert.Single(icLoras).MediaInput.Source);
        Assert.Equal(1, icLoras[0].MediaInput.ControlNetIndex);
        Assert.Equal(ImageReferenceSourceKind.Upload, Assert.Single(references).SourceKind);
    }

    [Fact]
    public void IcLoraPlanCompiler_UsesPerEntryStageStrengths()
    {
        StageSpec stage = Stage(11) with
        {
            ControlNetStrength = 0.6,
            IcLoraStrengths = [0.2, 0.9],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            IcLoras =
            [
                new IcLoraSpec(
                    IcLoraWeights.AutoModelToken,
                    Constants.ControlNetSourceOne,
                    1,
                    1,
                    Constants.IcLoraControlCanny,
                    null,
                    DriveData: IcLoraDriveData.Visual),
                new IcLoraSpec(
                    IcLoraWeights.AutoModelToken,
                    Constants.ControlNetSourceTwo,
                    1,
                    1,
                    Constants.IcLoraControlDepth,
                    null,
                    DriveData: IcLoraDriveData.Visual),
            ],
        };

        var plans = IcLoraPlanCompiler.Compile(clip, stage);

        Assert.Equal(2, plans.Length);
        Assert.Equal(0.2, plans[0].GuideStrength);
        Assert.Equal(0.9, plans[1].GuideStrength);
    }

    [Theory]
    [InlineData(2, "Upload", "none", null, (int)IcLoraDriveData.None, "ltx2.ic-lora.stage-target-invalid")]
    [InlineData(-1, "future-source", "none", null, (int)IcLoraDriveData.Visual, "ltx2.ic-lora.drive-source-unsupported")]
    [InlineData(-1, "Upload", "future-control", null, (int)IcLoraDriveData.None, "ltx2.ic-lora.control-mode-unsupported")]
    [InlineData(
        -1,
        "Upload",
        "none",
        "data:application/octet-stream;base64,QQ==",
        (int)IcLoraDriveData.Visual,
        "ltx2.ic-lora.drive-media-kind-unsupported")]
    public void LtxIcLoraStructuralErrors_AreBlockingPlanningDiagnostics(
        int targetStage,
        string source,
        string control,
        string uploadedData,
        int driveData,
        string expectedCode)
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            AuthoredStages = [new(0, "ltx-2", "ltx-2.3", false)],
            IcLoras =
            [
                new(
                    "adapter.safetensors",
                    source,
                    1,
                    1,
                    control,
                    uploadedData is null ? null : new(uploadedData, "drive.bin"),
                    DriveData: (IcLoraDriveData)driveData,
                    Stage: targetStage),
            ],
        };

        Ltx2ClipPlanCompilation compilation = Ltx2ClipPlanCompiler.Compile(
            clip,
            new(640, 360, 24));

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code == expectedCode
                && diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void LtxIcLoraStructuralError_PreventsArchitecturePayloadPublication()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            AuthoredStages = [new(0, "ltx-2", "ltx-2.3", false)],
            IcLoras =
            [
                new(
                    "adapter.safetensors",
                    "future-source",
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null,
                    DriveData: IcLoraDriveData.Visual),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(new(
            640,
            360,
            24,
            false,
            [clip]));
        ClipPlan plannedClip = Assert.Single(plan.Clips);

        Assert.Null(plannedClip.ArchitecturePayload);
        Assert.Null(Assert.Single(plannedClip.Stages).ArchitecturePayload);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-source-unsupported"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void ClipPlanCompiler_PlansSourcedStageChainAndOutputOwnership()
    {
        ClipSpec clip = SourcedClip(7, Stage(10, control: 0), Stage(11, rawIndex: 1));

        ClipPlan plan = ClipPlanCompiler.Compile(
            clip,
            new ClipPlanCompilationContext(
                IsTextToVideo: false,
                Width: 640,
                Height: 360,
                FramesPerSecond: 30,
                IsLastClip: true,
                IsMultiClip: false,
                TotalStageCount: 2,
                FirstStageOrdinal: 0,
                EntryMode: ArchitectureEntryMode.SourceVideo,
                ArchitecturePayload: Ltx2ClipPlanCompiler.Compile(
                    clip,
                    new(640, 360, 30)).Payload));

        Assert.Equal(ClipInputKind.SourceVideo, plan.Input);
        Assert.True(plan.Stages[0].IsPassthrough);
        Assert.Equal(StageInputKind.PreviousStage, plan.Stages[1].Input);
        Assert.False(plan.Stages[1].IsPassthrough);
        Assert.Equal(640, plan.SourceVideo.TargetWidth);
        Assert.True(plan.Stages[1].Output.IsTimelineTerminal);
    }

    [Theory]
    [MemberData(nameof(RepresentativeSpecs))]
    public void Facade_ParityWithExtractedPureComponents(
        VideoStagesSpec spec,
        object environmentValue)
    {
        RootEnvironment environment = Assert.IsType<RootEnvironment>(environmentValue);
        VideoExecutionPlan facade = TestPlanCompiler.Compile(spec, environment);
        VideoExecutionPlan assembled = CompileFromComponents(spec, environment);

        Assert.Equal(Serialize(assembled), Serialize(facade));
    }

    public static IEnumerable<object[]> RepresentativeSpecs()
    {
        yield return
        [
            new VideoStagesSpec(512, 512, 24, true, [GeneratedClip(0, Stage(10))]),
            new RootEnvironment(HostRootKind.TextToVideoRoot),
        ];

        yield return
        [
            new VideoStagesSpec(640, 360, 30, false,
            [
                GeneratedClip(0, Stage(10), Stage(11, rawIndex: 1)) with
                {
                    BoundaryOut = Constants.BoundaryOutContinue,
                    BoundaryOutOverlap = 16,
                },
                GeneratedClip(1, Stage(12)) with
                {
                    ImageRefs = [new ImageRefSpec("Upload", 1, false, "first.png")],
                },
            ]),
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true),
        ];

        yield return
        [
            new VideoStagesSpec(512, 512, 24, false,
            [
                SourcedClip(0, Stage(10, control: 0), Stage(11, rawIndex: 1)) with
                {
                    Loras = [new LoraRef("clip", 0.8)],
                    PromptWindows = [new PromptWindowSpec("opening", 0, 1)],
                    ImageRefs = [new ImageRefSpec("Upload", 2, false, "ref.png", "data:image/png;base64,QQ==")],
                    IcLoras =
                    [
                        new IcLoraSpec(
                            IcLoraWeights.AutoModelToken,
                            Constants.IcLoraSourceUpload,
                            0.7,
                            1,
                            Constants.IcLoraControlNone,
                            new UploadedMediaSpec("data:image/png;base64,Qg==", "drive.png"),
                            DriveData: IcLoraDriveData.Visual,
                            Preset: "deblur",
                            Stage: 1),
                    ],
                },
            ]),
            new RootEnvironment(HostRootKind.ImageToVideo),
        ];
    }

    private static VideoExecutionPlan CompileFromComponents(VideoStagesSpec spec, RootEnvironment environment)
    {
        ArchitecturePlanningResult architecture = TestPlanCompiler.ResolveLtx(spec);
        List<PlanDiagnostic> diagnostics = [];
        List<ClipSpec> clips = [];
        HashSet<int> seenClipIds = [];
        foreach (ClipSpec clip in (spec.Clips ?? []).Where(clip =>
                     clip is not null && (clip.SourceVideo is not null || clip.Stages is { Count: > 0 })))
        {
            if (!seenClipIds.Add(clip.Id))
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Error,
                    "duplicate-clip-id",
                    $"Clip id {clip.Id} is duplicated; only its first occurrence is planned.",
                    clip.Id));
                continue;
            }
            clips.Add(clip);
        }
        if (clips.Count != (spec.Clips?.Count ?? 0))
        {
            diagnostics.Insert(0, new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "inactive-clips-ignored",
                "Clips without a source video or active stages were ignored by the execution plan."));
        }

        int totalStages = clips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        List<ClipPlan> plans = [];
        for (int i = 0; i < clips.Count; i++)
        {
            ClipArchitectureAssignment assignment =
                architecture.Clips.GetValueOrDefault(clips[i].Id);
            IArchitectureClipPayload payload = assignment?.Module
                .ValidateAndCompileClip(
                    clips[i],
                    assignment.StageModels,
                    new(spec.Width, spec.Height, spec.FPS))
                .Payload;
            plans.Add(ClipPlanCompiler.Compile(clips[i], new ClipPlanCompilationContext(
                spec.IsTextToVideo,
                spec.Width,
                spec.Height,
                spec.FPS,
                i == clips.Count - 1,
                clips.Count > 1,
                totalStages,
                firstStageOrdinal,
                clips[i].SourceVideo is not null
                    ? ArchitectureEntryMode.SourceVideo
                    : spec.IsTextToVideo
                        ? ArchitectureEntryMode.TextToVideo
                        : ArchitectureEntryMode.ImageToVideo,
                assignment,
                payload)));
            diagnostics.AddRange(plans[^1].Audio.Diagnostics.Select(audioDiagnostic =>
                new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    audioDiagnostic.Code,
                    audioDiagnostic.Message,
                    plans[^1].ClipId)));
            firstStageOrdinal += clips[i].Stages?.Count ?? 0;
        }

        BoundaryPlanningResult boundaries = BoundaryPlanCompiler.Compile(clips, plans);
        diagnostics.AddRange(boundaries.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [.. clips.Select(clip => clip.Frames)],
            boundaries.Boundaries);
        if (boundaryBudget.Degraded)
        {
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "boundary-frame-budget-reconciled",
                $"VideoStages: {boundaryBudget.Reason}."));
        }
        RootPlan root = RootPlanCompiler.Compile(environment, clips);
        diagnostics.AddRange(
            Ltx2ArchitectureModule.Instance.ValidatePlan(plans, plans, root));

        VideoExecutionPlan plan = new(
            spec.Width,
            spec.Height,
            spec.FPS,
            root,
            Array.AsReadOnly(plans.ToArray()),
            Array.AsReadOnly(boundaryBudget.Boundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
        AudioTimelinePlan audioTimeline = AudioTimelinePlanCompiler.Compile(plan);
        diagnostics.AddRange(audioTimeline.Diagnostics.Select(diagnostic => new PlanDiagnostic(
            diagnostic.Severity switch
            {
                PlanDiagnosticSeverity.Info => PlanDiagnosticSeverity.Info,
                PlanDiagnosticSeverity.Warning => PlanDiagnosticSeverity.Warning,
                _ => PlanDiagnosticSeverity.Error,
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.ClipId)));
        return plan with
        {
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray()),
            AudioTimeline = audioTimeline,
        };
    }

    private static string Serialize(VideoExecutionPlan plan) =>
        JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        new(id, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], stages);

    private static ClipSpec SourcedClip(int id, params StageSpec[] stages) =>
        new(id, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], stages,
            SourceVideo: new SourceVideoSpec("data:video/mp4;base64,QQ==", "source.mp4", 1.5));

    private static StageSpec Stage(int id, double control = 1, int? rawIndex = null) =>
        new(id, control, 1, "pixel-lanczos", "ltx-2", 12, 4.5, "euler", "normal", "Generated",
            ClipStageIndex: id - 10,
            ClipStageRawIndex: rawIndex ?? id - 10);

    private sealed record FakeBoundaryPayload(IArchitectureBoundaryPolicy BoundaryPolicy) :
        IArchitectureClipPayload,
        IArchitectureBoundaryPolicySource
    {
        public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;
    }

    private sealed class FakeBoundaryPolicy : IArchitectureBoundaryPolicy
    {
        public IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes
            { get; } = new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
            {
                [BoundaryExecutionMode.Continue] = new(
                    RuleSupport.Conditional,
                    "fake.continue",
                    "Fake policy with a different grid and permissive target.",
                    FrameStep: 5,
                    MinFrames: 10,
                    MaxFrames: 30,
                    DefaultFrames: 15,
                    ContinuityExtraFrames: 3,
                    TargetRequiresGeneratedEntry: false,
                    TargetRequiresStage: false,
                    TargetDisallowsInitialReference: false),
            };

        public IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules =>
            Modes.ToDictionary(pair => pair.Key, pair => pair.Value.ToRuleDecision());
    }
}
