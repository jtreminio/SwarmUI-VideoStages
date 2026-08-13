using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.Architectures.MiniMax;

internal static class MiniMaxAttentionWindowGraph
{
    internal const string FeatureFlag = "h3_window_attention";
    internal const string DenseLayerIndexes = "0,9,19,29,39,49";
    private const double MaximumSeconds = 20;

    internal static double NormalizeSeconds(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 0, MaximumSeconds) : 0;

    internal static void Apply(
        WorkflowGenerator generator,
        double seconds,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (seconds <= 0 || !generator.Features.Contains(FeatureFlag))
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        H3WindowAttentionPatchNode patch = bridge.AddNode(
            new H3WindowAttentionPatchNode());
        patch.Model.ConnectFromPath(bridge, genInfo.Model.Path);
        patch.WindowSeconds.Set(seconds);
        patch.DenseLayers.Set(DenseLayerIndexes);
        patch.Verbose.Set(true);
        genInfo.Model = genInfo.Model.WithPath(patch.MODEL.ToPath());
    }
}
