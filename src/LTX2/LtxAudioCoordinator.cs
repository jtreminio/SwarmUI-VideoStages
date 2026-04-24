using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

/// <summary>LTX2-only audio injection into the native workflow graph.</summary>
internal static class LtxAudioCoordinator
{
    internal static void TryInject(WorkflowGenerator g, AudioStageDetector.Detection detection)
    {
        if (detection is null || !g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return;
        }

        _ = new AudioInjector(g).TryInject(detection);
    }
}
