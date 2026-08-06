using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class IcLoraApplicator(WorkflowGenerator g)
{
    internal bool ApplyIcLoras(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipPlan clip,
        StagePlan stage,
        int? frameCount,
        WGNodeData stageInput,
        int incomingContinueHandleFrames,
        bool incomingMediaIncludesContinueHandle)
    {
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        if (payload.IcLoras.IsDefaultOrEmpty
            || genInfo.Model is null)
        {
            return false;
        }

        List<ResolvedIcLoraModel> resolved =
            IcLoraModelResolver.Resolve(payload.IcLoras, g.UserInput);
        if (resolved.Count == 0)
        {
            return false;
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
                ?? ResolveControlNetGuideStrength(entry.Plan.Drive.ControlNetIndex);
            if (strength <= 0)
            {
                continue;
            }

            JArray controlImages =
                controlSignals.Apply(
                    bridge,
                    clip.ClipId,
                    stage.ClipStageRawIndex,
                    entry.Plan,
                    drive.Images);
            if (drive.ControlNetIndex is null)
            {
                controlImages = driveResolver.ResizeToStageDimensions(
                    bridge,
                    controlImages,
                    genInfo,
                    clip.RequireLtx2Payload().ReferenceFraming);
            }
            JToken guideFrames = ResolveGuideFrameCount(
                genInfo,
                frameCount,
                clip.Audio.Length.Owner == AudioLengthOwner.ControlNet,
                drive.ControlNetIndex);
            int guideHandleFrames = entry.Plan.Drive.Source == IcLoraMediaSourceKind.Incoming
                && incomingMediaIncludesContinueHandle
                    ? 0
                    : incomingContinueHandleFrames;
            guides.Apply(
                bridge,
                genInfo,
                entry.Plan,
                loaders[i],
                controlImages,
                strength,
                guideFrames,
                drive.IsStillImage,
                guideHandleFrames);
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
            && new LtxControlNetMediaNormalizer(g).TryCreateFrameCount(
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
            && ControlNetCoreMediaCapture.IsValidIndex(index)
            && g.UserInput.TryGet(
                T2IParamTypes.Controlnets[index].Strength,
                out double slotStrength))
        {
            return slotStrength;
        }
        return 1.0;
    }
}
