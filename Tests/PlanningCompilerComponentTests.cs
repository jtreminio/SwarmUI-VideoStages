using System.Text.Json;
using VideoStages.Planning;
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
        BoundaryPlanningResult result = BoundaryPlanCompiler.Compile(
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 16,
            },
            SourcedClip(1),
        ]);

        BoundaryPlan boundary = Assert.Single(result.Boundaries);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(BoundaryFallback.TargetIsSourcedVideo, boundary.Fallback);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-targetissourcedvideo");
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
                    Constants.IcLoraAutoModel,
                    Constants.ControlNetSourceTwo,
                    1,
                    1,
                    Constants.IcLoraControlCanny,
                    null,
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
        Assert.Equal(IcLoraDriveSourceKind.ControlNet, Assert.Single(icLoras).Drive.Kind);
        Assert.Equal(1, icLoras[0].Drive.ControlNetIndex);
        Assert.Equal(ImageReferenceSourceKind.Upload, Assert.Single(references).SourceKind);
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
                FirstStageOrdinal: 0));

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
        VideoExecutionPlan facade = VideoExecutionPlanCompiler.Compile(spec, environment);
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
                            Constants.IcLoraAutoModel,
                            Constants.IcLoraSourceUpload,
                            0.7,
                            1,
                            Constants.IcLoraControlNone,
                            new UploadedAudioSpec("data:image/png;base64,Qg==", "drive.png"),
                            Stage: 1),
                    ],
                },
            ]),
            new RootEnvironment(HostRootKind.ImageToVideo),
        ];
    }

    private static VideoExecutionPlan CompileFromComponents(VideoStagesSpec spec, RootEnvironment environment)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        List<ClipSpec> clips = [];
        HashSet<int> seenClipIds = [];
        foreach (ClipSpec clip in (spec.Clips ?? []).Where(clip =>
                     clip is not null && (clip.SourceVideo is not null || clip.Stages is { Count: > 0 })))
        {
            if (!seenClipIds.Add(clip.Id))
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Error,
                    "duplicate-clip-id",
                    $"Clip id {clip.Id} is duplicated; only its first occurrence is planned.",
                    clip.Id));
                continue;
            }
            clips.Add(clip);
        }
        if (clips.Count != (spec.Clips?.Count ?? 0))
        {
            diagnostics.Insert(0, new VideoPlanDiagnostic(
                VideoPlanDiagnosticSeverity.Warning,
                "inactive-clips-ignored",
                "Clips without a source video or active stages were ignored by the execution plan."));
        }

        BoundaryPlanningResult boundaries = BoundaryPlanCompiler.Compile(clips);
        diagnostics.AddRange(boundaries.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [.. clips.Select(clip => clip.Frames)],
            boundaries.Boundaries);
        if (boundaryBudget.Degraded)
        {
            diagnostics.Add(new VideoPlanDiagnostic(
                VideoPlanDiagnosticSeverity.Warning,
                "boundary-frame-budget-reconciled",
                $"VideoStages: {boundaryBudget.Reason}."));
        }
        int totalStages = clips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        List<ClipPlan> plans = [];
        for (int i = 0; i < clips.Count; i++)
        {
            plans.Add(ClipPlanCompiler.Compile(clips[i], new ClipPlanCompilationContext(
                spec.IsTextToVideo,
                spec.Width,
                spec.Height,
                spec.FPS,
                i == clips.Count - 1,
                clips.Count > 1,
                totalStages,
                firstStageOrdinal)));
            diagnostics.AddRange(plans[^1].Audio.Diagnostics.Select(audioDiagnostic =>
                new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    audioDiagnostic.Code,
                    audioDiagnostic.Message,
                    plans[^1].ClipId)));
            firstStageOrdinal += clips[i].Stages?.Count ?? 0;
        }

        VideoExecutionPlan plan = new(
            spec.Width,
            spec.Height,
            spec.FPS,
            RootPlanCompiler.Compile(environment, clips),
            Array.AsReadOnly(plans.ToArray()),
            Array.AsReadOnly(boundaryBudget.Boundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
        AudioTimelinePlan audioTimeline = AudioTimelinePlanCompiler.Compile(plan);
        diagnostics.AddRange(audioTimeline.Diagnostics.Select(diagnostic => new VideoPlanDiagnostic(
            diagnostic.Severity switch
            {
                AudioTimelineDiagnosticSeverity.Info => VideoPlanDiagnosticSeverity.Info,
                AudioTimelineDiagnosticSeverity.Warning => VideoPlanDiagnosticSeverity.Warning,
                _ => VideoPlanDiagnosticSeverity.Error,
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
}
