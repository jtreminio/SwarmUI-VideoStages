using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed record MiniMaxPiH3Adapter(LoraPlan Lora, T2IModel Model, bool DecodesPixels)
{
    internal const string InstallFeature = "piflow";
    internal const string RepositoryUrl = "https://github.com/Lakonik/ComfyUI-piFlow";
    internal const string LoaderFeature = LoadLakonLabAdapterNode.ClassType;
    internal const string DecodeFeature = DWDDecodeNode.ClassType;

    internal static void RegisterDependencies()
    {
        InstallableFeatures.RegisterInstallableFeature(new(
            "ComfyUI-piFlow",
            InstallFeature,
            RepositoryUrl,
            "Lakonik",
            "This will install ComfyUI-piFlow for MiniMax H3 pi-H3 adapters. "
                + "SwarmUI will restart its managed ComfyUI backends.\nDo you wish to install?"));
        ComfyUIBackendExtension.NodeToFeatureMap[LoadLakonLabAdapterNode.ClassType] = LoaderFeature;
        ComfyUIBackendExtension.NodeToFeatureMap[DWDDecodeNode.ClassType] = DecodeFeature;
    }

    internal static string RenderGeneratedTypeScript() => $$"""
        // Generated from MiniMaxPiH3Adapter.cs. Do not edit by hand.

        export const PI_H3_INSTALL_FEATURE = "{{InstallFeature}}";
        export const PI_H3_REPOSITORY_URL = "{{RepositoryUrl}}";
        export const PI_H3_REQUIRED_FEATURES = ["{{LoaderFeature}}", "{{DecodeFeature}}"];
        """ + "\n";

    internal static MiniMaxPiH3Adapter FromLora(LoraPlan lora)
    {
        string name = lora.Name.Replace('\\', '/');
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler)
            || handler.GetModel(name) is not T2IModel model
            || Path.GetExtension(model.RawFilePath) is not (".safetensors" or ".sft")
            || !File.Exists(model.RawFilePath))
        {
            return null;
        }
        JObject header;
        try
        {
            header = T2IModel.GetMetadataHeaderFrom(model.RawFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or SwarmReadableErrorException)
        {
            return null;
        }

        // pi-H3 ships both ComfyUI and Diffusers names for the full A/V output weights.
        bool hasAudioVideoWeights =
            (header.ContainsKey("audio_proj_out.weight") && header.ContainsKey("proj_out.weight"))
            || (header.ContainsKey("final_layer.audio_out.weight")
                && header.ContainsKey("final_layer.video_out.weight"));
        if (!hasAudioVideoWeights
            || header["__metadata__"] is not JObject metadata
            || !metadata.ContainsKey("policy_config"))
        {
            return null;
        }

        // Match LakonLabAdapterLoader's test for a DWD head.
        bool decodesPixels = header.Properties().Any(property =>
            property.Name.StartsWith("dwd_", StringComparison.Ordinal));
        return new(lora, model, decodesPixels);
    }

    internal JArray Apply(WorkflowGenerator g, JArray model)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LoadLakonLabAdapterNode adapter = bridge.AddNode(new LoadLakonLabAdapterNode().With(
            AdapterName: Model.ToString(g.ModelFolderFormat),
            AdapterStrength: Lora.ModelWeight));
        adapter.Model.ConnectFromPath(bridge, model);
        return adapter.MODEL.ToPath();
    }

    internal static WGNodeData DecodePixels(WorkflowGenerator g, WGNodeData sampled, WGNodeData vae, string id)
    {
        WGNodeData video = sampled.AsLatentImage(vae);
        using WorkflowBridge bridge = BridgeSync.For(g);
        DWDDecodeNode decode = id is null
            ? bridge.AddNode(new DWDDecodeNode())
            : bridge.AddNode(new DWDDecodeNode(), id);
        decode.Samples.ConnectFromPath(bridge, sampled.Path);
        return video.WithPath(decode.IMAGE.ToPath(), WGNodeData.DT_VIDEO, vae.Compat);
    }

    internal static WGNodeData VideoLatentBeforeDecode(
        WorkflowGenerator g, WGNodeData incoming, WGNodeData vae)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return bridge.NodeAt<DWDDecodeNode>(incoming.Path)?.Samples.Connection is INodeOutput sampled
            ? incoming.WithPath(sampled.ToPath(), WGNodeData.DT_LATENT_AUDIOVIDEO).AsLatentImage(vae)
            : null;
    }
}
