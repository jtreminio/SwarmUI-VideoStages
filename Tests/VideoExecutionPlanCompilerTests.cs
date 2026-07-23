using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class VideoExecutionPlanCompilerTests
{
    [Fact]
    public void Compile_TextToVideoGeneratedClip_ReplacesRootAndGeneratesFromEmptyLatent()
    {
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(
            isTextToVideo: true,
            GeneratedClip(0, Stage(10))));

        Assert.Equal(VideoModelFamily.Ltx, plan.ModelFamily);
        Assert.Equal(RootUse.Discard, plan.Root.Use);
        Assert.Equal(HostCoreDisposition.Drop, plan.Root.CoreDisposition);
        Assert.Equal(TimelineOutputDisposition.PublishTimelineOutput, plan.Root.OutputDisposition);
        Assert.Equal(NativeAudioDisposition.DiscardWithRoot, plan.Root.NativeAudioDisposition);
        StagePlan stage = Assert.Single(Assert.Single(plan.Clips).Stages);
        Assert.Equal(StageInputKind.EmptyLatent, stage.Input);
        Assert.Equal(StageExecutionMode.GenerateFromEmptyLatent, stage.Execution);
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

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, sourced));

        ClipPlan clip = Assert.Single(plan.Clips);
        Assert.Equal(ClipInputKind.SourceVideo, clip.Input);
        Assert.Equal(StageInputKind.SourceVideo, clip.Stages[0].Input);
        Assert.Equal(StageExecutionMode.Passthrough, clip.Stages[0].Execution);
        Assert.Equal(StageExecutionMode.Refine, clip.Stages[1].Execution);
        Assert.Equal(StageExecutionMode.Retake, clip.Stages[2].Execution);
        Assert.Equal(StageInputKind.PreviousStage, clip.Stages[2].Input);
        Assert.Equal("data", clip.SourceVideo.Data);
        Assert.Equal("source.mp4", clip.SourceVideo.FileName);
        Assert.Equal(0, clip.SourceVideo.StartSeconds);
        Assert.Equal(49, clip.SourceVideo.TargetFrames);
        Assert.Equal(512, clip.SourceVideo.TargetWidth);
        Assert.Equal(512, clip.SourceVideo.TargetHeight);
        Assert.Equal(24, clip.SourceVideo.TargetFramesPerSecond);
    }

    [Fact]
    public void Compile_RootEnvironment_SeparatesRootUseCoreAndAudioOwnership()
    {
        ClipSpec sourcedLead = SourcedClip(0);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            Spec(false, sourcedLead, GeneratedClip(1, Stage(11))),
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true));

        Assert.Equal(RootUse.GeneratedClipDonor, plan.Root.Use);
        Assert.Equal(HostCoreDisposition.Handoff, plan.Root.CoreDisposition);
        Assert.Equal(NativeAudioDisposition.MakeAvailableToTimeline, plan.Root.NativeAudioDisposition);
    }

    [Fact]
    public void Compile_HostRootFirstStage_DoesNotClaimTheEmptyLatentExecutionPath()
    {
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, GeneratedClip(0, Stage(10))));

        StagePlan stage = plan.Clips[0].Stages[0];
        Assert.Equal(StageInputKind.RootMedia, stage.Input);
        Assert.Equal(StageExecutionMode.GenerateOrRefineFromRootMedia, stage.Execution);
    }

    [Theory]
    [InlineData("pixel-lanczos", (int)StageUpscaleMode.Pixel)]
    [InlineData("model-upscaler.safetensors", (int)StageUpscaleMode.Model)]
    [InlineData("latent-bilinear", (int)StageUpscaleMode.Latent)]
    [InlineData("latentmodel-upscaler.safetensors", (int)StageUpscaleMode.LatentModel)]
    [InlineData("unknown", (int)StageUpscaleMode.Unsupported)]
    public void Compile_StageUpscaleMethods_AreClassified(string method, int expectedValue)
    {
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false,
            GeneratedClip(0, Stage(10, control: 0.5, upscale: 2, upscaleMethod: method))));

        StageUpscalePlan upscale = plan.Clips[0].Stages[0].Upscale;
        Assert.Equal((StageUpscaleMode)expectedValue, upscale.Mode);
        Assert.Equal(2, upscale.Factor);
        Assert.Equal(method, upscale.RawMethod);
    }

    [Fact]
    public void Compile_OptionsAreExpressedAtStageAndClipHooks()
    {
        ClipSpec clip = new(
            4, 49, Constants.AudioSourceVoiceRef,
            [new IcLoraSpec("drive", Constants.IcLoraSourceUpload, 1, 1, Constants.IcLoraControlNone, null, Stage: 0)],
            false, true, false, true, null,
            [new ImageRefSpec("Upload", 1, false, "ref.png")],
            [Stage(10, loras: [new LoraRef("stage")]), Stage(11)],
            AudioSegments: [new AudioSegmentSpec(new UploadedAudioSpec("audio", "clip.wav"), 0, 0, 1)],
            Loras: [new LoraRef("clip")],
            PromptWindows: [new PromptWindowSpec("first", 0, 1)]);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, clip));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(AudioLengthOwner.Audio, compiled.Audio.Length.Owner);
        Assert.True(compiled.Audio.VoiceReference.IsRequested);
        Assert.False(compiled.Audio.Reuse.IsEligible);
        Assert.Single(compiled.Audio.Segments.Items);
        Assert.Equal(PromptRelayMode.Relay, compiled.Stages[0].PromptRelay.Mode);
        Assert.Equal(2, compiled.Stages[0].Loras.Length);
        Assert.Single(compiled.Stages[0].IcLoras);
        Assert.Single(compiled.Stages[0].FrameReferences);
        Assert.Empty(compiled.Stages[1].IcLoras);
        Assert.Contains(compiled.Audio.Diagnostics, d => d.Code == "audio.reuse.requires_three_stages");
    }

    [Theory]
    [InlineData("Base", (int)GuideReferenceKind.Base, -1)]
    [InlineData("Refiner", (int)GuideReferenceKind.Refiner, -1)]
    [InlineData("Generated", (int)GuideReferenceKind.Generated, -1)]
    [InlineData("PreviousStage", (int)GuideReferenceKind.PreviousStage, -1)]
    [InlineData("Stage3", (int)GuideReferenceKind.ExplicitStage, 3)]
    [InlineData("edit4", (int)GuideReferenceKind.Base2Edit, 4)]
    [InlineData("not-a-reference", (int)GuideReferenceKind.Unknown, -1)]
    public void Compile_GuideReferenceIntent_IsTyped(
        string raw,
        int expectedKindValue,
        int expectedStageIndex)
    {
        StageSpec stage = Stage(10) with { ImageReference = raw };

        GuideReferencePlan guide = VideoExecutionPlanCompiler
            .Compile(Spec(false, GeneratedClip(0, stage)))
            .Clips[0].Stages[0].Guide;

        Assert.Equal((GuideReferenceKind)expectedKindValue, guide.Kind);
        Assert.Equal(expectedStageIndex < 0 ? null : expectedStageIndex, guide.ReferencedStageIndex);
        Assert.Equal(raw, guide.RawValue);
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
                Constants.IcLoraAutoModel,
                Constants.IcLoraSourceUpload,
                0.8,
                0.7,
                Constants.IcLoraControlCanny,
                new UploadedAudioSpec("data:image/png;base64,Qg==", "drive.png"),
                Preset: "upscaler-x2",
                Stage: 1,
                DriveAudioRef: true),
            new(
                "control.safetensors",
                Constants.ControlNetSourceTwo,
                1.1,
                1,
                Constants.IcLoraControlDepth,
                null,
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

        StagePlan compiled = VideoExecutionPlanCompiler
            .Compile(Spec(false, clip))
            .Clips[0].Stages[1];

        Assert.Equal(GuideReferenceKind.Base2Edit, compiled.Guide.Kind);
        Assert.Equal(4, compiled.Guide.ReferencedStageIndex);
        Assert.Equal(StageUpscaleMode.Latent, compiled.Upscale.Mode);
        Assert.Equal(1.5, compiled.Upscale.Factor);
        Assert.Equal("bilinear", compiled.Upscale.MethodName);
        Assert.Equal(11, compiled.StageId);
        Assert.Equal(1, compiled.ClipStageIndex);
        Assert.Equal(1, compiled.ClipStageRawIndex);
        Assert.Equal("ltx-2", compiled.Core.Model);
        Assert.Equal(0.5, compiled.Core.Control);
        Assert.Equal(12, compiled.Core.Steps);
        Assert.Equal(4.5, compiled.Core.CfgScale);
        Assert.Equal("euler", compiled.Core.Sampler);
        Assert.Equal("normal", compiled.Core.Scheduler);
        Assert.Equal(0.55, compiled.Core.ControlNetStrength);
        Assert.True(compiled.Core.ImageReferenceWasExplicit);

        Assert.Collection(
            compiled.Loras,
            lora =>
            {
                Assert.Equal(NormalLoraScope.Clip, lora.Scope);
                Assert.Equal("clip.safetensors", lora.Name);
                Assert.Equal(0.6, lora.ModelWeight);
                Assert.Equal(0.4, lora.TextEncoderWeight);
            },
            lora =>
            {
                Assert.Equal(NormalLoraScope.Stage, lora.Scope);
                Assert.Equal("stage.safetensors", lora.Name);
                Assert.Equal(1.2, lora.TextEncoderWeight);
            });

        Assert.Equal(2, compiled.IcLoras.Length);
        IcLoraPlan uploaded = compiled.IcLoras[0];
        Assert.True(uploaded.UsesAutoModel);
        Assert.Equal(IcLoraDriveSourceKind.UploadedMedia, uploaded.Drive.Kind);
        Assert.Equal(IcLoraUploadedMediaKind.Image, uploaded.Drive.UploadedMediaKind);
        Assert.Equal(IcLoraControlMode.Canny, uploaded.ControlMode);
        Assert.Equal(IcLoraGuideStrengthSource.StageOverride, uploaded.GuideStrengthSource);
        Assert.Equal(0.55, uploaded.GuideStrength);
        Assert.True(uploaded.DrivesAudioReference);
        Assert.Equal(IcLoraDriveSourceKind.ControlNet, compiled.IcLoras[1].Drive.Kind);
        Assert.Equal(1, compiled.IcLoras[1].Drive.ControlNetIndex);

        Assert.Equal(new RetakePlan(8, 16, 24, 0.75), compiled.Retake);
        Assert.Equal(PromptRelayMode.Relay, compiled.PromptRelay.Mode);
        Assert.Equal(2, compiled.PromptRelay.AuthoredWindows.Length);
        Assert.Contains(compiled.PromptRelay.Segments, segment => segment.Prompt == "opening prompt");
        Assert.Collection(
            compiled.FrameReferences,
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
        Assert.Equal(StageAudioAction.CaptureForReuse, compiled.AudioAction);
        Assert.False(compiled.Output.IsClipTerminal);
        Assert.Equal(
            IntermediateOutputPolicy.ControlledByHostSetting,
            compiled.Output.IntermediatePolicy);
        Assert.True(compiled.Output.PreserveConfiguredAudioTrackSave);

        NormalLoraPlan plannedLora = Assert.Single(
            compiled.Loras,
            lora => lora.Scope == NormalLoraScope.Stage);
        Assert.Equal("stage.safetensors", plannedLora.Name);
        Assert.Null(plannedLora.AuthoredTextEncoderWeight);
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

        PromptRelayPlan relay = VideoExecutionPlanCompiler.Compile(Spec(false, clip))
            .Clips[0].Stages[0].PromptRelay;

        Assert.Equal(PromptRelayMode.Relay, relay.Mode);
        Assert.Equal(49d / 24d, relay.Segments.Sum(segment => segment.Seconds), 12);
    }

    [Fact]
    public void Compile_DuplicateClipIds_RejectsLaterOccurrenceDeterministically()
    {
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(
            false,
            GeneratedClip(4, Stage(10)),
            GeneratedClip(4, Stage(11))));

        Assert.Single(plan.Clips);
        Assert.Equal(4, plan.Clips[0].ClipId);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "duplicate-clip-id"
                && diagnostic.Severity == VideoPlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ContinueIntoSourcedClip_FallsBackToCutWithDiagnostic()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            Frames = 49,
            BoundaryOut = Constants.BoundaryOutContinue,
            BoundaryOutOverlap = 16,
        };
        ClipSpec source = SourcedClip(1);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, first, source));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryExecutionMode.Continue, boundary.Requested);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(BoundaryFallback.TargetIsSourcedVideo, boundary.Fallback);
        Assert.Equal(0, boundary.ContinuityWindowFrames);
        Assert.Contains(plan.Diagnostics, d => d.Code == "boundary-targetissourcedvideo");
    }

    [Fact]
    public void Compile_ContinueWithFirstFrameReference_FallsBackToCut()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        ClipSpec next = GeneratedClip(1, Stage(11)) with
        {
            ImageRefs = [new ImageRefSpec("Upload", 1, false, "first.png")],
        };

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, first, next));

        Assert.Equal(BoundaryFallback.TargetHasFirstFrameReference, plan.Boundaries[0].Fallback);
        Assert.Equal(BoundaryExecutionMode.Cut, plan.Boundaries[0].Effective);
        Assert.False(plan.Clips[1].UsesIncomingContinuity);
    }

    [Fact]
    public void Compile_ValidContinue_OnlyMarksItsTargetClipAsUsingContinuity()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with { BoundaryOut = Constants.BoundaryOutContinue };
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        Assert.Equal(BoundaryExecutionMode.Continue, plan.Boundaries[0].Effective);
        Assert.True(plan.Clips[1].UsesIncomingContinuity);
    }

    [Fact]
    public void Compile_Crossfade_PreservesRequestedBoundaryForRuntimeMergeValidation()
    {
        ClipSpec first = GeneratedClip(0, Stage(10)) with
        {
            BoundaryOut = Constants.BoundaryOutCrossfade,
            BoundaryOutOverlap = 24,
        };

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(Spec(false, first, GeneratedClip(1, Stage(11))));

        BoundaryPlan boundary = plan.Boundaries[0];
        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Effective);
        Assert.Equal(24, boundary.OverlapFrames);
        Assert.True(boundary.RequiresRuntimeMergeValidation);
        Assert.Single(plan.Boundaries);
        Assert.All(
            plan.Clips.Select(clip => Assert.Single(clip.Stages).Output),
            output =>
            {
                Assert.True(output.FeedsClipAssembly);
                Assert.False(output.IsTimelineTerminal);
            });
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
