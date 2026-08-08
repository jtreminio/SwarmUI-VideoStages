using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.WebAPI;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;

namespace VideoStages.Tests;

internal static class TestResolvedVideoModel
{
    internal static ResolvedVideoModel Create(
        string modelName,
        ModelProfileId modelProfileId,
        VideoArchitectureDescriptor architecture,
        string modelClassId = null,
        string compatibilityClassId = null,
        IReadOnlyList<FrameReferencePosition> referencePositions = null,
        bool lorasTargetTextEncoder = true) =>
        new(
            modelName,
            modelProfileId,
            architecture,
            modelClassId ?? modelName,
            compatibilityClassId ?? architecture.Id.Value,
            referencePositions ?? [],
            lorasTargetTextEncoder);
}

internal static class UnitTestStubs
{
    /// <summary>A generator carrying only what graph-shaping code reads off it: the workflow to
    /// edit and a param input to look parameters up in. Everything else stays at its default.</summary>
    public static WorkflowGenerator StubGenerator(JObject workflow) =>
        new()
        {
            UserInput = new T2IParamInput(null),
            Workflow = workflow
        };

    public static void EnsureComfySamplerSchedulerRegistered()
    {
        if (ComfyUIBackendExtension.SamplerParam is not null
            && ComfyUIBackendExtension.SchedulerParam is not null)
        {
            return;
        }

        ComfyUIBackendExtension.SamplerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Sampler",
            Description: "Stub sampler used by VideoStages unit tests.",
            Default: "euler",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["euler", "dpmpp_2m"]
        ));

        ComfyUIBackendExtension.SchedulerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Scheduler",
            Description: "Stub scheduler used by VideoStages unit tests.",
            Default: "normal",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["normal", "karras"]
        ));
    }

    public static void EnsureComfyVideoParamsRegistered()
    {
        if (ComfyUIBackendExtension.VideoPreviewType is null)
        {
            ComfyUIBackendExtension.VideoPreviewType = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Video Preview Type",
                Description: "Stub video preview type used by VideoStages unit tests.",
                Default: "animate",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupVideo,
                Toggleable: true,
                GetValues: (_) => ["animate"]
            ));
        }

        if (ComfyUIBackendExtension.VideoFrameInterpolationMethod is null)
        {
            ComfyUIBackendExtension.VideoFrameInterpolationMethod = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Video Frame Interpolation Method",
                Description: "Stub frame interpolation method used by VideoStages unit tests.",
                Default: "RIFE",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupAdvancedVideo,
                Toggleable: true,
                GetValues: (_) => ["RIFE", "FILM", "GIMM-VFI"]
            ));
        }

        if (ComfyUIBackendExtension.VideoFrameInterpolationMultiplier is null)
        {
            ComfyUIBackendExtension.VideoFrameInterpolationMultiplier = T2IParamTypes.Register<int>(new T2IParamType(
                Name: "Video Frame Interpolation Multiplier",
                Description: "Stub frame interpolation multiplier used by VideoStages unit tests.",
                Default: "1",
                IgnoreIf: "1",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupAdvancedVideo,
                Toggleable: true,
                Min: 1,
                Max: 10,
                Step: 1
            ));
        }

        if (ComfyUIBackendExtension.RefinerUpscaleMethod is null)
        {
            ComfyUIBackendExtension.RefinerUpscaleMethod = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Refiner Upscale Method",
                Description: "Stub upscale method used by VideoStages unit tests.",
                Default: "pixel-lanczos",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupRefiners,
                Toggleable: true,
                GetValues: (_) => ["pixel-lanczos", "model-unit-test-upscaler"]
            ));
        }

    }

    public static void EnsureComfyWorkflowParamsRegistered()
    {
        EnsureComfySamplerSchedulerRegistered();
        EnsureComfyVideoParamsRegistered();
        // Core dereferences ControlNetPreprocessorParams[i] unguarded once a ControlNet model is
        // set, so these must exist for any POST that configures one.
        EnsureComfyControlNetParamsRegistered();

        // Names must match ComfyUIBackendExtension's production registrations exactly:
        // T2IParamType.ID is CleanTypeName(Name), and that ID is the key a POST body uses.
        Ensure(ref ComfyUIBackendExtension.CustomWorkflowParam, "ComfyUI Custom Workflow", "");
        Ensure(ref ComfyUIBackendExtension.SetClipDevice, "Set CLIP Device", "cpu");
        Ensure(ref ComfyUIBackendExtension.SelfAttentionGuidanceScale, "Self-Attention Guidance Scale", "0.5");
        Ensure(ref ComfyUIBackendExtension.SelfAttentionGuidanceSigmaBlur, "Self-Attention Guidance Sigma Blur", "2");
        Ensure(ref ComfyUIBackendExtension.PerturbedAttentionGuidanceScale, "Perturbed-Attention Guidance Scale", "3");
        Ensure(ref ComfyUIBackendExtension.RescaleCFGMultiplier, "Rescale CFG Multiplier", "0.7");
        Ensure(ref ComfyUIBackendExtension.RenormCFG, "Renorm CFG", "0");
        Ensure(ref ComfyUIBackendExtension.UseCfgZeroStar, "Use CFG Zero Star", "false");
        Ensure(ref ComfyUIBackendExtension.UseTCFG, "Use TCFG", "false");
        Ensure(ref ComfyUIBackendExtension.NormalizedAttentionGuidanceScale, "Normalized Attention Guidance Scale", "0");
        Ensure(ref ComfyUIBackendExtension.NormalizedAttentionGuidanceAlpha, "Normalized Attention Guidance Alpha", "0.5");
        Ensure(ref ComfyUIBackendExtension.NormalizedAttentionGuidanceTau, "Normalized Attention Guidance Tau", "1.5");
        Ensure(ref ComfyUIBackendExtension.TeaCacheMode, "TeaCache Mode", "disabled");
        Ensure(ref ComfyUIBackendExtension.TeaCacheThreshold, "TeaCache Threshold", "0.25");
        Ensure(ref ComfyUIBackendExtension.TeaCacheStart, "TeaCache Start", "0");
        Ensure(ref ComfyUIBackendExtension.EasyCacheMode, "EasyCache Mode", "disabled");
        Ensure(ref ComfyUIBackendExtension.EasyCacheThreshold, "EasyCache Threshold", "0.2");
        Ensure(ref ComfyUIBackendExtension.EasyCacheStart, "EasyCache Start", "0.15");
        Ensure(ref ComfyUIBackendExtension.EasyCacheEnd, "EasyCache End", "0.95");
        Ensure(ref ComfyUIBackendExtension.AITemplateParam, "Enable AITemplate", "false");
        Ensure(ref ComfyUIBackendExtension.ShiftedLatentAverageInit, "Shifted Latent Average Init", "false");
        Ensure(ref ComfyUIBackendExtension.NunchakuCacheThreshold, "Nunchaku Cache Threshold", "0");
        Ensure(ref ComfyUIBackendExtension.PreferredDType, "Preferred DType", "automatic");
        Ensure(ref ComfyUIBackendExtension.Sam2PointCoordsPositive, "SAM2 Positive Points", "[]");
        Ensure(ref ComfyUIBackendExtension.Sam2PointCoordsNegative, "SAM2 Negative Points", "[]");
        Ensure(ref ComfyUIBackendExtension.Sam2BBox, "SAM2 BBox", "");
        Ensure(ref ComfyUIBackendExtension.PixelDecoderModel, "Pixel Decoder Model", "");
        // Core reads these through TryGet without a null guard.
        Ensure(ref ComfyUIBackendExtension.RefinerHyperTile, "Refiner HyperTile", "256");
        Ensure(ref ComfyUIBackendExtension.RefinerSamplerParam, "Refiner Sampler", "euler");
        Ensure(ref ComfyUIBackendExtension.RefinerSchedulerParam, "Refiner Scheduler", "normal");
        Ensure(ref ComfyUIBackendExtension.DebugRegionalPrompting, "Debug Regional Prompting", "false");
        Ensure(ref ComfyUIBackendExtension.GligenModel, "GLIGEN Model", "None");
        Ensure(ref ComfyUIBackendExtension.IPAdapterStart, "IP-Adapter Start", "0");
        Ensure(ref ComfyUIBackendExtension.IPAdapterEnd, "IP-Adapter End", "1");
        Ensure(ref ComfyUIBackendExtension.IPAdapterWeight, "IP-Adapter Weight", "1");
        Ensure(ref ComfyUIBackendExtension.IPAdapterWeightType, "IP-Adapter Weight Type", "standard");
        Ensure(ref ComfyUIBackendExtension.UseIPAdapterForRevision, "Use IP-Adapter", "None");
        Ensure(ref ComfyUIBackendExtension.UseStyleModel, "Use Style Model", "None");
        Ensure(ref ComfyUIBackendExtension.StyleModelApplyStart, "Style Model Apply Start", "0");
        Ensure(ref ComfyUIBackendExtension.StyleModelMergeStrength, "Style Model Merge Strength", "1");
        Ensure(ref ComfyUIBackendExtension.StyleModelMultiplyStrength, "Style Model Multiply Strength", "1");
        Ensure(ref ComfyUIBackendExtension.YoloModelInternal, "YOLO Model Internal", "");
    }

    private static void Ensure<T>(
        ref T2IRegisteredParam<T> parameter,
        string name,
        string defaultValue)
    {
        parameter ??= T2IParamTypes.Register<T>(new T2IParamType(
            Name: name,
            Description: "Parameter stub used by full SwarmUI workflow contract tests.",
            Default: defaultValue,
            Min: -100,
            Max: 100));
    }

    public static void EnsureComfyControlNetParamsRegistered()
    {
        if (ComfyUIBackendExtension.ControlNetPreprocessorParams[0] is not null
            && ComfyUIBackendExtension.ControlNetUnionTypeParams[0] is not null)
        {
            return;
        }

        ComfyUIBackendExtension.ControlNetPreprocessors["UnitTestPreprocessor"] = new JObject()
        {
            ["input"] = new JObject()
            {
                ["required"] = new JObject(),
                ["optional"] = new JObject()
            }
        };

        for (int i = 0; i < 3; i++)
        {
            string suffix = T2IParamTypes.Controlnets[i].NameSuffix;
            ComfyUIBackendExtension.ControlNetPreprocessorParams[i] ??= T2IParamTypes.Register<string>(new T2IParamType(
                Name: $"ControlNet{suffix} Preprocessor",
                Description: "Stub ControlNet preprocessor used by VideoStages unit tests.",
                Default: "None",
                FeatureFlag: "controlnet",
                Group: T2IParamTypes.Controlnets[i].Group,
                Toggleable: true,
                GetValues: (_) => ["None", "UnitTestPreprocessor"]
            ));
            ComfyUIBackendExtension.ControlNetUnionTypeParams[i] ??= T2IParamTypes.Register<string>(new T2IParamType(
                Name: $"ControlNet{suffix} Union Type",
                Description: "Stub ControlNet union type used by VideoStages unit tests.",
                Default: "auto",
                FeatureFlag: "controlnet",
                Group: T2IParamTypes.Controlnets[i].Group,
                Toggleable: true,
                GetValues: (_) => ["auto", "openpose"]
            ));
        }
    }
}

internal sealed class SwarmUiTestContext : IDisposable
{
    private readonly Dictionary<string, T2IModelHandler> _priorModelSets;
    private readonly bool _priorIncludeHash;
    private readonly List<WorkflowGenerator.WorkflowGenStep> _priorModelGenSteps;
    private readonly ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> _priorExtraModelProviders;
    private readonly ConcurrentDictionary<string, string> _priorClipModelsValid;
    private readonly ConcurrentDictionary<string, string> _priorVisionModelsValid;

    public SwarmUiTestContext(
        bool disableImageMetadataModelHash = true,
        bool resetExtraModelProviders = true,
        bool clearModelGenSteps = true)
    {
        // Snapshot by value: tests mutate this dictionary in place (installing support models,
        // replacing the "VAE" handler), and restoring the same reference would keep those edits.
        _priorModelSets = new Dictionary<string, T2IModelHandler>(Program.T2IModelSets);
        _priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        _priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        _priorExtraModelProviders = ModelsAPI.ExtraModelProviders;
        // Never-cleared statics: once one test installs a support model, every later test passes
        // without it, while the same test under --filter downloads. Green in suite, hangs alone.
        _priorClipModelsValid = WorkflowGenerator.ClipModelsValid;
        _priorVisionModelsValid = WorkflowGenerator.VisionModelsValid;
        WorkflowGenerator.ClipModelsValid = new(_priorClipModelsValid);
        WorkflowGenerator.VisionModelsValid = new(_priorVisionModelsValid);

        if (disableImageMetadataModelHash)
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
        }
        if (resetExtraModelProviders)
        {
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => [])
                ]);
        }
        if (clearModelGenSteps)
        {
            WorkflowGenerator.ModelGenSteps = [];
        }
    }

    public void Dispose()
    {
        WorkflowGenerator.VisionModelsValid = _priorVisionModelsValid;
        WorkflowGenerator.ClipModelsValid = _priorClipModelsValid;
        WorkflowGenerator.ModelGenSteps = _priorModelGenSteps;
        ModelsAPI.ExtraModelProviders = _priorExtraModelProviders;
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>(_priorModelSets);
        Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = _priorIncludeHash;
    }
}

internal sealed record TestModelBundle(
    T2IModel BaseModel,
    T2IModel VideoModel,
    T2IModel GemmaModel = null,
    T2IModel VisionModel = null);

internal sealed record MixedVideoModelBundle(
    T2IModel BaseModel,
    T2IModel LtxVideoModel,
    T2IModel WanVideoModel,
    T2IModel GemmaModel);

internal static class TestModelFactory
{
    public static TestModelBundle CreateBaseAndVideoModels()
    {
        return CreateBaseAndVideoModels(T2IModelClassSorter.CompatSvd, "unit-video", "Unit Video");
    }

    public static TestModelBundle CreateBaseAndUnsupportedVideoModels()
    {
        return CreateBaseAndVideoModels(
            new T2IModelCompatClass { ID = "unit-unsupported" },
            "unit-unsupported",
            "Unit Unsupported");
    }

    public static TestModelBundle CreateBaseAndLtxv2VideoModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatLtxv2,
            "unit-video-ltxv2",
            "Unit Video LTXV2");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = "lightricks-ltx-video-2-3",
            Name = "LTX Video 2.3",
        };
        return models;
    }

    public static TestModelBundle CreateBaseAndMiniMaxH3Models()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatMiniMaxH3,
            MiniMaxArchitectureModule.ModelClassId,
            "MiniMax H3");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            StandardWidth = 1344,
            StandardHeight = 768,
        };
        InstallMiniMaxSupportModels();
        return models;
    }

    public static void InstallMiniMaxSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        // EnsureModelSet, not a fresh handler: the per-architecture installers are chained by
        // cross-family fixtures, and replacing the set would drop the previous one's VAE stubs —
        // which does not fail, it falls through to a multi-gigabyte DownloadNow().Wait().
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors");
        Install("VAE", CommonModels.Known["minimax-h3-video-vae"].FileName);
        Install("VAE", CommonModels.Known["minimax-h3-audio-vae"].FileName);
    }

    /// <summary>
    /// The four support models core's LTX-2.3 loader branch resolves. Names must match
    /// <c>WorkflowGeneratorModelSupport</c> exactly — a miss does not fail, it downloads.
    /// </summary>
    public static void InstallLtx2SupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        // EnsureModelSet, not a fresh handler: the per-architecture installers are chained by
        // cross-family fixtures, and replacing the set would drop the previous one's VAE stubs —
        // which does not fail, it falls through to a multi-gigabyte DownloadNow().Wait().
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "gemma_3_12B_it.safetensors");
        Install("Clip", "LTX-2/ltx-2.3_text_projection_bf16.safetensors");
        Install("VAE", CommonModels.Known["ltx2-3-video-vae"].FileName);
        Install("VAE", CommonModels.Known["ltx2-3-audio-vae"].FileName);
    }

    public const string Hunyuan15ClipVisionFileName = "sigclip_vision_patch14_384.safetensors";

    /// <summary>
    /// Returns the CLIP-vision stub. Core's Hunyuan 1.5 image-to-video branch calls
    /// <c>RequireVisionModel</c>, which — unlike <c>RequireClipModel</c> — has no "already
    /// installed" fallback, so callers must also name it on the request.
    /// </summary>
    public static T2IModel InstallHunyuan15SupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("ClipVision");
        // EnsureModelSet, not a fresh handler: the per-architecture installers are chained by
        // cross-family fixtures, and replacing the set would drop the previous one's VAE stubs —
        // which does not fail, it falls through to a multi-gigabyte DownloadNow().Wait().
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "qwen_2.5_vl_7b_fp8_scaled.safetensors");
        Install("Clip", "byt5_small_glyphxl_fp16.safetensors");
        Install("VAE", CommonModels.Known["hunyuan-video-1_5-vae"].FileName);
        return TestStubModel.Install(
            Program.T2IModelSets["ClipVision"],
            Hunyuan15ClipVisionFileName);
    }

    public static void InstallMochiSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "t5xxl_enconly.safetensors");
        Install("VAE", CommonModels.Known["mochi-vae"].FileName);
    }

    private static void EnsureModelSet(string modelType)
    {
        if (!Program.T2IModelSets.ContainsKey(modelType))
        {
            Program.T2IModelSets[modelType] = new() { ModelType = modelType };
        }
    }

    public static TestModelBundle CreateBaseAndWan22ImageToVideoModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatWan21_14b,
            WanArchitectureModule.ImageToVideoModelClassId,
            "Wan 2.2 Image2Video 14B");
        // WAN resolves its CLIP and VAE support models through the host registries.
        return models with { VisionModel = InstallWanSupportModels() };
    }

    public static TestModelBundle CreateBaseAndWan22Ti2v5bModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatWan22_5b,
            WanArchitectureModule.Ti2v5bModelClassId,
            "Wan 2.2 Text/Image2Video 5B");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            StandardWidth = 960,
            StandardHeight = 960,
        };
        return models with { VisionModel = InstallWanSupportModels() };
    }

    public static MixedVideoModelBundle CreateBaseLtxv2AndWan22ImageToVideoModels()
    {
        TestModelBundle ltx = CreateBaseAndLtxv2VideoModels();
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel wan = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, "UnitTest_Wan22.safetensors"), "UnitTest_Wan22.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = WanArchitectureModule.ImageToVideoModelClassId,
                Name = "Wan 2.2 Image2Video 14B",
                CompatClass = T2IModelClassSorter.CompatWan21_14b,
                StandardWidth = 1024,
                StandardHeight = 576,
            },
        };
        handler.Models[wan.Name] = wan;
        InstallWanSupportModels();
        return new(ltx.BaseModel, ltx.VideoModel, wan, ltx.GemmaModel);
    }

    public static (
        T2IModel BaseModel,
        T2IModel LtxVideoModel,
        T2IModel MiniMaxVideoModel) CreateBaseLtxv2AndMiniMaxH3Models()
    {
        TestModelBundle ltx = CreateBaseAndLtxv2VideoModels();
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel miniMax = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, "UnitTest_MiniMaxH3.safetensors"), "UnitTest_MiniMaxH3.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = MiniMaxArchitectureModule.ModelClassId,
                Name = "MiniMax H3",
                CompatClass = T2IModelClassSorter.CompatMiniMaxH3,
                StandardWidth = 1344,
                StandardHeight = 768,
            },
        };
        handler.Models[miniMax.Name] = miniMax;
        InstallMiniMaxSupportModels();
        return (ltx.BaseModel, ltx.VideoModel, miniMax);
    }

    public const string WanClipVisionFileName = "clip_vision_h.safetensors";

    /// <summary>
    /// Returns the CLIP-vision stub. Models whose compat class is <c>wan-21-14b</c> or
    /// <c>wan-21-1_3b</c> — which includes Wan 2.2 I2V 14B, so the model class ID is not the
    /// discriminator — route core into
    /// <c>RequireVisionModel</c>, which — unlike <c>RequireClipModel</c> — has no "already
    /// installed" fallback and downloads 1.2 GB unless the request names the model itself, so
    /// callers reaching that branch must set <see cref="T2IParamTypes.ClipVisionModel"/>. Stubbing
    /// it under core's own file name keeps the generated <c>CLIPVisionLoader</c> unchanged.
    /// </summary>
    public static T2IModel InstallWanSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("ClipVision");
        // EnsureModelSet, not a fresh handler: the per-architecture installers are chained by
        // cross-family fixtures, and replacing the set would drop the previous one's VAE stubs —
        // which does not fail, it falls through to a multi-gigabyte DownloadNow().Wait().
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "umt5_xxl_fp8_e4m3fn_scaled.safetensors");
        Install("VAE", CommonModels.Known["wan21-vae"].FileName);
        Install("VAE", CommonModels.Known["wan22-vae"].FileName);
        return TestStubModel.Install(
            Program.T2IModelSets["ClipVision"],
            WanClipVisionFileName);
    }

    private static void Install(string modelType, string fileName)
    {
        TestStubModel.Install(Program.T2IModelSets[modelType], fileName);
    }

    private static TestModelBundle CreateBaseAndVideoModelsWithClass(T2IModelClass videoClass)
    {
        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler clipHandler = new() { ModelType = "Clip" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["Clip"] = clipHandler
        };

        T2IModelCompatClass baseCompat = new() { ID = "unit-base-compat", ShortCode = "UB" };
        T2IModelClass baseClass = new()
        {
            ID = "unit-base",
            Name = "Unit Base",
            CompatClass = baseCompat,
            StandardWidth = 1024,
            StandardHeight = 1024
        };

        T2IModel baseModel = new(sdHandler, TestStubModel.Folder(sdHandler), TestStubModel.File(sdHandler, "UnitTest_Base.safetensors"), "UnitTest_Base.safetensors")
        {
            ModelClass = baseClass
        };
        T2IModel videoModel = new(sdHandler, TestStubModel.Folder(sdHandler), TestStubModel.File(sdHandler, "UnitTest_Video.safetensors"), "UnitTest_Video.safetensors")
        {
            ModelClass = videoClass
        };
        T2IModel gemmaModel = new(clipHandler, TestStubModel.Folder(clipHandler), TestStubModel.File(clipHandler, "gemma_3_12B_it.safetensors"), "gemma_3_12B_it.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = "unit-gemma",
                Name = "Unit Gemma",
                CompatClass = baseCompat,
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };

        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[videoModel.Name] = videoModel;
        clipHandler.Models[gemmaModel.Name] = gemmaModel;
        return new TestModelBundle(baseModel, videoModel, gemmaModel);
    }

    private static TestModelBundle CreateBaseAndVideoModels(T2IModelCompatClass videoCompat, string videoClassId, string videoClassName)
    {
        T2IModelClass videoClass = new()
        {
            ID = videoClassId,
            Name = videoClassName,
            CompatClass = videoCompat,
            StandardWidth = 1024,
            StandardHeight = 576
        };
        return CreateBaseAndVideoModelsWithClass(videoClass);
    }
}
