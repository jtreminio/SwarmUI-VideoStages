using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Ltx2;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoExecutionPlanCompilerTests
{
    [Fact]
    public void Compile_TextToVideoGeneratedClip_ReplacesRootAndGeneratesFromEmptyLatent()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(
            isTextToVideo: true,
            GeneratedClip(0, Stage(10))));

        Assert.True(plan.Root.DiscardsRoot);
        Assert.True(plan.Root.InterceptsHostCore);
        Assert.False(plan.Root.UsesGeneratedClipDonor);
        StagePlan stage = Assert.Single(Assert.Single(plan.Clips).Stages);
        Assert.Equal(StageInputKind.EmptyLatent, stage.Input);
        Assert.False(stage.IsPassthrough);
    }

    [Fact]
    public void Compile_InitVideoClip_UsesSourceAndDistinguishesPassthroughRefineAndRetake()
    {
        ClipSpec initVideoClip = new(
            3, 49, MediaSource.Native, [], false, false, false, false, null, [],
            [
                Stage(10, control: 0),
                Stage(11, control: 0.4),
                Stage(12, control: 0, retake: new RetakeWindowSpec(8, 16, 1)),
            ],
            InitVideo: new InitVideoSpec("data", "source.mp4", 0));

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, initVideoClip));

        ClipPlan clip = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.InitVideo, clip.EntryMode);
        Assert.Equal(StageInputKind.InitVideo, clip.Stages[0].Input);
        Assert.True(clip.Stages[0].IsPassthrough);
        Assert.False(clip.Stages[1].IsPassthrough);
        Assert.False(clip.Stages[2].IsPassthrough);
        Assert.NotNull(clip.Stages[2].RequireLtx2Payload().Retake);
        Assert.Equal(StageInputKind.PreviousStage, clip.Stages[2].Input);
        Assert.Equal("data", clip.InitVideo.Data);
        Assert.Equal("source.mp4", clip.InitVideo.FileName);
        Assert.Equal(0, clip.InitVideo.StartSeconds);
        Assert.Equal(512, clip.InitVideo.TargetWidth);
        Assert.Equal(512, clip.InitVideo.TargetHeight);
        Assert.Equal(24, clip.InitVideo.TargetFramesPerSecond);
    }

    [Fact]
    public void Compile_RetakeWithoutInitVideo_WarnsAndDropsRetake()
    {
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, retake: new RetakeWindowSpec(8, 16, 1)));

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "retake-source-required"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.Null(Assert.Single(Assert.Single(plan.Clips).Stages).RequireLtx2Payload().Retake);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_RootEnvironment_SeparatesRootDiscardHandoffAndDonorOwnership()
    {
        ClipSpec initVideoLead = InitVideoClip(0);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(
            Spec(false, initVideoLead, GeneratedClip(1, Stage(11))),
            new RootEnvironment(HostRootKind.ImageToVideo, CanInterceptHostCore: true));

        Assert.False(plan.Root.DiscardsRoot);
        Assert.True(plan.Root.InterceptsHostCore);
        Assert.True(plan.Root.UsesGeneratedClipDonor);
    }

    [Fact]
    public void Compile_HostRootFirstStage_DoesNotClaimTheEmptyLatentExecutionPath()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, GeneratedClip(0, Stage(10))));

        StagePlan stage = plan.Clips[0].Stages[0];
        Assert.Equal(StageInputKind.RootMedia, stage.Input);
        Assert.False(stage.IsPassthrough);
        Assert.False(stage.IsIntermediateStage);
    }

    [Theory]
    [InlineData("pixel-lanczos", (int)StageUpscaleMode.Pixel)]
    [InlineData("model-upscaler.safetensors", (int)StageUpscaleMode.Model)]
    [InlineData("latent-bilinear", (int)StageUpscaleMode.Latent)]
    [InlineData("latentmodel-upscaler.safetensors", (int)StageUpscaleMode.LatentModel)]
    public void Compile_StageUpscaleMethods_AreClassified(string method, int expectedValue)
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false,
            GeneratedClip(0, Stage(10, control: 0.5, upscale: 2, upscaleMethod: method))));

        StageUpscalePlan upscale = plan.Clips[0].Stages[0].Core.Upscale;
        Assert.Equal((StageUpscaleMode)expectedValue, upscale.Mode);
        Assert.Equal(2, upscale.Factor);
        Assert.Equal(method, upscale.RawMethod);
    }

    [Fact]
    public void Compile_OptionsAreExpressedAtStageAndClipHooks()
    {
        ClipSpec clip = new(
            4, 49, MediaSource.Upload,
            [new IcLoraSpec("drive", MediaSource.Upload, 1, 1, Constants.IcLoraControlNone, null, Stage: 0)],
            false, true, false, true,
            new UploadedMediaSpec("data:audio/wav;base64,AA==", "track.wav"),
            [new FrameRefSpec("Upload", 1, false, "ref.png")],
            [Stage(10, loras: [new LoraRef("stage")]), Stage(11)],
            Loras: [new LoraRef("clip")],
            PromptWindows: [new PromptWindowSpec("first", 0, 1)],
            ReferenceFraming: ReferenceFramingMode.FitGreen);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Ltx2ClipPayload ltxClip = Assert.IsType<Ltx2ClipPayload>(
            compiled.ArchitecturePayload);
        Assert.Equal(AudioLengthOwner.Audio, compiled.Audio.LengthOwner);
        Assert.Equal(ReferenceFramingMode.FitGreen, ltxClip.ReferenceFraming);
        Assert.Equal(
            PromptRelayMode.Relay,
            compiled.Stages[0].RequireLtx2Payload().PromptRelay.Mode);
        Assert.Equal(2, compiled.Stages[0].Core.Loras.Length);
        Assert.Single(compiled.Stages[0].RequireLtx2Payload().IcLoras);
        Assert.Single(compiled.Stages[0].RequireLtx2Payload().FrameReferences);
        Assert.Empty(compiled.Stages[1].RequireLtx2Payload().IcLoras);
        // Audio reuse needs a generate/capture/reuse trio, so this two-stage clip drops the
        // authored ReuseAudio flag without any stage action and without any diagnostic.
        Assert.All(
            compiled.Stages,
            stage => Assert.Equal(
                StageAudioAction.None,
                stage.RequireLtx2Payload().AudioAction));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void Compile_Ltx_OmitsNoOpStageLoraAndRetainsTextEncoderOnlyLora()
    {
        StageSpec stage = Stage(
            10,
            loras:
            [
                new("stage-no-op", 0, 0),
                new("stage-text-only", 0, 0.8),
            ]);

        Ltx2StagePayload payload = Assert.Single(
            Assert.Single(
                TestPlanCompiler.Compile(
                    Spec(false, GeneratedClip(0, stage))).Clips).Stages)
            .RequireLtx2Payload();
        LoraPlan lora = Assert.Single(payload.Core.Loras);

        Assert.Equal("stage-text-only", lora.Name);
        Assert.Equal(0, lora.ModelWeight);
        Assert.Equal(0.8, lora.TextEncoderWeight);
    }

    [Theory]
    [InlineData("Base", (int)StageGuideReferenceKind.Base, -1)]
    [InlineData("Refiner", (int)StageGuideReferenceKind.Refiner, -1)]
    [InlineData("Generated", (int)StageGuideReferenceKind.Generated, -1)]
    [InlineData("PreviousStage", (int)StageGuideReferenceKind.PreviousStage, -1)]
    [InlineData("Stage3", (int)StageGuideReferenceKind.ExplicitStage, 3)]
    [InlineData("edit4", (int)StageGuideReferenceKind.Base2Edit, 4)]
    public void Compile_GuideReferenceIntent_IsTyped(
        string raw,
        int expectedKindValue,
        int expectedStageIndex)
    {
        StageSpec stage = Stage(10) with { ImageReference = raw };

        GuideReferencePlan guide = TestPlanCompiler
            .Compile(Spec(false, GeneratedClip(0, stage)))
            .Clips[0].Stages[0].RequireLtx2Payload().Guide;

        Assert.Equal((StageGuideReferenceKind)expectedKindValue, guide.Kind);
        Assert.Equal(expectedStageIndex < 0 ? null : expectedStageIndex, guide.ReferencedStageIndex);
        Assert.Equal(raw, guide.RawValue);
    }

    [Fact]
    public void Compile_TypedStageFields_AreActionableWithValidSources()
    {
        List<LoraRef> clipLoras = [new("clip.safetensors", 0.6, 0.4)];
        List<LoraRef> stageLoras = [new("stage.safetensors", 1.2)];
        List<FrameRefSpec> references =
        [
            new("Upload", 5, false, "opening.png", "data:image/png;base64,QQ=="),
            new("edit2", 3, true, null),
        ];
        List<IcLoraSpec> icLoras =
        [
            new(
                IcLoraWeights.AutoModelToken,
                MediaSource.Upload,
                0.8,
                0.7,
                Constants.IcLoraControlCanny,
                new UploadedMediaSpec("data:image/png;base64,Qg==", "drive.png"),
                DriveData: IcLoraDriveData.Visual,
                Preset: "pixel-spatial-upscaler-x2",
                Stage: 1),
            new(
                "control.safetensors",
                MediaSource.ControlNetTwo,
                1.1,
                1,
                Constants.IcLoraControlDepth,
                null,
                DriveData: IcLoraDriveData.Visual,
                Stage: -1),
            new(
                "other-stage.safetensors",
                MediaSource.Upload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                Stage: 2),
        ];
        StageSpec first = Stage(10);
        StageSpec target = Stage(
            11,
            control: 0.5,
            upscale: 1.5,
            upscaleMethod: "latent-bilinear",
            retake: new RetakeWindowSpec(8, 16, 0.75),
            loras: stageLoras) with
        {
            ImageReference = "edit4",
            ControlNetStrength = 0.55,
            FrameRefStrengths = [0.25, 0.9],
            ImageRefWasExplicit = true,
        };
        StageSpec last = Stage(12);
        ClipSpec clip = new(
            7,
            72,
            MediaSource.Native,
            icLoras,
            SaveAudioTrack: true,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: true,
            UploadedAudio: null,
            FrameRefs: references,
            Stages: [first, target, last],
            Loras: clipLoras,
            PromptWindows:
            [
                new("opening prompt", 0, 1),
                new("ending prompt", 2, 1),
            ],
            InitVideo: new InitVideoSpec("data", "source.mp4", 0));

        StagePlan compiled = TestPlanCompiler
            .Compile(Spec(false, clip))
            .Clips[0].Stages[1];
        Ltx2StagePayload ltx = compiled.RequireLtx2Payload();

        Assert.Equal(StageGuideReferenceKind.Base2Edit, ltx.Guide.Kind);
        Assert.Equal(4, ltx.Guide.ReferencedStageIndex);
        Assert.Equal(StageUpscaleMode.Latent, compiled.Core.Upscale.Mode);
        Assert.Equal(1.5, compiled.Core.Upscale.Factor);
        Assert.Equal("bilinear", compiled.Core.Upscale.MethodName);
        Assert.Equal(11, compiled.StageId);
        Assert.Equal(1, compiled.ClipStageIndex);
        Assert.Equal(1, compiled.ClipStageRawIndex);
        Assert.Equal("ltx-2", compiled.ResolvedModel.ModelName);
        Assert.Equal(0.5, ltx.Core.Control);
        Assert.Equal(12, ltx.Core.Steps);
        Assert.Equal(4.5, ltx.Core.CfgScale);
        Assert.Equal("euler", ltx.Core.Sampler);
        Assert.Equal("normal", ltx.Core.Scheduler);
        Assert.True(ltx.ImageReferenceWasExplicit);

        Assert.Collection(
            compiled.Core.Loras,
            lora =>
            {
                Assert.Equal("clip.safetensors", lora.Name);
                Assert.Equal(0.6, lora.ModelWeight);
                Assert.Equal(0.4, lora.TextEncoderWeight);
            },
            lora =>
            {
                Assert.Equal("stage.safetensors", lora.Name);
                Assert.Equal(1.2, lora.TextEncoderWeight);
            });

        Assert.Equal(2, ltx.IcLoras.Length);
        IcLoraPlan uploaded = ltx.IcLoras[0];
        Assert.True(uploaded.UsesAutoModel);
        Assert.Equal(IcLoraMediaSourceKind.Upload, uploaded.Drive.Source);
        Assert.Equal(IcLoraDriveMediaKind.Image, uploaded.Drive.MediaKind);
        Assert.Equal(IcLoraControlMode.Canny, uploaded.ControlMode);
        Assert.Equal(0.55, uploaded.GuideStrength);
        Assert.Equal(IcLoraMediaSourceKind.ControlNet, ltx.IcLoras[1].Drive.Source);
        Assert.Equal(1, ltx.IcLoras[1].Drive.ControlNetIndex);

        Assert.Equal(new RetakePlan(8, 16, 0.75), ltx.Retake);
        Assert.Equal(PromptRelayMode.Relay, ltx.PromptRelay.Mode);
        Assert.Equal(2, ltx.PromptRelay.AuthoredWindows.Length);
        Assert.Contains(ltx.PromptRelay.Segments, segment => segment.Prompt == "opening prompt");
        Assert.Collection(
            ltx.FrameReferences,
            reference =>
            {
                Assert.Equal(ImageReferenceSourceKind.Upload, reference.SourceKind);
                Assert.Equal(5, reference.Frame);
                Assert.Equal(ImageReferenceFrameEdge.Start, reference.FrameOrigin);
                Assert.Equal(0.25, reference.Strength);
                Assert.Equal("opening.png", reference.UploadFileName);
            },
            reference =>
            {
                Assert.Equal(ImageReferenceSourceKind.Base2Edit, reference.SourceKind);
                Assert.Equal(2, reference.Base2EditStageIndex);
                Assert.Equal(ImageReferenceFrameEdge.End, reference.FrameOrigin);
                Assert.Equal(0.9, reference.Strength);
            });
        Assert.Equal(StageAudioAction.CaptureForReuse, ltx.AudioAction);
        Assert.True(compiled.IsIntermediateStage);

        LoraPlan plannedLora = compiled.Core.Loras[1];
        Assert.Equal("stage.safetensors", plannedLora.Name);
        Assert.Equal(plannedLora.ModelWeight, plannedLora.TextEncoderWeight);
    }

    [Fact]
    public void Compile_PromptRelaySegments_PreserveExactClipDuration()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            Frames = 49,
            PromptWindows =
            [
                new("first", 0, 0.37),
                new("second", 1.11, 0.42),
            ],
        };

        PromptRelayPlan relay = TestPlanCompiler.Compile(Spec(false, clip))
            .Clips[0].Stages[0].RequireLtx2Payload().PromptRelay;

        Assert.Equal(PromptRelayMode.Relay, relay.Mode);
        Assert.Equal(49d / 24d, relay.Segments.Sum(segment => segment.Seconds), 12);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_PromptRelayWithDynamicLength_DefersTilingToRuntime(
        bool controlNetOwnsLength)
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            Frames = null,
            AudioSource = controlNetOwnsLength ? MediaSource.ControlNet : MediaSource.Upload,
            UploadedAudio = controlNetOwnsLength ? null : new UploadedMediaSpec("audio", "track.wav"),
            ClipLengthFromAudio = !controlNetOwnsLength,
            ClipLengthFromControlNet = controlNetOwnsLength,
            IcLoras = controlNetOwnsLength
                ? [new IcLoraSpec(
                    "control",
                    MediaSource.ControlNetOne,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null,
                    DriveData: IcLoraDriveData.Visual)]
                : [],
            PromptWindows = [new PromptWindowSpec("opening", 0, 1)],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.DoesNotContain(plan.Diagnostics, diagnostic =>
            diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        // Both duration owners defer identically; assert the arm actually resolved the one it authored.
        Assert.Equal(
            controlNetOwnsLength ? AudioLengthOwner.ControlNet : AudioLengthOwner.Audio,
            compiled.Audio.LengthOwner);
        PromptRelayPlan relay = compiled
            .Stages[0].RequireLtx2Payload().PromptRelay;
        // The frame count is only known at runtime, so the authored windows ride along
        // unresolved and LtxModelPromptPreparer tiles them against the real length.
        Assert.Equal(PromptRelayMode.RequiresRuntimeLength, relay.Mode);
        Assert.Single(relay.AuthoredWindows);
        Assert.Empty(relay.Segments);
    }

    [Fact]
    public void Compile_ControlSignalDerivedDuration_StoresTheResolvedLtxSource()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            ClipLengthFromControlNet = true,
            IcLoras =
            [
                new(
                    "control",
                    MediaSource.ControlNetTwo,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null,
                    DriveData: IcLoraDriveData.Visual),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));
        Ltx2ClipPayload payload = Assert.IsType<Ltx2ClipPayload>(
            Assert.Single(plan.Clips).ArchitecturePayload);

        Assert.Equal(1, payload.ControlNetSourceIndex);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ControlSignalDerivedDurationWithoutLtxSource_WarnsAndKeepsThePayload()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            ClipLengthFromControlNet = true,
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "audio.length.controlnet_owner_has_no_source"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning
                && diagnostic.ClipId == clip.Id);
        Assert.IsType<Ltx2ClipPayload>(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    // LTX guide nodes mask only the reference's latent frames, so a frame reference narrows the
    // retake window instead of invalidating it.
    [Fact]
    public void Compile_RetakeWithFrameReferences_IsAllowed()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            Stages = [Stage(10, retake: new RetakeWindowSpec(8, 16, 1))],
            FrameRefs = [new FrameRefSpec("Upload", 2, false, "ref.png", "image")],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.DoesNotContain(plan.Diagnostics, diagnostic =>
            diagnostic.Severity == PlanDiagnosticSeverity.Error);
        StagePlan stage = Assert.Single(Assert.Single(plan.Clips).Stages);
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        Assert.NotNull(payload.Retake);
        Assert.Single(payload.FrameReferences);
    }

    [Fact]
    public void Compile_DuplicateClipIds_WarnsAndDropsLaterOccurrence()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(
            false,
            GeneratedClip(4, Stage(10)),
            GeneratedClip(4, Stage(11))));

        Assert.Single(plan.Clips);
        Assert.Equal(4, plan.Clips[0].ClipId);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "duplicate-clip-id"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Compile_DuplicateClipIds_RootPlanUsesSurvivingOccurrence()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(
            Spec(
                false,
                InitVideoClip(4),
                GeneratedClip(4, Stage(10))),
            new RootEnvironment(
                HostRootKind.ImageToVideo,
                CanInterceptHostCore: true));

        ClipPlan surviving = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.InitVideo, surviving.EntryMode);
        Assert.True(plan.Root.DiscardsRoot);
        Assert.True(plan.Root.InterceptsHostCore);
        Assert.False(plan.Root.UsesGeneratedClipDonor);
        Assert.False(plan.Root.UsesStageHandoff);
        Assert.False(plan.Root.DropsTextToVideoRootDonor);
        Assert.False(plan.Root.DiscardsTextToVideoRoot);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "duplicate-clip-id"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Compile_ContinueIntoInitVideoClip_PreservesAuthoredBoundaryAndWarnsWhileUsingCut()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            Frames = 49,
            BoundaryOut = Constants.BoundaryOutContinue,
            BoundaryOutOverlap = 16,
        };
        ClipSpec source = InitVideoClip(1);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, source));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryJoinType.Cut, boundary.Effective);
        Assert.Equal(
            BoundaryFallbackReason.ArchitectureRuleUnsupported,
            boundary.Fallback);
        Assert.Equal(0, boundary.ContinuityWindowFrames);
        Assert.Contains(plan.Diagnostics, d =>
            d.Code == "boundary-cross-architecture-non-cut"
            && d.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(Constants.BoundaryOutContinue, first.BoundaryOut);
    }

    [Fact]
    public void Compile_ContinueWithFirstFrameReference_FallsBackToCut()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        ClipSpec next = GeneratedClip(1, Stage(11)) with
        {
            FrameRefs = [new FrameRefSpec("Upload", 1, false, "first.png")],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, next));

        Assert.Equal(BoundaryFallbackReason.TargetHasFirstFrameReference, plan.Boundaries[0].Fallback);
        Assert.Equal(BoundaryJoinType.Cut, plan.Boundaries[0].Effective);
    }

    [Fact]
    public void Compile_ValidContinue_OnlyMarksItsTargetClipAsUsingContinuity()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        Assert.Equal(BoundaryJoinType.Continue, plan.Boundaries[0].Effective);
    }

    [Fact]
    public void Compile_ShortContinue_CutsWhenArchitectureMinimumCannotFit()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            Frames = 5,
            BoundaryOut = Constants.BoundaryOutContinue,
            BoundaryOutOverlap = 8,
        };
        ClipSpec second = GeneratedClip(1, Stage(11)) with { Frames = 5 };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, second));

        Assert.Equal(BoundaryJoinType.Cut, plan.Boundaries[0].Effective);
        Assert.Equal(0, plan.Boundaries[0].ContinuityWindowFrames);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-frame-budget-reconciled");
    }

    [Fact]
    public void Compile_Crossfade_PreservesEffectiveBoundaryForRuntimeAssembly()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            BoundaryOut = Constants.BoundaryOutCrossfade,
            BoundaryOutOverlap = 24,
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryJoinType.Crossfade, boundary.Effective);
        Assert.Equal(24, boundary.OverlapFrames);
        Assert.Single(plan.Boundaries);
    }

    [Fact]
    public void Compile_TimelineAudioUsesTheFittedBoundaryWindow()
    {
        TimelineSpec spec = Spec(
            false,
            GeneratedClip(0, Stage(10)) with
            {
                Frames = 9,
                BoundaryOut = Constants.BoundaryOutCrossfade,
                BoundaryOutOverlap = 16,
            },
            GeneratedClip(1, Stage(11)) with { Frames = 9 }) with
        {
            TimelineAudioSegments =
            [
                new(
                    "score",
                    new UploadedMediaSpec("data:audio/wav;base64,QUJD", "score.wav"),
                    null,
                    TimelineStartSeconds: 0,
                    SourceStartSeconds: 0,
                    LengthSeconds: 1),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        Assert.Equal(8, Assert.Single(plan.Boundaries).OverlapFrames);
        Assert.Equal(
            1 / 24d,
            Assert.Single(plan.Clips[0].Audio.Segments.Items).LengthSeconds,
            8);
    }

    [Fact]
    public void Compile_TimelineAudioSegment_CutsAtEveryClipAndAdvancesSourceOffset()
    {
        TimelineSpec spec = Spec(
            false,
            GeneratedClip(0, Stage(10)) with { Frames = 49 },
            GeneratedClip(1, Stage(11)) with { Frames = 49 },
            GeneratedClip(2, Stage(12)) with { Frames = 49 }) with
        {
            TimelineAudioSegments =
            [
                new(
                    "dialogue",
                    new UploadedMediaSpec("data:audio/wav;base64,QUJD", "dialogue.wav"),
                    null,
                    TimelineStartSeconds: 1.5,
                    SourceStartSeconds: 10,
                    LengthSeconds: 3,
                    Volume: 0.75),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        AudioSegmentItemPlan[] projected = plan.Clips
            .SelectMany(clip => clip.Audio.Segments.Items)
            .ToArray();
        double clipSeconds = 49 / 24.0;
        double firstLength = clipSeconds - 1.5;
        Assert.Equal(3, projected.Length);
        Assert.Equal([1.5d, 0d, 0d], projected.Select(item => item.StartSeconds));
        Assert.Equal(
            [firstLength, clipSeconds, 4.5 - clipSeconds * 2],
            projected.Select(item => item.LengthSeconds));
        Assert.Equal(
            [10d, 10 + firstLength, 10 + firstLength + clipSeconds],
            projected.Select(item => item.TrimStartSeconds));
        Assert.All(projected, item =>
        {
            Assert.Equal(AudioSourceKind.Upload, item.SourceKind);
            Assert.Equal("dialogue.wav", item.UploadedMedia.FileName);
            Assert.Equal(0.75, item.Volume);
        });
    }

    [Fact]
    public void Compile_FinalPlanIncludesTimelineAudioAndAuthoredResolutionState()
    {
        TimelineSpec spec = Spec(false, GeneratedClip(0, Stage(10))) with
        {
            HasConfiguredResolution = false,
            TimelineAudioSegments =
            [
                new(
                    "dialogue",
                    new UploadedMediaSpec("data:audio/wav;base64,QUJD", "dialogue.wav"),
                    null,
                    TimelineStartSeconds: 0,
                    SourceStartSeconds: 0,
                    LengthSeconds: 1),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        Assert.False(plan.HasConfiguredResolution);
        Assert.Single(Assert.Single(plan.Clips).Audio.Segments.Items);
    }

    [Fact]
    public void Compile_MixedTimelineAudio_ProjectsSegmentsOntoEveryArchitecture()
    {
        ClipSpec ltx = GeneratedClip(0, Stage(10));
        ClipSpec host = GeneratedClip(1, Stage(11));
        TimelineSpec spec = Spec(false, ltx, host) with
        {
            TimelineAudioSegments =
            [
                new(
                    "mixed-overlay",
                    new UploadedMediaSpec(
                        "data:audio/wav;base64,QUJD",
                        "overlay.wav"),
                    null,
                    TimelineStartSeconds: 0,
                    SourceStartSeconds: 0,
                    LengthSeconds: 4),
            ],
        };
        ClipArchitectureAssignment Assignment(
            ClipSpec clip,
            IVideoArchitectureModule module)
        {
            VideoArchitectureDescriptor descriptor = module.Descriptor;
            return new(
                clip.Id,
                module,
                descriptor,
                clip.Stages.ToDictionary(
                    stage => stage.ClipStageRawIndex,
                    stage => TestResolvedVideoModel.Create(
                        stage.Model,
                        new($"{descriptor.Id.Value}-test-profile"),
                        descriptor)));
        }
        ArchitecturePlanningResult architectures = new(
            new Dictionary<int, ClipArchitectureAssignment>
            {
                [ltx.Id] = Assignment(ltx, Ltx2ArchitectureModule.Instance),
                [host.Id] = Assignment(host, HostVideoArchitectureModule.Instance),
            },
            []);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architectures);

        // Audio tracks are model-independent: the generic clip carries its window too, and
        // mixes it after decode instead of during generation.
        Assert.Single(plan.Clips[0].Audio.Segments.Items);
        Assert.Single(plan.Clips[1].Audio.Segments.Items);
        Assert.True(plan.Clips[0].Architecture.ConsumesTimelineAudio);
        Assert.False(plan.Clips[1].Architecture.ConsumesTimelineAudio);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void Compile_OverlappingTimelineAudioSegments_RemainIndependentPerClip()
    {
        TimelineSpec spec = Spec(
            false,
            GeneratedClip(0, Stage(10)) with { Frames = 48 },
            GeneratedClip(1, Stage(11)) with { Frames = 48 }) with
        {
            TimelineAudioSegments =
            [
                new("music", null, "audio0", 0.5, 0, 3, 1),
                new("voice", null, "audio1", 1, 2, 2.5, 0.5),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        Assert.Equal(2, plan.Clips[0].Audio.Segments.Items.Length);
        Assert.Equal(2, plan.Clips[1].Audio.Segments.Items.Length);
        Assert.Equal(
            [0, 1],
            plan.Clips[0].Audio.Segments.Items.Select(item => item.AceStepFunTrack));
        Assert.Equal(
            [0, 1],
            plan.Clips[1].Audio.Segments.Items.Select(item => item.AceStepFunTrack));
    }

    [Fact]
    public void Compile_TimelineAudioSeamAnchor_DoesNotLeakAnAlignedFrameIntoPreviousClip()
    {
        TimelineSpec spec = Spec(
            false,
            GeneratedClip(0, Stage(10)) with { Frames = 49 },
            GeneratedClip(1, Stage(11)) with { Frames = 49 }) with
        {
            TimelineAudioSegments =
            [
                new(
                    "seam",
                    null,
                    "audio0",
                    TimelineStartSeconds: 2,
                    SourceStartSeconds: 0,
                    LengthSeconds: 1,
                    Volume: 1,
                    FirstClipId: 1,
                    LastClipId: 1,
                    FirstClipOffsetSeconds: 0,
                    LastClipOffsetSeconds: 1),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        Assert.Empty(plan.Clips[0].Audio.Segments.Items);
        AudioSegmentItemPlan item = Assert.Single(plan.Clips[1].Audio.Segments.Items);
        Assert.Equal(0, item.StartSeconds);
        Assert.Equal(1, item.LengthSeconds);
    }

    private static TimelineSpec Spec(bool isTextToVideo, params ClipSpec[] clips) =>
        new(512, 512, 24, isTextToVideo, clips);

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        new(id, 49, MediaSource.Native, [], false, false, false, false, null, [], stages);

    private static ClipSpec InitVideoClip(int id) =>
        new(id, 49, MediaSource.Native, [], false, false, false, false, null, [], [],
            InitVideo: new InitVideoSpec("data", "source.mp4", 0));

    private static StageSpec Stage(
        int id,
        double control = 1,
        double upscale = 1,
        string upscaleMethod = "pixel-lanczos",
        RetakeWindowSpec retake = null,
        IReadOnlyList<LoraRef> loras = null) =>
        new(id, control, upscale, upscaleMethod, "ltx-2", 12, 4.5, "euler", "normal", "Generated",
            ClipStageIndex: id - 10,
            ClipStageRawIndex: id - 10,
            Loras: loras,
            RetakeWindow: retake);
}
