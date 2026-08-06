using System.Globalization;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;

namespace VideoStages.Authoring;

internal static class PromptSyntax
{
    public const string TagName = "videoclip";
    public const string CidMarker = "//cid=";
    public const int NoMatchCid = -1;

    private static readonly Regex WindowMarkerPattern = new(
        $@"<{TagName}:w\|(\d+)\|([0-9.]+)\|([0-9.]+){Regex.Escape(CidMarker)}-?\d+>",
        RegexOptions.Compiled);

    private static readonly Regex StageSectionMarkerPattern = new(
        $@"<{TagName}:s\|(\d+)\|(\d+){Regex.Escape(CidMarker)}-?\d+>",
        RegexOptions.Compiled);

    private static readonly Regex SectionMarkerPattern = new(
        $@"<{TagName}{Regex.Escape(CidMarker)}(-?\d+)>",
        RegexOptions.Compiled);

    public static bool TryParseWindow(string value, out double start, out double end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Contains(','))
        {
            return false;
        }
        int dash = value.IndexOf('-');
        if (dash <= 0 || dash >= value.Length - 1)
        {
            return false;
        }
        if (!double.TryParse(value[..dash].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out start)
            || !double.TryParse(value[(dash + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }
        if (!double.IsFinite(start) || !double.IsFinite(end))
        {
            return false;
        }
        start = Math.Max(0, start);
        return end > start;
    }

    public static string RestoreForMetadata(string prompt)
    {
        if (string.IsNullOrEmpty(prompt) || !prompt.Contains($"<{TagName}", StringComparison.OrdinalIgnoreCase))
        {
            return prompt;
        }
        prompt = WindowMarkerPattern.Replace(
            prompt,
            match => $"<{TagName}[{match.Groups[1].Value}]:{match.Groups[2].Value}-{match.Groups[3].Value}>");
        prompt = StageSectionMarkerPattern.Replace(
            prompt,
            match => $"<{TagName}[{match.Groups[1].Value},{match.Groups[2].Value}]>");
        return SectionMarkerPattern.Replace(prompt, match =>
        {
            int cid = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (cid == Constants.SectionID_VideoClip)
            {
                return $"<{TagName}>";
            }
            if (cid > Constants.SectionID_VideoClip && cid < Constants.SectionID_VideoClipUnmatched)
            {
                return $"<{TagName}[{cid - Constants.SectionID_VideoClip - 1}]>";
            }
            return match.Value;
        });
    }

    public static bool TryParseAuthoringTag(string tag, out string preData, out string value)
    {
        preData = null;
        string prefix = tag.BeforeAndAfter(':', out value);
        value = value?.Trim() ?? "";
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            (prefix, preData) = prefix.BeforeLast(']').BeforeAndAfter('[');
        }
        return prefix.Equals(TagName, StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeOverrideText(string value) =>
        (value ?? "").Replace("<", "").Replace(">", "").Replace("|", "").Replace(CidMarker, "")
            .Replace("\n", " ").Replace("\r", " ").Trim();

    public static string FormatWindowBound(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string FormatStageSectionMarker(int clip, int stage, int sectionId) =>
        $"<{TagName}:s|{clip}|{stage}{CidMarker}{sectionId}>";
}
