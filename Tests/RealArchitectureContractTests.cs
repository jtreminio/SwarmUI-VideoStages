using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Shared executable contracts for the real architecture modules. Family-specific setup ends at
/// <see cref="FamilyFixture"/>; assertions below describe only the common architecture boundary.
/// </summary>
[Collection("VideoStagesTests")]
public class RealArchitectureContractTests
{
    private sealed record FamilyFixture(
        IVideoArchitectureModule Module,
        T2IModel BaseModel,
        T2IModel Model,
        ModelProfileId ModelProfileId,
        string CloseImpostorClassId)
    {
        internal VideoArchitectureDescriptor Descriptor => Module.Descriptor;

        internal ClipSpec MinimalClip() =>
            new(
                0,
                25,
                Constants.AudioSourceNative,
                [],
                false,
                false,
                false,
                false,
                null,
                [],
                [
                    new(
                        10,
                        1,
                        1,
                        "pixel-lanczos",
                        Model.Name,
                        12,
                        4.5,
                        "euler",
                        "normal",
                        "Generated"),
                ]);
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Model_recognition_is_exact_and_returns_the_declared_identity(string family)
    {
        using SwarmUiTestContext context = new();
        FamilyFixture fixture = CreateFixture(family);
        VideoArchitectureRegistry registry = RealRegistry();

        Assert.True(registry.TryResolveModel(
            fixture.Model,
            out ResolvedVideoModel resolved));
        Assert.Equal(fixture.Descriptor.Id, resolved.ArchitectureId);
        Assert.Equal(fixture.ModelProfileId, resolved.ModelProfileId);
        Assert.Same(fixture.Descriptor, resolved.Architecture);
        Assert.Equal(fixture.Model.Name, resolved.ModelName);

        fixture.Model.ModelClass = fixture.Model.ModelClass with
        {
            ID = fixture.CloseImpostorClassId,
        };
        bool closeVariantSupported = family == "wan22";
        Assert.Equal(
            closeVariantSupported,
            registry.TryResolveModel(
                fixture.Model,
                out ResolvedVideoModel impostorResolution));
        if (closeVariantSupported)
        {
            Assert.Equal(
                WanArchitectureModule.OrdinaryImageToVideoProfileId,
                impostorResolution.ModelProfileId);
        }
        else
        {
            Assert.Null(impostorResolution);
        }
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Descriptor_publishes_the_common_executable_contract(string family)
    {
        using SwarmUiTestContext context = new();
        VideoArchitectureDescriptor descriptor = CreateFixture(family).Descriptor;

        // The registry's constructor invariants (non-empty entry modes, a positive frame grid, a
        // rule per boundary join) are pinned in ArchitectureFoundationTests; the only claim left
        // here is that every family can be entered by something other than uploaded footage.
        Assert.Contains(
            descriptor.EntryModes,
            mode => mode != ArchitectureEntryMode.InitVideo);
    }

    [Fact]
    public void Registry_accepts_all_real_architecture_modules()
    {
        VideoArchitectureRegistry registry = RealRegistry();

        Assert.Same(
            Ltx2ArchitectureModule.Instance,
            registry.GetModule(Ltx2ArchitectureModule.ArchitectureId));
        Assert.Same(
            MiniMaxArchitectureModule.Instance,
            registry.GetModule(MiniMaxArchitectureModule.ArchitectureId));
        Assert.Same(
            WanArchitectureModule.Instance,
            registry.GetModule(WanArchitectureModule.ArchitectureId));
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Minimal_generated_image_to_video_clip_compiles_the_shared_payload_contract(
        string family)
    {
        using SwarmUiTestContext context = new();
        FamilyFixture fixture = CreateFixture(family);
        ClipSpec clip = fixture.MinimalClip();
        TimelineSpec spec = new(512, 512, 24, false, [clip]);
        ArchitecturePlanningResult planning =
            ArchitecturePlanResolver.Resolve(spec, RealRegistry());
        ClipArchitectureAssignment assignment = planning.Clips[clip.Id];
        ArchitectureClipCompilation architectureCompilation =
            fixture.Module.ValidateAndCompileClip(
                clip,
                assignment.StageModels,
                new(512, 512, 24, ArchitectureEntryMode.ImageToVideo));
        KeyValuePair<int, IArchitectureStagePayload> compiledEntry =
            Assert.Single(architectureCompilation.StagePayloads);
        Assert.Equal(0, compiledEntry.Key);
        Assert.NotNull(architectureCompilation.Payload);
        Assert.Equal(
            fixture.Descriptor.Id,
            architectureCompilation.Payload.ArchitectureId);
        ClipPlan directlyCompiledClip = ClipPlanCompiler.Compile(
            clip,
            new(
                Width: 512,
                Height: 512,
                FramesPerSecond: 24,
                IsLastClip: true,
                IsMultiClip: false,
                TotalStageCount: 1,
                FirstStageOrdinal: 0,
                EntryMode: ArchitectureEntryMode.ImageToVideo,
                Architecture: assignment,
                ArchitectureCompilation: architectureCompilation));
        Assert.Same(
            compiledEntry.Value,
            Assert.Single(directlyCompiledClip.Stages).ArchitecturePayload);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            planning);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiledClip = Assert.Single(plan.Clips);
        StagePlan compiledStage = Assert.Single(compiledClip.Stages);
        Assert.Same(fixture.Descriptor, compiledClip.Architecture);
        Assert.NotNull(compiledClip.ArchitecturePayload);
        Assert.NotNull(compiledStage.ArchitecturePayload);
        Assert.Equal(compiledClip.Architecture.Id, compiledClip.ArchitecturePayload.ArchitectureId);
        Assert.Equal(compiledClip.Architecture.Id, compiledStage.ArchitecturePayload.ArchitectureId);
    }

    private static FamilyFixture CreateFixture(string family) => family switch
    {
        "ltx2" => CreateLtxFixture(),
        "minimax" => CreateMiniMaxFixture(),
        "wan22" => CreateWanFixture(),
        "wan22-5b" => CreateWan5bFixture(),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    private static FamilyFixture CreateLtxFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        return new(
            Ltx2ArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            new("ltx-2.3"),
            "lightricks-ltx-video-2");
    }

    private static FamilyFixture CreateWanFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        return new(
            WanArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            WanArchitectureModule.ImageToVideoProfileId,
            "wan-2_1-image2video-14b");
    }

    private static FamilyFixture CreateMiniMaxFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        return new(
            MiniMaxArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            MiniMaxArchitectureModule.ProfileId,
            "minimax-h3-refiner");
    }

    private static FamilyFixture CreateWan5bFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        return new(
            WanArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            WanArchitectureModule.Ti2v5bProfileId,
            $"{WanArchitectureModule.Ti2v5bModelClassId}/lora");
    }

    private static VideoArchitectureRegistry RealRegistry() =>
        new([
            Ltx2ArchitectureModule.Instance,
            MiniMaxArchitectureModule.Instance,
            WanArchitectureModule.Instance,
        ]);
}
