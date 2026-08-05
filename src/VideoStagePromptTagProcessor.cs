using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class VideoStagePromptTagProcessor
{
    private static readonly HashSet<string> TopLevelFields =
        new(["width", "height", "fps"], StringComparer.OrdinalIgnoreCase);

    public static string ProcessVideoClipTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        string preData = context.PreData?.Trim() ?? "";
        string[] tokens = preData.Length == 0 ? [] : [.. preData.Split(',').Select(token => token.Trim())];
        string value = string.IsNullOrEmpty(data) ? "" : context.Parse(data).Trim();
        bool lastIsField = tokens.Length > 0 && !int.TryParse(tokens[^1], out _);

        if (lastIsField)
        {
            if (value.Length == 0)
            {
                context.TrackWarning($"VideoStages: <videoclip[{preData}]> override has no value; ignoring.");
            }
            else
            {
                StashClipOrStageOverride(tokens, value, context);
            }
            return "";
        }
        if (value.Length > 0)
        {
            if (TryBuildWindowMarker(tokens, value, out string windowMarker))
            {
                return windowMarker;
            }
            context.TrackWarning($"VideoStages: <videoclip[{preData}]:{value}> is not a valid time window; ignoring.");
            return UnmatchedSectionMarker(context);
        }

        bool resolved = VideoClipSectionResolver.TryResolve(preData, context, out int sectionId);
        context.SectionID = sectionId;
        if (resolved && tokens.Length == 2
            && int.TryParse(tokens[0], out int clip) && clip >= 0
            && int.TryParse(tokens[1], out int stage) && stage >= 0)
        {
            return VideoClipPromptSyntax.FormatStageSectionMarker(clip, stage, sectionId);
        }
        return $"<{VideoClipPromptSyntax.TagName}{VideoClipPromptSyntax.CidMarker}{sectionId}>";
    }

    public static string ProcessVideoStagesTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        string preData = context.PreData?.Trim() ?? "";
        if (preData.Length == 0)
        {
            context.TrackWarning("VideoStages: the legacy <videostages> JSON prompt section is no longer supported; discarding it.");
            return UnmatchedSectionMarker(context);
        }
        string[] tokens = [.. preData.Split(',').Select(token => token.Trim())];
        if (tokens.Length != 1)
        {
            context.TrackWarning("VideoStages: <videostages> override tag has too many bracket groups; ignoring.");
            return "";
        }
        string field = tokens[0];
        if (!TopLevelFields.Contains(field))
        {
            context.TrackWarning($"VideoStages: unknown top-level override field '{field}'; ignoring.");
            return "";
        }
        string value = string.IsNullOrEmpty(data) ? "" : context.Parse(data).Trim();
        if (value.Length == 0)
        {
            context.TrackWarning($"VideoStages: top-level override '{field}' has no value; ignoring.");
            return "";
        }
        PromptParser.VideoStageTagData overrides = GetOverrides(context);
        overrides?.TopLevelOverrides.Add((
            VideoClipPromptSyntax.SanitizeOverrideText(field),
            VideoClipPromptSyntax.SanitizeOverrideText(value)));
        return "";
    }

    private static string UnmatchedSectionMarker(T2IPromptHandling.PromptTagContext context)
    {
        context.SectionID = Constants.SectionID_VideoClipUnmatched;
        return $"<{VideoClipPromptSyntax.TagName}{VideoClipPromptSyntax.CidMarker}{Constants.SectionID_VideoClipUnmatched}>";
    }

    private static bool TryBuildWindowMarker(string[] tokens, string value, out string marker)
    {
        marker = null;
        if (tokens.Length != 1
            || !int.TryParse(tokens[0], out int clip) || clip < 0
            || !VideoClipPromptSyntax.TryParseWindow(value, out double start, out double end))
        {
            return false;
        }
        marker = $"<{VideoClipPromptSyntax.TagName}:w|{clip}|{VideoClipPromptSyntax.FormatWindowBound(start)}"
            + $"|{VideoClipPromptSyntax.FormatWindowBound(end)}{VideoClipPromptSyntax.CidMarker}{Constants.SectionID_VideoClipUnmatched}>";
        return true;
    }

    private static void StashClipOrStageOverride(
        string[] tokens,
        string value,
        T2IPromptHandling.PromptTagContext context)
    {
        if (tokens.Length == 2)
        {
            if (!int.TryParse(tokens[0], out int clip))
            {
                context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override has a non-numeric clip index; ignoring.");
                return;
            }
            PromptParser.VideoStageTagData overrides = GetOverrides(context);
            overrides?.ClipOverrides.GetOrCreate(clip, () => []).Add((
                VideoClipPromptSyntax.SanitizeOverrideText(tokens[1]),
                VideoClipPromptSyntax.SanitizeOverrideText(value)));
            return;
        }
        if (tokens.Length == 3)
        {
            if (!int.TryParse(tokens[0], out int clip) || !int.TryParse(tokens[1], out int stage))
            {
                context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override requires numeric clip and stage indices; ignoring.");
                return;
            }
            PromptParser.VideoStageTagData overrides = GetOverrides(context);
            overrides?.StageOverrides.GetOrCreate((clip, stage), () => []).Add((
                VideoClipPromptSyntax.SanitizeOverrideText(tokens[2]),
                VideoClipPromptSyntax.SanitizeOverrideText(value)));
            return;
        }
        context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override has an unsupported bracket arity; ignoring.");
    }

    private static PromptParser.VideoStageTagData GetOverrides(
        T2IPromptHandling.PromptTagContext context)
    {
        if (context.Input?.ExtraMeta is null)
        {
            return null;
        }
        if (context.Input.ExtraMeta.GetValueOrDefault(PromptParser.OverridesKey)
            is PromptParser.VideoStageTagData overrides)
        {
            return overrides;
        }
        overrides = new();
        context.Input.ExtraMeta[PromptParser.OverridesKey] = overrides;
        return overrides;
    }
}
