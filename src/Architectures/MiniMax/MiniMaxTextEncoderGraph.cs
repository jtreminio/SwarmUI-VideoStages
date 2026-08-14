using System.IO;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
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
        (string encoderName, string loaderType) = selection switch
        {
            MiniMaxTextEncoder.Qwen3Vl8B => (
                modelLoader.GetQwen3vl_8bModel(),
                CLIPLoaderNode.TypeValues.Boogu),
            MiniMaxTextEncoder.Qwen3Vl4B => (
                modelLoader.GetQwen3vl_4bModel(),
                CLIPLoaderNode.TypeValues.Krea2),
            _ => throw Invariant.Failure(
                $"Unsupported MiniMax H3 text encoder '{selection}'."),
        };
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

        using WorkflowBridge bridge = BridgeSync.For(generator);
        if (textEncoder.Path is not JArray { Count: >= 1 } path
            || path[0]?.Value<string>() is not string loaderId
            || !bridge.Graph.Nodes.TryGetValue(loaderId, out ComfyNode node)
            || node is not CLIPLoaderNode loader)
        {
            throw Invariant.Failure(
                "MiniMax H3's core text encoder did not come from CLIPLoader.");
        }

        loader.ClipName.Set(encoderName);
        loader.Type.Set(loaderType);
        ClipProjApplyNode projection = bridge.AddNode(
            new ClipProjApplyNode().With(Projection: projectionName));
        projection.ClipInput.ConnectTo(loader.CLIP);
        return textEncoder.WithPath(projection.Clip.ToPath());
    }
}
