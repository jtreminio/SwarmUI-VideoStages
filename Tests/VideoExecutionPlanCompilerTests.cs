using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class VideoExecutionPlanCompilerTests
{
    [Fact]
    public void Compile_TextToVideoGeneratedClip_ReplacesRootAndGeneratesFromEmptyLatent()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(
            isTextToVideo: true,
            GeneratedClip(0, Stage(10))));

        Assert.Equal(RootUse.Discard, plan.Root.Use);
        Assert.Equal(HostCoreDisposition.Drop, plan.Root.CoreDisposition);
        Assert.Equal(TimelineOutputDisposition.PublishTimelineOutput, plan.Root.OutputDisposition);
        Assert.Equal(NativeAudioDisposition.DiscardWithRoot, plan.Root.NativeAudioDisposition);
        StagePlan stage = Assert.Single(Assert.Single(plan.Clips).Stages);
        Assert.Equal(StageInputKind.EmptyLatent, stage.Input);
        Assert.False(stage.IsPassthrough);
    }

    [Fact]
    public void Compile_SourcedClip_UsesSourceAndDistinguishesPassthroughRefineAndRetake()
    {
        ClipSpec sourced = new(
            3, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [],
            [
                Stage(10, control: 0),
                Stage(11, control: 0.4),
                Stage(12, control: 0, retake: new RetakeWindowSpec(8, 16, 1)),
            ],
            SourceVideo: new SourceVideoSpec("data", "source.mp4", 0));

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, sourced));

        ClipPlan clip = Assert.Single(plan.Clips);
        Assert.Equal(ClipInputKind.SourceVideo, clip.Input);
        Assert.Equal(StageInputKind.SourceVideo, clip.Stages[0].Input);
        Assert.True(clip.Stages[0].IsPassthrough);
        Assert.False(clip.Stages[1].IsPassthrough);
        Assert.False(clip.Stages[2].IsPassthrough);
        Assert.NotNull(clip.Stages[2].RequireLtx2Payload().Retake);
        Assert.Equal(StageInputKind.PreviousStage, clip.Stages[2].Input);
        Assert.Equal("data", clip.SourceVideo.Data);
        Assert.Equal("source.mp4", clip.SourceVideo.FileName);
        Assert.Equal(0, clip.SourceVideo.StartSeconds);
        Assert.Equal(512, clip.SourceVideo.TargetWidth);
        Assert.Equal(512, clip.SourceVideo.TargetHeight);
        Assert.Equal(24, clip.SourceVideo.TargetFramesPerSecond);
    }

    [Fact]
    public void Compile_RootEnvironment_SeparatesRootUseCoreAndAudioOwnership()
    {
        ClipSpec sourcedLead = SourcedClip(0);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(
            Spec(false, sourcedLead, GeneratedClip(1, Stage(11))),
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true));

        Assert.Equal(RootUse.GeneratedClipDonor, plan.Root.Use);
        Assert.Equal(HostCoreDisposition.Handoff, plan.Root.CoreDisposition);
        Assert.Equal(NativeAudioDisposition.MakeAvailableToTimeline, plan.Root.NativeAudioDisposition);
    }

    [Fact]
    public void Compile_HostRootFirstStage_DoesNotClaimTheEmptyLatentExecutionPath()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, GeneratedClip(0, Stage(10))));

        StagePlan stage = plan.Clips[0].Stages[0];
        Assert.Equal(StageInputKind.RootMedia, stage.Input);
        Assert.False(stage.IsPassthrough);
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

        StageUpscalePlan upscale = plan.Clips[0].Stages[0].RequireLtx2Payload().Upscale;
        Assert.Equal((StageUpscaleMode)expectedValue, upscale.Mode);
        Assert.Equal(2, upscale.Factor);
        Assert.Equal(method, upscale.RawMethod);
    }

    [Fact]
    public void Compile_OptionsAreExpressedAtStageAndClipHooks()
    {
        ClipSpec clip = new(
            4, 49, Constants.AudioSourceUpload,
            [new IcLoraSpec("drive", Constants.IcLoraSourceUpload, 1, 1, Constants.IcLoraControlNone, null, Stage: 0)],
            false, true, false, true,
            new UploadedMediaSpec("data:audio/wav;base64,AA==", "track.wav"),
            [new ImageRefSpec("Upload", 1, false, "ref.png")],
            [Stage(10, loras: [new LoraRef("stage")]), Stage(11)],
            Loras: [new LoraRef("clip")],
            PromptWindows: [new PromptWindowSpec("first", 0, 1)],
            ReferenceFraming: ReferenceFramingMode.FitGreen);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Ltx2ClipPayload ltxClip = Assert.IsType<Ltx2ClipPayload>(
            compiled.ArchitecturePayload);
        Assert.Equal(AudioLengthOwner.Audio, compiled.Audio.Length.Owner);
        Assert.False(ltxClip.AudioReuse.IsEligible);
        Assert.Equal(ReferenceFramingMode.FitGreen, ltxClip.ReferenceFraming);
        Assert.Equal(
            PromptRelayMode.Relay,
            compiled.Stages[0].RequireLtx2Payload().PromptRelay.Mode);
        Assert.Equal(2, compiled.Stages[0].RequireLtx2Payload().Loras.Length);
        Assert.Single(compiled.Stages[0].RequireLtx2Payload().IcLoras);
        Assert.Single(compiled.Stages[0].RequireLtx2Payload().FrameReferences);
        Assert.Empty(compiled.Stages[1].RequireLtx2Payload().IcLoras);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.reuse.requires_three_stages"
            && diagnostic.Severity == PlanDiagnosticSeverity.Warning
            && diagnostic.ClipId == clip.Id);
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
    public void Compile_UnknownGuideReference_IsRejectedBeforeArchitectureCompilation()
    {
        StageSpec stage = Stage(10) with { ImageReference = "not-a-reference" };

        VideoExecutionPlan plan =
            TestPlanCompiler.Compile(Spec(false, GeneratedClip(0, stage)));

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.ClipId == 0
                && diagnostic.StageId == 10
                && diagnostic.Message.Contains("stage image reference 'not-a-reference'"));
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Compile_TypedStageFields_AreActionableWithoutCompatibilitySource()
    {
        List<LoraRef> clipLoras = [new("clip.safetensors", 0.6, 0.4)];
        List<LoraRef> stageLoras = [new("stage.safetensors", 1.2)];
        List<ImageRefSpec> references =
        [
            new("Upload", 5, false, "opening.png", "data:image/png;base64,QQ=="),
            new("edit2", 3, true, null),
        ];
        List<IcLoraSpec> icLoras =
        [
            new(
                IcLoraWeights.AutoModelToken,
                Constants.IcLoraSourceUpload,
                0.8,
                0.7,
                Constants.IcLoraControlCanny,
                new UploadedMediaSpec("data:image/png;base64,Qg==", "drive.png"),
                DriveData: IcLoraDriveData.Visual,
                Preset: "pixel-spatial-upscaler-x2",
                Stage: 1),
            new(
                "control.safetensors",
                Constants.ControlNetSourceTwo,
                1.1,
                1,
                Constants.IcLoraControlDepth,
                null,
                DriveData: IcLoraDriveData.Visual,
                Stage: -1),
            new(
                "other-stage.safetensors",
                Constants.IcLoraSourceUpload,
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
            ImageRefStrengths = [0.25, 0.9],
            ImageRefWasExplicit = true,
        };
        StageSpec last = Stage(12);
        ClipSpec clip = new(
            7,
            72,
            Constants.AudioSourceNative,
            icLoras,
            SaveAudioTrack: true,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: true,
            UploadedAudio: null,
            ImageRefs: references,
            Stages: [first, target, last],
            Loras: clipLoras,
            PromptWindows:
            [
                new("opening prompt", 0, 1),
                new("ending prompt", 2, 1),
            ]);

        StagePlan compiled = TestPlanCompiler
            .Compile(Spec(false, clip))
            .Clips[0].Stages[1];
        Ltx2StagePayload ltx = compiled.RequireLtx2Payload();

        Assert.Equal(StageGuideReferenceKind.Base2Edit, ltx.Guide.Kind);
        Assert.Equal(4, ltx.Guide.ReferencedStageIndex);
        Assert.Equal(StageUpscaleMode.Latent, ltx.Upscale.Mode);
        Assert.Equal(1.5, ltx.Upscale.Factor);
        Assert.Equal("bilinear", ltx.Upscale.MethodName);
        Assert.Equal(11, compiled.StageId);
        Assert.Equal(1, compiled.ClipStageIndex);
        Assert.Equal(1, compiled.ClipStageRawIndex);
        Assert.Equal("ltx-2", ltx.Core.Model);
        Assert.Equal(0.5, ltx.Core.Control);
        Assert.Equal(12, ltx.Core.Steps);
        Assert.Equal(4.5, ltx.Core.CfgScale);
        Assert.Equal("euler", ltx.Core.Sampler);
        Assert.Equal("normal", ltx.Core.Scheduler);
        Assert.Equal(0.55, ltx.Core.ControlNetStrength);
        Assert.True(ltx.Core.ImageReferenceWasExplicit);

        Assert.Collection(
            ltx.Loras,
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
        Assert.Equal(IcLoraMediaSourceKind.Upload, uploaded.MediaInput.Source);
        Assert.Equal(IcLoraDriveMediaKind.Image, uploaded.DriveMedia.Kind);
        Assert.Equal(IcLoraControlMode.Canny, uploaded.ControlMode);
        Assert.Equal(0.55, uploaded.GuideStrength);
        Assert.Equal(IcLoraMediaSourceKind.ControlNet, ltx.IcLoras[1].MediaInput.Source);
        Assert.Equal(1, ltx.IcLoras[1].MediaInput.ControlNetIndex);

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
                Assert.Equal(ImageReferenceFrameOrigin.Start, reference.FrameOrigin);
                Assert.Equal(0.25, reference.Strength);
                Assert.Equal("opening.png", reference.UploadFileName);
            },
            reference =>
            {
                Assert.Equal(ImageReferenceSourceKind.Base2Edit, reference.SourceKind);
                Assert.Equal(2, reference.Base2EditStageIndex);
                Assert.Equal(ImageReferenceFrameOrigin.End, reference.FrameOrigin);
                Assert.Equal(0.9, reference.Strength);
            });
        Assert.Equal(StageAudioAction.CaptureForReuse, ltx.AudioAction);
        Assert.Equal(
            IntermediateOutputPolicy.ControlledByHostSetting,
            compiled.Output.IntermediatePolicy);
        Assert.True(compiled.Output.PreserveConfiguredAudioTrackSave);

        NormalLoraPlan plannedLora = ltx.Loras[1];
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
    public void Compile_PromptRelayWithDynamicLength_IsRejected(bool controlNetOwnsLength)
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            Frames = null,
            AudioSource = controlNetOwnsLength ? Constants.AudioSourceControlNet : Constants.AudioSourceUpload,
            UploadedAudio = controlNetOwnsLength ? null : new UploadedMediaSpec("audio", "track.wav"),
            ClipLengthFromAudio = !controlNetOwnsLength,
            ClipLengthFromControlNet = controlNetOwnsLength,
            IcLoras = controlNetOwnsLength
                ? [new IcLoraSpec(
                    "control",
                    Constants.ControlNetSourceOne,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null,
                    DriveData: IcLoraDriveData.Visual)]
                : [],
            PromptWindows = [new PromptWindowSpec("opening", 0, 1)],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "prompt-relay-dynamic-length-unsupported"
            && diagnostic.Severity == PlanDiagnosticSeverity.Error
            && diagnostic.ClipId == clip.Id);
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
                    Constants.ControlNetSourceTwo,
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
    public void Compile_ControlSignalDerivedDurationWithoutLtxSource_IsAPlanningError()
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
                && diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.ClipId == clip.Id);
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Compile_RetakeWithFrameReferences_IsRejected()
    {
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, retake: new RetakeWindowSpec(8, 16, 1))) with
        {
            ImageRefs = [new ImageRefSpec("Upload", 2, false, "ref.png", "image")],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, clip));

        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "retake-frame-references-unsupported"
            && diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_MixedHdrMultiClipTimeline_IsRejected()
    {
        IcLoraSpec hdr = new(
            "ltx-2.3-22b-ic-lora-hdr-0.9",
            Constants.IcLoraSourceUpload,
            1,
            1,
            Constants.IcLoraControlNone,
            null,
            Preset: "hdr",
            Hdr: true);
        ClipSpec hdrClip = GeneratedClip(0, Stage(10)) with { IcLoras = [hdr] };
        ClipSpec sdrClip = GeneratedClip(1, Stage(11));

        VideoExecutionPlan mixed = TestPlanCompiler.Compile(Spec(false, hdrClip, sdrClip));
        VideoExecutionPlan allHdr = TestPlanCompiler.Compile(
            Spec(false, hdrClip, GeneratedClip(1, Stage(11)) with { IcLoras = [hdr] }));

        Assert.Contains(mixed.Diagnostics, diagnostic =>
            diagnostic.Code == "mixed-hdr-timeline-unsupported"
            && diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.DoesNotContain(
            allHdr.Diagnostics,
            diagnostic => diagnostic.Code == "mixed-hdr-timeline-unsupported");
    }

    [Fact]
    public void Compile_DuplicateClipIds_RejectsLaterOccurrenceDeterministically()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(
            false,
            GeneratedClip(4, Stage(10)),
            GeneratedClip(4, Stage(11))));

        Assert.Single(plan.Clips);
        Assert.Equal(4, plan.Clips[0].ClipId);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "duplicate-clip-id"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ContinueIntoSourcedClip_PreservesInvalidAuthoredBoundaryAndBlocksGeneration()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            Frames = 49,
            BoundaryOut = Constants.BoundaryOutContinue,
            BoundaryOutOverlap = 16,
        };
        ClipSpec source = SourcedClip(1);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, source));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryExecutionMode.Continue, boundary.Requested);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(BoundaryFallback.ArchitectureRuleUnsupported, boundary.Fallback);
        Assert.Equal(0, boundary.ContinuityWindowFrames);
        Assert.Contains(plan.Diagnostics, d =>
            d.Code == "boundary-cross-architecture-non-cut"
            && d.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ContinueWithFirstFrameReference_FallsBackToCut()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        ClipSpec next = GeneratedClip(1, Stage(11)) with
        {
            ImageRefs = [new ImageRefSpec("Upload", 1, false, "first.png")],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, next));

        Assert.Equal(BoundaryFallback.TargetHasFirstFrameReference, plan.Boundaries[0].Fallback);
        Assert.Equal(BoundaryExecutionMode.Cut, plan.Boundaries[0].Effective);
    }

    [Fact]
    public void Compile_ValidContinue_OnlyMarksItsTargetClipAsUsingContinuity()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        Assert.Equal(BoundaryExecutionMode.Continue, plan.Boundaries[0].Effective);
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

        Assert.Equal(BoundaryExecutionMode.Cut, plan.Boundaries[0].Effective);
        Assert.Equal(0, plan.Boundaries[0].ContinuityWindowFrames);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-frame-budget-reconciled");
    }

    [Fact]
    public void Compile_Crossfade_PreservesRequestedBoundaryForRuntimeMergeValidation()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            BoundaryOut = Constants.BoundaryOutCrossfade,
            BoundaryOutOverlap = 24,
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Effective);
        Assert.Equal(24, boundary.OverlapFrames);
        Assert.True(boundary.RequiresRuntimeMergeValidation);
        Assert.Single(plan.Boundaries);
        Assert.All(
            plan.Clips.Select(clip => Assert.Single(clip.Stages).Output),
            output =>
            {
                Assert.False(output.IsTimelineTerminal);
            });
    }

    [Fact]
    public void Compile_TimelineAudioSegment_CutsAtEveryClipAndAdvancesSourceOffset()
    {
        VideoStagesSpec spec = Spec(
            false,
            GeneratedClip(0, Stage(10)) with { Frames = 48 },
            GeneratedClip(1, Stage(11)) with { Frames = 48 },
            GeneratedClip(2, Stage(12)) with { Frames = 48 }) with
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
        Assert.Equal(3, projected.Length);
        Assert.Equal([1.5d, 0d, 0d], projected.Select(item => item.StartSeconds));
        Assert.Equal([0.5d, 2d, 0.5d], projected.Select(item => item.LengthSeconds));
        Assert.Equal([10d, 10.5d, 12.5d], projected.Select(item => item.TrimStartSeconds));
        Assert.All(projected, item =>
        {
            Assert.Equal(AudioSourceKind.Upload, item.SourceKind);
            Assert.Equal("dialogue.wav", item.UploadedMedia.FileName);
            Assert.Equal(0.75, item.Volume);
        });
    }

    [Fact]
    public void Compile_OverlappingTimelineAudioSegments_RemainIndependentPerClip()
    {
        VideoStagesSpec spec = Spec(
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
        Assert.Contains(plan.AudioTimeline.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.overlapping_tracks");
    }

    [Fact]
    public void Compile_TimelineAudioSeamAnchor_DoesNotLeakAnAlignedFrameIntoPreviousClip()
    {
        VideoStagesSpec spec = Spec(
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
        Assert.Equal(49d / 24, Assert.Single(
            plan.AudioTimeline.Tracks.Where(track => track.TrackId == "seam"))
            .Windows[0]
            .TimelineStartSeconds);
    }

    private static VideoStagesSpec Spec(bool isTextToVideo, params ClipSpec[] clips) =>
        new(512, 512, 24, isTextToVideo, clips);

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        new(id, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], stages);

    private static ClipSpec SourcedClip(int id) =>
        new(id, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], [],
            SourceVideo: new SourceVideoSpec("data", "source.mp4", 0));

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
