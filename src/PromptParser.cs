using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Compatibility facade for VideoStages prompt-tag processing, prompt selection, and scoped LoRA selection.
/// Each operation delegates to a component that owns one prompt-processing responsibility.
/// </summary>
internal static class PromptParser
{
    public const string OverridesKey = "videostages_overrides";

    public sealed class VideoStageTagData
    {
        public readonly Dictionary<int, List<PromptWindowSpec>> ClipWindows = [];
        public readonly Dictionary<int, List<(string Field, string Value)>> ClipOverrides = [];
        public readonly Dictionary<(int Clip, int Stage), List<(string Field, string Value)>> StageOverrides = [];
        public readonly List<(string Field, string Value)> TopLevelOverrides = [];
    }

    public static string ProcessVideoClipTag(string data, T2IPromptHandling.PromptTagContext context) =>
        VideoStagePromptTagProcessor.ProcessVideoClipTag(data, context);

    public static string ProcessVideoStagesTag(string data, T2IPromptHandling.PromptTagContext context) =>
        VideoStagePromptTagProcessor.ProcessVideoStagesTag(data, context);

    public static string RestoreTagsForMetadata(string prompt) =>
        VideoClipPromptSyntax.RestoreForMetadata(prompt);

    public static VideoStageTagData ExtractTagData(string processedPrompt, T2IParamInput input)
    {
        VideoStageTagData data = new();
        VideoStageTagDataReader.Populate(processedPrompt, input, data);
        return data;
    }

    public static string ExtractPrompt(
        string prompt,
        string originalPrompt,
        int clipIndex,
        int? clipStageFlatId = null,
        int? clipStageIndexWithinClip = null) =>
        VideoClipPromptText.Extract(
            prompt,
            originalPrompt,
            clipIndex,
            clipStageFlatId,
            clipStageIndexWithinClip);

    public static string GetOriginalPrompt(T2IParamInput input, string paramId, string fallback)
    {
        if (input.ExtraMeta is not null
            && input.ExtraMeta.TryGetValue($"original_{paramId}", out object originalObj)
            && originalObj is string originalPrompt)
        {
            return originalPrompt;
        }

        return fallback ?? "";
    }

    public static ParamSnapshot ApplyLoraScope(
        T2IParamInput input,
        int clipIndex,
        int stageSectionId,
        NormalLoraTargetPolicy targetPolicy =
            NormalLoraTargetPolicy.ModelAndTextEncoder,
        int? targetSectionId = null) =>
        VideoScopedLoraSelector.Apply(
            input,
            clipIndex,
            stageSectionId,
            targetPolicy,
            targetSectionId);
}
