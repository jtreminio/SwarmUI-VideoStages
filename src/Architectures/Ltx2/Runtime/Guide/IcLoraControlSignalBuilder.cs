using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Execution.Graph;

namespace VideoStages.Architectures.Ltx2.Runtime.Guide;

internal sealed class IcLoraControlSignalBuilder(WorkflowGenerator g)
{
    internal JArray Apply(
        WorkflowBridge bridge,
        int clipId,
        int rawStageIndex,
        IcLoraPlan entry,
        JArray driveImages)
    {
        if (entry.ControlMode == IcLoraControlMode.None)
        {
            return driveImages;
        }
        int? stageKey = entry.Drive.Source == IcLoraMediaSourceKind.Incoming
            ? rawStageIndex
            : null;
        string key = LtxRuntimeKeyScope.IcLoraControlSignal(
            clipId,
            entry.EntryIndex,
            stageKey);
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }

        JArray processed = entry.ControlMode switch
        {
            IcLoraControlMode.Canny => BuildCanny(bridge, driveImages),
            IcLoraControlMode.Depth => BuildDepth(bridge, driveImages),
            IcLoraControlMode.Normal => BuildNormal(bridge, driveImages),
            IcLoraControlMode.Unknown => driveImages,
            _ => driveImages,
        };
        VideoGraphHelpers.CachePath(g, key, processed);
        return processed;
    }

    private static JArray BuildCanny(WorkflowBridge bridge, JArray images)
    {
        CannyNode canny = bridge.AddNode(new CannyNode());
        canny.Image.TryConnectFromPath(bridge, images);
        return WorkflowBridge.ToPath(canny.IMAGE);
    }

    private static JArray BuildDepth(WorkflowBridge bridge, JArray images)
    {
        LoadDA3ModelNode model =
            bridge.AddNode(new LoadDA3ModelNode().With(
                ModelName: IcLoraWeights.Da3ModelFileName));
        DA3InferenceNode inference =
            bridge.AddNode(new DA3InferenceNode().With(Mode: "mono"));
        inference.Da3Model.ConnectToUntyped(model.DA3MODEL);
        inference.Image.TryConnectFromPath(bridge, images);
        DA3RenderNode render = new DA3RenderNode().With(Output: "depth");
        render.ExtraInputs = new JObject
        {
            ["output.normalization"] = "v2_style",
            ["output.apply_sky_clip"] = false,
        };
        bridge.AddNode(render);
        render.Da3Geometry.ConnectToUntyped(inference.Da3Geometry);
        return WorkflowBridge.ToPath(render.IMAGE);
    }

    private static JArray BuildNormal(WorkflowBridge bridge, JArray images)
    {
        LoadMoGeModelNode model =
            bridge.AddNode(new LoadMoGeModelNode().With(
                ModelName: IcLoraWeights.MoGeModelFileName));
        MoGeInferenceNode inference = bridge.AddNode(new MoGeInferenceNode());
        inference.MogeModel.ConnectToUntyped(model.MOGEMODEL);
        inference.Image.TryConnectFromPath(bridge, images);
        MoGeRenderNode render =
            bridge.AddNode(new MoGeRenderNode().With(Output: "normal_opengl"));
        render.MogeGeometry.ConnectToUntyped(inference.MogeGeometry);
        return WorkflowBridge.ToPath(render.IMAGE);
    }
}
