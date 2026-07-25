using System.Globalization;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Reads normalized prompt-window markers and scalar override metadata into one tag-data model.</summary>
internal static class VideoStageTagDataReader
{
    public static void Populate(
        string processedPrompt,
        T2IParamInput input,
        PromptParser.VideoStageTagData data)
    {
        ExtractWindows(processedPrompt, data);
        ReadOverrides(input, data);
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

    private static void ReadOverrides(T2IParamInput input, PromptParser.VideoStageTagData data)
    {
        if (input?.ExtraMeta is null
            || !input.ExtraMeta.TryGetValue(PromptParser.OverridesKey, out object raw)
            || raw is not List<string> overrides)
        {
            return;
        }
        foreach (string encoded in overrides)
        {
            if (string.IsNullOrEmpty(encoded))
            {
                continue;
            }
            switch (encoded.Split('|', 2)[0])
            {
                case "t":
                {
                    string[] tokens = encoded.Split('|', 3);
                    if (tokens.Length == 3)
                    {
                        data.TopLevelOverrides.Add((tokens[1], tokens[2]));
                    }
                    break;
                }
                case "c":
                {
                    string[] tokens = encoded.Split('|', 4);
                    if (tokens.Length == 4 && int.TryParse(tokens[1], out int clip))
                    {
                        data.ClipOverrides.GetOrCreate(clip, () => []).Add((tokens[2], tokens[3]));
                    }
                    break;
                }
                case "s":
                {
                    string[] tokens = encoded.Split('|', 5);
                    if (tokens.Length == 5
                        && int.TryParse(tokens[1], out int clip)
                        && int.TryParse(tokens[2], out int stage))
                    {
                        data.StageOverrides.GetOrCreate((clip, stage), () => []).Add((tokens[3], tokens[4]));
                    }
                    break;
                }
            }
        }
    }
}
