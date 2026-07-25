using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class VideoStagesPromptSection
{
    private static bool IsGroupEnabled(WorkflowGenerator g) =>
        g.UserInput.TryGetRaw(VideoStagesExtension.Enabled.Type, out _);

    public static string GetDataJson(WorkflowGenerator g) =>
        g.UserInput.Get(VideoStagesExtension.Data, "");

    public static bool IsActive(WorkflowGenerator g) =>
        IsGroupEnabled(g) && !string.IsNullOrWhiteSpace(GetDataJson(g));
}
