using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// Typed facade for the focused ControlNet capture services.
/// </summary>
internal sealed class ControlNetCapture(WorkflowGenerator g)
{
    private readonly ControlNetAudioCapture _audio = new(g);
    private readonly ControlNetFrameCountService _frameCounts = new(g);

    public void CaptureCoreVideoControlNetPreprocessors() =>
        new ControlNetCoreMediaCapture(g).Capture();

    internal bool TryGetCapturedControlNetAudio(int index, out WGNodeData audio) =>
        _audio.TryGetCapturedAudio(index, out audio);

    internal bool TryCreateCapturedControlImageFrameCount(
        int index,
        out JArray framesConnection) =>
        _frameCounts.TryCreate(index, out framesConnection);

    internal bool TryGetCapturedCoreControlImage(int index, out WGNodeData controlImage) =>
        ControlNetCoreMediaCapture.TryGetCapturedControlImage(g, index, out controlImage);

    internal static JArray PeelSingleFrameWrap(WorkflowBridge bridge, JArray imagePath) =>
        ControlNetCoreMediaCapture.PeelSingleFrameWrap(bridge, imagePath);

}
