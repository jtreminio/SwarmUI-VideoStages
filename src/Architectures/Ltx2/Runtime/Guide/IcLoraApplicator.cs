using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Applies the already-compiled IC-LoRA stage plan. Parsing, stage scoping, drive-source
/// classification, control-mode classification, and guide-strength selection are deliberately
/// owned by <see cref="VideoExecutionPlanCompiler"/>, not this graph builder.
/// </summary>
internal sealed class IcLoraApplicator(WorkflowGenerator g)
{
    internal bool ApplyIcLoras(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipPlan clip,
        StagePlan stage,
        int? frameCount,
        WGNodeData stageInput)
    {
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        if (payload.IcLoras.IsDefaultOrEmpty
            || genInfo.Model is null
            || genInfo.VideoModel.ModelClass.CompatClass.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            return false;
        }

        List<ResolvedIcLoraModel> resolved = IcLoraModelResolver.Resolve(payload.IcLoras);
        if (resolved.Count == 0)
        {
            return false;
        }
        if (!Ltx2HostIntegration.IsAvailable(g.Features))
        {
            throw new SwarmUserErrorException(
                "VideoStages IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Ltx2HostIntegration.NodeUrl} "
                + "or use SwarmUI's LTXVideo feature installer.");
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        List<LTXICLoRALoaderModelOnlyNode> loaders = [];
        foreach (ResolvedIcLoraModel entry in resolved)
        {
            g.FinalLoadedModelList.Add(entry.Model);
            if (Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash)
            {
                entry.Model.GetOrGenerateTensorHashSha256();
            }
            LTXICLoRALoaderModelOnlyNode loader =
                bridge.AddNode(new LTXICLoRALoaderModelOnlyNode()).With(
                    LoraName: entry.Model.ToString(g.ModelFolderFormat),
                    StrengthModel: entry.Plan.ModelStrength);
            if (genInfo.Model.Path is JArray modelPath)
            {
                loader.ModelInput.ConnectFromPath(bridge, modelPath);
            }
            genInfo.Model = genInfo.Model.WithPath(loader.Model);
            loaders.Add(loader);
        }

        IcLoraVisualGuideResolver driveResolver = new(g);
        IcLoraControlSignalBuilder controlSignals = new(g);
        LtxIcLoraGuideApplicator guides = new(g);
        bool anyGuide = false;
        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedIcLoraModel entry = resolved[i];
            if (!driveResolver.TryResolve(
                    bridge,
                    clip,
                    stage,
                    entry.Plan,
                    stageInput,
                    out ResolvedIcLoraDrive drive))
            {
                continue;
            }

            double strength = entry.Plan.GuideStrength
                ?? ResolveControlNetGuideStrength(entry.Plan.MediaInput.ControlNetIndex);
            if (strength <= 0)
            {
                continue;
            }

            JArray controlImages =
                controlSignals.Apply(bridge, clip.ClipId, entry.Plan, drive.Images);
            if (drive.ControlNetIndex is null)
            {
                controlImages = driveResolver.ResizeToStageDimensions(
                    bridge,
                    controlImages,
                    genInfo);
            }
            JToken guideFrames = ResolveGuideFrameCount(
                genInfo,
                frameCount,
                clip.Audio.Length.Owner == AudioLengthOwner.ControlNet,
                drive.ControlNetIndex);
            guides.Apply(
                bridge,
                genInfo,
                entry.Plan,
                loaders[i],
                controlImages,
                strength,
                guideFrames,
                drive.IsStillImage);
            anyGuide = true;
        }
        return anyGuide;
    }

    private JToken ResolveGuideFrameCount(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int? stageClipFrames,
        bool clipLengthFromControlNet,
        int? controlNetIndex)
    {
        if (clipLengthFromControlNet
            && controlNetIndex is int index
            && new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(
                index,
                out JArray framesConnection))
        {
            return framesConnection;
        }
        int? frames = stageClipFrames ?? genInfo.Frames;
        return frames is int n ? new JValue(n) : null;
    }

    private double ResolveControlNetGuideStrength(int? controlNetIndex)
    {
        if (controlNetIndex is int index
            && index >= 0
            && index < T2IParamTypes.Controlnets.Length
            && g.UserInput.TryGet(
                T2IParamTypes.Controlnets[index].Strength,
                out double slotStrength))
        {
            return slotStrength;
        }
        return 1.0;
    }
}
