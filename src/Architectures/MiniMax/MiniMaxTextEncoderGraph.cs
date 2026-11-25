using System.IO;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Generated;

namespace VideoStages.Architectures.MiniMax;

internal static class MiniMaxTextEncoderGraph
{
    internal const string FeatureFlag = MiniMaxTextEncoders.FeatureFlag;
    internal const string NodeUrl = "https://github.com/nicolab28/ComfyUI-ClipProj";
    internal const string ProjectionFolder = "clip_projections";

    internal static void RegisterDependencies()
    {
        InstallableFeatures.RegisterInstallableFeature(new(
            "ComfyUI-ClipProj",
            FeatureFlag,
            NodeUrl,
            "NicoLab28",
            "This will install ComfyUI-ClipProj for MiniMax H3's 8B and 4B text "
                + "encoder options. SwarmUI will restart its managed ComfyUI backends.\n"
                + "Do you wish to install?"));
        ComfyUIBackendExtension.NodeToFeatureMap[ClipProjApplyNode.ClassType] = FeatureFlag;
        if (!ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Contains(ProjectionFolder))
        {
            ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Add(ProjectionFolder);
        }
    }

    internal static (string FileName, string Url, string Sha256) ProjectionFor(
        MiniMaxTextEncoder selection) => selection switch
    {
        MiniMaxTextEncoder.Qwen3Vl8B => (
            "mmh3-8b-ClipProj-v3-mlp.safetensors",
            "https://huggingface.co/NicoLab28/ClipProj-MiniMax-H3/resolve/main/"
                + "mmh3-8b-ClipProj-v3-mlp.safetensors",
            "9304d6002db92eb1ac58dac917864b3f8b96bf0d65fd889e6f20de18413a091c"),
        MiniMaxTextEncoder.Qwen3Vl4B => (
            "mmh3-4b-ClipProj-v3-mlp.safetensors",
            "https://huggingface.co/NicoLab28/ClipProj-MiniMax-H3/resolve/main/"
                + "mmh3-4b-ClipProj-v3-mlp.safetensors",
            "feef06ef3b9aede3b1f3331b71eebbc873e21a867d73bcf40ea2c0b007270693"),
        _ => throw Invariant.Failure(
            $"MiniMax H3 text encoder '{selection}' has no CLIP projection."),
    };

    internal static WGNodeData Apply(
        WorkflowGenerator generator,
        MiniMaxTextEncoder selection,
        WGNodeData textEncoder)
    {
        if (selection == MiniMaxTextEncoder.Default)
        {
            return textEncoder;
        }

        WorkflowGenerator.ModelLoadHelpers modelLoader = new(generator);
        (string encoderFile, string encoderHash) = selection switch
        {
            MiniMaxTextEncoder.Qwen3Vl8B => (
                "qwen3vl_8b_int8_convrot.safetensors",
                "8bfd0f6e12abf2d2d697ecc888e5e90b0d6741d6708f05799f53afa560452e8f"),
            MiniMaxTextEncoder.Qwen3Vl4B => (
                "qwen3vl_4b_fp8_scaled.safetensors",
                "54bd5144df0bbc25dd6ccadfcb826b521445a1b06ae5a42570bdd2974ca87094"),
            _ => throw Invariant.Failure(
                $"Unsupported MiniMax H3 text encoder '{selection}'."),
        };
        string encoderName = modelLoader.RequireClipModel(
            encoderFile,
            $"https://huggingface.co/Comfy-Org/Qwen3-VL/resolve/main/text_encoders/{encoderFile}",
            encoderHash,
            T2IParamTypes.QwenModel);
        (string projectionName, string projectionUrl, string projectionHash) =
            ProjectionFor(selection);
        string projectionDirectory = Utilities.CombinePathWithAbsolute(
            Program.ServerSettings.Paths.ActualModelRoot,
            ProjectionFolder);
        Directory.CreateDirectory(projectionDirectory);
        generator.DownloadModel(
            projectionName,
            Path.Join(projectionDirectory, projectionName),
            projectionUrl,
            projectionHash);

        if (textEncoder?.Path is not { Count: > 0 }
            || textEncoder.SourceNodeData.Item1 != CLIPLoaderNode.ClassType)
        {
            throw Invariant.Failure(
                "MiniMax H3's core text encoder did not come from CLIPLoader.");
        }

        // Core caches the native loader by input, so load the projected encoder separately.
        JArray originalLoadingClip = generator.LoadingClip;
        JArray videoClip;
        try
        {
            modelLoader.LoadClip(CLIPLoaderNode.TypeValues.Minimax, encoderName);
            videoClip = generator.LoadingClip;
        }
        finally
        {
            generator.LoadingClip = originalLoadingClip;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        ClipProjApplyNode projection = bridge.AddNode(
            new ClipProjApplyNode().With(Projection: projectionName));
        projection.ClipInput.ConnectFromPath(bridge, videoClip);
        return textEncoder.WithPath(projection.Clip.ToPath());
    }
}
