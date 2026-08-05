using System.Globalization;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class VideoStageTagDataReader
{
    public static void Populate(
        string processedPrompt,
        T2IParamInput input,
        PromptParser.VideoStageTagData data)
    {
        ExtractWindows(processedPrompt, data);
        CopyOverrides(input, data);
    }

    private static void ExtractWindows(string processedPrompt, PromptParser.VideoStageTagData data)
    {
        if (string.IsNullOrEmpty(processedPrompt)
            || !processedPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (PromptRegion.Part part in new PromptRegion(processedPrompt).Parts)
        {
            if (part.Prefix != VideoClipPromptSyntax.TagName
                || string.IsNullOrEmpty(part.DataText)
                || !part.DataText.StartsWith("w|"))
            {
                continue;
            }
            string[] tokens = part.DataText.Split('|');
            if (tokens.Length != 4
                || !int.TryParse(tokens[1], out int clip)
                || !double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
                || !double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
            {
                continue;
            }
            PromptWindowSpec window = new((part.Prompt ?? "").Trim(), start, end - start);
            data.ClipWindows.GetOrCreate(clip, () => []).Add(window);
        }
    }

    private static void CopyOverrides(T2IParamInput input, PromptParser.VideoStageTagData data)
    {
        if (input?.ExtraMeta is null
            || !input.ExtraMeta.TryGetValue(PromptParser.OverridesKey, out object raw)
            || raw is not PromptParser.VideoStageTagData overrides)
        {
            return;
        }
        data.TopLevelOverrides.AddRange(overrides.TopLevelOverrides);
        foreach ((int clip, List<(string Field, string Value)> values) in overrides.ClipOverrides)
        {
            data.ClipOverrides.GetOrCreate(clip, () => []).AddRange(values);
        }
        foreach (((int clip, int stage) target, List<(string Field, string Value)> values)
            in overrides.StageOverrides)
        {
            data.StageOverrides.GetOrCreate(target, () => []).AddRange(values);
        }
    }
}
