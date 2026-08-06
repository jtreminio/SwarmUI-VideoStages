using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.MiniMax;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxArchitectureTests
{
    [Fact]
    public void The_checkpoint_resolves_to_MiniMax_rather_than_the_host_video_fallback()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        // The fallback also claims this compat class, so the specialized tier is what keeps H3
        // off the video-only stock path.
        Assert.True(HostVideoArchitectureModule.Instance.TryResolveModel(
            models.VideoModel,
            out _));
        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        Assert.Equal(MiniMaxArchitectureModule.ArchitectureId, resolved.ArchitectureId);
        Assert.Equal(MiniMaxArchitectureModule.ProfileId, resolved.ModelProfileId);
    }

    // Core registers the checkpoint and both support VAEs under the same compat class, so the
    // class ID is the only thing separating a generation model from a support model.
    [Theory]
    [InlineData("minimax-h3", true)]
    [InlineData("MiniMax-H3", true)]
    [InlineData("minimax-h3/vae", false)]
    [InlineData("minimax-h3/audio-vae", false)]
    public void Only_the_exact_checkpoint_class_is_a_generation_model(
        string modelClassId,
        bool expectResolved)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
        };

        bool didResolve = MiniMaxArchitectureModule.Instance.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved);

        Assert.Equal(expectResolved, didResolve);
        if (!expectResolved)
        {
            Assert.Null(resolved);
            return;
        }
        Assert.Equal(modelClassId, resolved.ModelClassId);
        Assert.Equal(MiniMaxArchitectureModule.ProfileId, resolved.ModelProfileId);
    }

    [Fact]
    public void The_published_catalog_states_the_frame_grid_audio_and_boundary_rules()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            VideoArchitectureRegistry.Production);
        JObject architecture = Assert.Single(
            ((JArray)catalog["architectures"]).Values<JObject>(),
            item => item["id"]?.ToString() == "minimax");
        JObject model = Assert.Single(
            ((JArray)catalog["models"]).Values<JObject>(),
            item => item["modelName"]?.ToString() == models.VideoModel.Name);

        Assert.Equal("MiniMax H3", architecture.Value<string>("label"));
        Assert.Equal(17, model.Value<int>("frameGrid"));
        Assert.Equal(5, model.Value<int>("frameGridOrigin"));
        Assert.Equal(
            ["Native", "Upload", "ControlNet", "AceStepFun"],
            architecture["capabilities"]["audioSourceKinds"].Values<string>());
        Assert.Equal(
            ["text-to-video", "image-to-video", "init-video"],
            architecture["capabilities"]["entryModes"].Values<string>());
        Assert.Equal(
            [
                "frameReferences",
                "clipReferences",
                "referenceFraming",
                "audioBoundaryCarry",
                "latentUpscale",
                "audioReuse",
                "audioDerivedDuration",
            ],
            architecture["capabilities"]["features"].Values<string>());
        Assert.Equal(
            "supported",
            architecture["boundaryRules"]["cut"].Value<string>("support"));
        Assert.Equal(
            "unsupported",
            architecture["boundaryRules"]["continue"].Value<string>("support"));
        Assert.Equal(
            "conditional",
            architecture["boundaryRules"]["crossfade"].Value<string>("support"));
    }

    [Theory]
    [InlineData(Constants.AudioSourceUpload)]
    [InlineData(Constants.AudioSourceControlNet)]
    [InlineData("audio0")]
    public void External_audio_can_drive_MiniMax_duration(string audioSource)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = audioSource;
        clip["clipLengthFromAudio"] = true;
        if (audioSource == Constants.AudioSourceUpload)
        {
            clip["uploadedAudio"] = new JObject
            {
                ["data"] = "data:audio/wav;base64,QUJD",
                ["fileName"] = "clip.wav",
            };
        }
        ClipSpec parsed = ParseClip(clip, models);
        VideoArchitectureDescriptor descriptor = MiniMaxArchitectureModule.Instance.Descriptor;

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                parsed,
                descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.True(descriptor.Features.HasFlag(ArchitectureFeature.AudioDerivedDuration));
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Code
                == "audio.length.source_cannot_drive_duration");
    }

    [Fact]
    public void Native_H3_audio_cannot_drive_MiniMax_duration()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["clipLengthFromAudio"] = true;

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                ParseClip(clip, models),
                MiniMaxArchitectureModule.Instance.Descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "audio.length.source_cannot_drive_duration");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void A_second_stage_compiles_at_every_usable_control(double control)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        Assert.Empty(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: control,
                steps: 8,
                cfgScale: 1)));
    }

    [Fact]
    public void A_partial_control_that_rounds_to_start_step_zero_is_refused()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        PlanDiagnostic refusal = Assert.Single(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.99,
                steps: 8,
                cfgScale: 1)));
        Assert.Equal(PlanDiagnosticSeverity.Error, refusal.Severity);
        Assert.Equal("minimax.stage-control.quantized-zero", refusal.Code);
    }

    [Fact]
    public void A_guided_cfg_warns_without_blocking_the_clip()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        PlanDiagnostic warning = Assert.Single(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 7)));
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("minimax.stage.cfg-scale.non-unity", warning.Code);
    }

    [Fact]
    public void Init_video_rejects_audio_derived_duration()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8, cfgScale: 1));
        clipObject["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 0,
        };
        clipObject["audioSource"] = Constants.AudioSourceUpload;
        clipObject["clipLengthFromAudio"] = true;
        clipObject["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "source.wav",
        };

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models,
            ArchitectureEntryMode.InitVideo);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code
                == "minimax.init-video.audio-derived-duration-unsupported"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Latent_interpolation_activates_a_zero_control_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject stageObject = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            upscale: 1.5,
            upscaleMethod: "latent-bislerp",
            steps: 8,
            cfgScale: 1);
        ClipSpec clip = ParseClip(
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                stageObject),
            models);
        StageSpec stage = clip.Stages[^1];
        VideoArchitectureDescriptor descriptor = MiniMaxArchitectureModule.Instance.Descriptor;

        Assert.False(ArchitectureStageActivity.IsPassthrough(stage, descriptor));
    }

    [Fact]
    public void Latent_model_upscale_remains_unsupported()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject stageObject = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            upscale: 1.5,
            upscaleMethod: "latentmodel-unit-upscaler.safetensors",
            steps: 8,
            cfgScale: 1);
        ClipSpec clip = ParseClip(
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                stageObject),
            models);
        VideoArchitectureDescriptor descriptor = MiniMaxArchitectureModule.Instance.Descriptor;

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.True(ArchitectureStageActivity.IsPassthrough(clip.Stages[^1], descriptor));
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-latent-upscale-ignored");
    }

    [Fact]
    public void Latent_interpolation_projects_cross_clip_geometry()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        ClipSpec authored = ParseClip(
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    upscale: 1.5,
                    upscaleMethod: "latent-bislerp",
                    steps: 8,
                    cfgScale: 1)),
            models);
        ArchitectureClipCompilation compilation = Compile(authored, models);
        StageSpec authoredUpscale = authored.Stages[^1];
        StagePlan upscale = new(
            authoredUpscale.Id,
            authoredUpscale.ClipStageIndex,
            authoredUpscale.ClipStageRawIndex,
            StageInputKind.PreviousStage,
            false,
            compilation.StagePayloads[authoredUpscale.ClipStageRawIndex],
            new(IntermediateOutputEligibility.NotEligible, false));
        ClipPlan scaled = new(
            0,
            49,
            ArchitectureEntryMode.ImageToVideo,
            null,
            [upscale],
            AudioPlanCompiler.Compile(authored))
        {
            ArchitecturePayload = compilation.Payload,
        };
        ClipPlan plain = scaled with
        {
            ClipId = 1,
            Stages = [],
        };

        PlanDiagnostic diagnostic = Assert.Single(
            ClipGeometryProjection.Validate([scaled, plain], 512, 512),
            entry => entry.Code == "clip-geometry-will-conform");

        Assert.Equal(0, diagnostic.ClipId);
        Assert.Contains("768x768", diagnostic.Message);
        Assert.Contains("512x512", diagnostic.Message);
    }

    [Fact]
    public void Bounded_first_and_last_frame_references_compile_and_others_are_dropped()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        ClipSpec clip = ParseClip(
            MakeClipWithRefs(
                [
                    UploadedReference(fromEnd: false, frame: 1),
                    UploadedReference(fromEnd: true, frame: 1),
                    UploadedReference(fromEnd: false, frame: 9),
                ],
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1)),
            models);

        ArchitectureClipCompilation compilation = Compile(clip, models);
        MiniMaxClipPayload payload =
            Assert.IsType<MiniMaxClipPayload>(compilation.Payload);

        Assert.NotNull(payload.FirstFrameReference);
        Assert.NotNull(payload.LastFrameReference);

        Assert.Single(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.minimax-middle-frame-reference-ignored");
    }

    [Fact]
    public void Clip_references_compile_in_authored_order_with_their_paired_soundtrack()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clipObject["references"] = new JArray
        {
            ClipReference("image", "Upload", "subject.png", "data:image/png;base64,QUJD"),
            ClipReference("image", "Base", null, null),
            ClipReference(
                "video",
                "Upload",
                "motion.mp4",
                "data:video/mp4;base64,REVG",
                includeSoundtrack: true,
                mediaScale: 0.5),
            ClipReference(
                "audio",
                "Upload",
                "voice.wav",
                "data:audio/wav;base64,QUJD",
                mediaScale: 0.25),
        };

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models);

        Assert.Empty(compilation.Diagnostics);
        MiniMaxClipPayload payload =
            Assert.IsType<MiniMaxClipPayload>(compilation.Payload);
        Assert.Equal(
            [
                ClipReferenceKind.Image,
                ClipReferenceKind.Image,
                ClipReferenceKind.Video,
                ClipReferenceKind.Audio,
            ],
            payload.References.Select(reference => reference.Kind));
        Assert.Equal("Base", payload.References[1].Source);
        Assert.Null(payload.References[1].Media);
        Assert.True(payload.References[2].IncludeSoundtrack);
        Assert.False(payload.References[3].IncludeSoundtrack);
        // Only a video reference has frames to downsample.
        Assert.Equal([1, 1, 0.5, 1], payload.References.Select(r => r.MediaScale));
    }

    /// <summary>
    /// Base2Edit publishes each edit stage's image for other extensions to build on, so an image
    /// reference may name one exactly as it names the host's own base and refiner outputs.
    /// </summary>
    [Fact]
    public void An_image_clip_reference_can_name_a_base2edit_edit_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clipObject["references"] = new JArray
        {
            ClipReference("image", "edit2", null, null),
        };

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models);

        Assert.Empty(compilation.Diagnostics);
        MiniMaxReferencePlan reference = Assert.Single(
            Assert.IsType<MiniMaxClipPayload>(compilation.Payload).References);
        Assert.Equal("edit2", reference.Source);
        Assert.Null(reference.Media);
    }

    [Theory]
    [InlineData("video", "Base", "minimax.clip-reference.source-ignored")]
    [InlineData("audio", "Refiner", "minimax.clip-reference.source-ignored")]
    [InlineData("image", "ControlNet 1", "minimax.clip-reference.source-ignored")]
    public void A_clip_reference_from_an_unreadable_source_is_warned_and_dropped(
        string kind,
        string source,
        string expectedCode)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clipObject["references"] = new JArray
        {
            ClipReference(kind, source, "media.bin", "data:x;base64,QQ=="),
        };

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models);

        PlanDiagnostic warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(expectedCode, warning.Code);
        Assert.Empty(Assert.IsType<MiniMaxClipPayload>(compilation.Payload).References);
    }

    [Fact]
    public void An_uploaded_clip_reference_with_no_media_is_warned_and_dropped()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clipObject["references"] = new JArray
        {
            ClipReference("image", "Upload", null, null),
        };

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models);

        Assert.Equal(
            "minimax.clip-reference.media-missing",
            Assert.Single(compilation.Diagnostics).Code);
        Assert.Empty(Assert.IsType<MiniMaxClipPayload>(compilation.Payload).References);
    }

    [Fact]
    public void References_past_the_node_input_cap_stay_saved_but_are_ignored()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clipObject = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clipObject["references"] = new JArray(
            Enumerable.Range(0, MiniMaxClipReferences.MaxVideos + 2).Select(index =>
                ClipReference(
                    "video",
                    "Upload",
                    $"motion{index}.mp4",
                    "data:video/mp4;base64,REVG")));

        ArchitectureClipCompilation compilation = Compile(
            ParseClip(clipObject, models),
            models);

        Assert.Equal(
            MiniMaxClipReferences.MaxVideos,
            Assert.IsType<MiniMaxClipPayload>(compilation.Payload).References.Count);
        Assert.Equal(2, compilation.Diagnostics.Count);
        Assert.All(
            compilation.Diagnostics,
            diagnostic => Assert.Equal(
                "minimax.clip-reference.limit-exceeded",
                diagnostic.Code));
    }

    private static JObject ClipReference(
        string kind,
        string source,
        string fileName,
        string data,
        bool includeSoundtrack = false,
        double mediaScale = 1)
    {
        JObject reference = new()
        {
            ["kind"] = kind,
            ["source"] = source,
            ["includeSoundtrack"] = includeSoundtrack,
            ["mediaScale"] = mediaScale,
        };
        if (data is not null)
        {
            reference["uploadedMedia"] = new JObject
            {
                ["data"] = data,
                ["fileName"] = fileName,
            };
        }
        return reference;
    }

    private static IReadOnlyList<PlanDiagnostic> CompileDiagnostics(
        TestModelBundle models,
        params JObject[] stages) =>
        Compile(ParseClip(MakeClip(stages), models), models).Diagnostics;

    private static ArchitectureClipCompilation Compile(
        ClipSpec clip,
        TestModelBundle models,
        ArchitectureEntryMode entryMode = ArchitectureEntryMode.ImageToVideo)
    {
        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        Dictionary<int, ResolvedVideoModel> stageModels = (clip.Stages ?? [])
            .ToDictionary(stage => stage.ClipStageRawIndex, _ => resolved);
        return MiniMaxArchitectureModule.Instance.ValidateAndCompileClip(
            clip,
            stageModels,
            new(1344, 768, 24, entryMode));
    }

    private static ClipSpec ParseClip(JObject clip, TestModelBundle models)
    {
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        TimelineSpec spec = RequestCaches.GetTimelineSpecForPromptParse(input);
        return Assert.Single(spec.Clips);
    }
}
