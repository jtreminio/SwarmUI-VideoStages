using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Applies LTX IC-LoRA guide nodes after drive media and control signals are resolved.</summary>
internal sealed class LtxIcLoraGuideApplicator(WorkflowGenerator g)
{
    private const int GuideFrameIdx = 0;
    private const string GuideCrop = "disabled";
    private const bool GuideUseTiledEncode = false;
    private const int GuideTileSize = 256;
    private const int GuideTileOverlap = 64;

    internal void Apply(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IcLoraPlan entry,
        LTXICLoRALoaderModelOnlyNode loader,
        JArray controlImages,
        double strength,
        JToken frameCount,
        bool stillImageDrive)
    {
        JArray guideImagePath = PrepareGuideFrames(
            bridge,
            controlImages,
            frameCount,
            stillImageDrive);
        ComfyNode guideNode;
        NodeInput<VaeType> vae;
        NodeInput<LatentType> latentInput;
        NodeInput<ImageType> image;
        NodeInput<FloatType> downscale;
        NodeOutput<LatentType> latentOut;
        if (entry.AttentionStrength < 1)
        {
            LTXAddVideoICLoRAGuideAdvancedNode advanced =
                bridge.AddNode(new LTXAddVideoICLoRAGuideAdvancedNode().With(
                    FrameIdx: GuideFrameIdx,
                    Strength: strength,
                    Crop: GuideCrop,
                    UseTiledEncode: GuideUseTiledEncode,
                    TileSize: GuideTileSize,
                    TileOverlap: GuideTileOverlap,
                    AttentionStrength: entry.AttentionStrength));
            guideNode = advanced;
            vae = advanced.Vae;
            latentInput = advanced.LatentInput;
            image = advanced.Image;
            downscale = advanced.LatentDownscaleFactor;
            latentOut = advanced.Latent;
        }
        else
        {
            LTXAddVideoICLoRAGuideNode basic =
                bridge.AddNode(new LTXAddVideoICLoRAGuideNode().With(
                    FrameIdx: GuideFrameIdx,
                    Strength: strength,
                    Crop: GuideCrop,
                    UseTiledEncode: GuideUseTiledEncode,
                    TileSize: GuideTileSize,
                    TileOverlap: GuideTileOverlap));
            guideNode = basic;
            vae = basic.Vae;
            latentInput = basic.LatentInput;
            image = basic.Image;
            downscale = basic.LatentDownscaleFactor;
            latentOut = basic.Latent;
        }

        IConditioningPairNode guide = (IConditioningPairNode)guideNode;
        guide.ConnectConditioning(bridge, genInfo);
        vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        latentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
        image.ConnectFromPath(bridge, guideImagePath);
        downscale.ConnectToUntyped(loader.LatentDownscaleFactor);
        genInfo.SetConditioning(guide);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            latentOut,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
    }

    private static JArray PrepareGuideFrames(
        WorkflowBridge bridge,
        JArray controlImagePath,
        JToken frames,
        bool stillImageDrive)
    {
        if (frames is null)
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }
        if (stillImageDrive)
        {
            RepeatImageBatchNode repeat = bridge.AddNode(new RepeatImageBatchNode());
            repeat.Image.TryConnectFromPath(
                bridge,
                new JArray(controlImagePath[0], controlImagePath[1]));
            repeat.Amount.SetFromToken(bridge, frames.DeepClone());
            return WorkflowBridge.ToPath(repeat.IMAGE);
        }

        JArray guideSource =
            ControlNetCapture.PeelSingleFrameWrap(bridge, controlImagePath);
        ImageFromBatchNode trim =
            bridge.AddNode(new ImageFromBatchNode().With(BatchIndex: 0));
        trim.Image.TryConnectFromPath(bridge, guideSource);
        trim.Length.SetFromToken(bridge, frames.DeepClone());
        return WorkflowBridge.ToPath(trim.IMAGE);
    }
}
