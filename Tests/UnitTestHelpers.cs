using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.WebAPI;
using VideoStages.Architectures.Abstractions;
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
        IReadOnlyList<string> referencePositions = null,
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
    public static void EnsureComfySamplerSchedulerRegistered()
    {
        if (ComfyUIBackendExtension.SamplerParam is not null
            && ComfyUIBackendExtension.SchedulerParam is not null)
        {
            return;
        }

        ComfyUIBackendExtension.SamplerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Sampler (UnitTest Stub)",
            Description: "Stub sampler used by VideoStages unit tests.",
            Default: "euler",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["euler", "dpmpp_2m"]
        ));

        ComfyUIBackendExtension.SchedulerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Scheduler (UnitTest Stub)",
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
                Name: "Video Preview Type (UnitTest Stub)",
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
                Name: "Video Frame Interpolation Method (UnitTest Stub)",
                Description: "Stub frame interpolation method used by VideoStages unit tests.",
                Default: "RIFE",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupAdvancedVideo,
                Toggleable: true,
                GetValues: (_) => ["RIFE"]
            ));
        }

        if (ComfyUIBackendExtension.VideoFrameInterpolationMultiplier is null)
        {
            ComfyUIBackendExtension.VideoFrameInterpolationMultiplier = T2IParamTypes.Register<int>(new T2IParamType(
                Name: "Video Frame Interpolation Multiplier (UnitTest Stub)",
                Description: "Stub frame interpolation multiplier used by VideoStages unit tests.",
                Default: "1",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupAdvancedVideo,
                Toggleable: true,
                Min: 1,
                Max: 8,
                Step: 1
            ));
        }

        if (ComfyUIBackendExtension.RefinerUpscaleMethod is null)
        {
            ComfyUIBackendExtension.RefinerUpscaleMethod = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Refiner Upscale Method (UnitTest Stub)",
                Description: "Stub upscale method used by VideoStages unit tests.",
                Default: "pixel-lanczos",
                FeatureFlag: "comfyui",
                Group: T2IParamTypes.GroupRefiners,
                Toggleable: true,
                GetValues: (_) => ["pixel-lanczos", "model-unit-test-upscaler"]
            ));
        }

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
                Name: $"ControlNet{suffix} Preprocessor (UnitTest Stub)",
                Description: "Stub ControlNet preprocessor used by VideoStages unit tests.",
                Default: "None",
                FeatureFlag: "controlnet",
                Group: T2IParamTypes.Controlnets[i].Group,
                Toggleable: true,
                GetValues: (_) => ["None", "UnitTestPreprocessor"]
            ));
            ComfyUIBackendExtension.ControlNetUnionTypeParams[i] ??= T2IParamTypes.Register<string>(new T2IParamType(
                Name: $"ControlNet{suffix} Union Type (UnitTest Stub)",
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

    public SwarmUiTestContext(
        bool disableImageMetadataModelHash = true,
        bool resetExtraModelProviders = true,
        bool clearModelGenSteps = true)
    {
        _priorModelSets = Program.T2IModelSets;
        _priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        _priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        _priorExtraModelProviders = ModelsAPI.ExtraModelProviders;

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
        WorkflowGenerator.ModelGenSteps = _priorModelGenSteps;
        ModelsAPI.ExtraModelProviders = _priorExtraModelProviders;
        Program.T2IModelSets = _priorModelSets;
        Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = _priorIncludeHash;
    }
}

internal sealed record TestModelBundle(T2IModel BaseModel, T2IModel VideoModel, T2IModel GemmaModel = null);

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

    public static TestModelBundle CreateBaseAndWan22ImageToVideoModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatWan21_14b,
            WanArchitectureModule.ImageToVideoModelClassId,
            "Wan 2.2 Image2Video 14B");
        // WAN resolves its CLIP and VAE support models through the host registries.
        InstallWanSupportModels();
        return models;
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
        InstallWanSupportModels();
        return models;
    }

    public static MixedVideoModelBundle CreateBaseLtxv2AndWan22ImageToVideoModels()
    {
        TestModelBundle ltx = CreateBaseAndLtxv2VideoModels();
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel wan = new(
            handler,
            "/tmp",
            "/tmp/UnitTest_Wan22.safetensors",
            "UnitTest_Wan22.safetensors")
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

    private static void InstallWanSupportModels()
    {
        Program.T2IModelSets["VAE"] = new() { ModelType = "VAE" };
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "umt5_xxl_fp8_e4m3fn_scaled.safetensors");
        Install("VAE", CommonModels.Known["wan21-vae"].FileName);
        Install("VAE", CommonModels.Known["wan22-vae"].FileName);
    }

    private static void Install(string modelType, string fileName)
    {
        T2IModelHandler handler = Program.T2IModelSets[modelType];
        handler.Models[fileName] = new(handler, "/tmp", $"/tmp/{fileName}", fileName);
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

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors")
        {
            ModelClass = baseClass
        };
        T2IModel videoModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Video.safetensors", "UnitTest_Video.safetensors")
        {
            ModelClass = videoClass
        };
        T2IModel gemmaModel = new(clipHandler, "/tmp", "/tmp/gemma_3_12B_it.safetensors", "gemma_3_12B_it.safetensors")
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
