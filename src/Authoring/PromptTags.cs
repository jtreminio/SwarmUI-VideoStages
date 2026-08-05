using System.Globalization;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class PromptTags
{
    public const string OverridesKey = "videostages_overrides";

    public sealed class Directives
    {
        public readonly Dictionary<int, List<PromptWindowSpec>> ClipWindows = [];
        public readonly Dictionary<int, List<(string Field, string Value)>> ClipOverrides = [];
        public readonly Dictionary<(int Clip, int Stage), List<(string Field, string Value)>> StageOverrides = [];
        public readonly List<(string Field, string Value)> TopLevelOverrides = [];
    }

    private static readonly HashSet<string> TopLevelFields =
        new(["width", "height", "fps"], StringComparer.OrdinalIgnoreCase);

    public static Directives Read(string processedPrompt, T2IParamInput input)
    {
        Directives directives = new();
        ReadWindows(processedPrompt, directives);
        CopyOverrides(input, directives);
        return directives;
    }

    public static string ProcessVideoClip(string data, T2IPromptHandling.PromptTagContext context)
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

        bool resolved = TryResolveSection(preData, context, out int sectionId);
        context.SectionID = sectionId;
        if (resolved && tokens.Length == 2
            && int.TryParse(tokens[0], out int clip) && clip >= 0
            && int.TryParse(tokens[1], out int stage) && stage >= 0)
        {
            return PromptSyntax.FormatStageSectionMarker(clip, stage, sectionId);
        }
        return $"<{PromptSyntax.TagName}{PromptSyntax.CidMarker}{sectionId}>";
    }

    public static string ProcessVideoStages(string data, T2IPromptHandling.PromptTagContext context)
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
        Directives directives = GetDirectives(context);
        directives?.TopLevelOverrides.Add((
            PromptSyntax.SanitizeOverrideText(field),
            PromptSyntax.SanitizeOverrideText(value)));
        return "";
    }

    private static string UnmatchedSectionMarker(T2IPromptHandling.PromptTagContext context)
    {
        context.SectionID = Constants.SectionID_VideoClipUnmatched;
        return $"<{PromptSyntax.TagName}{PromptSyntax.CidMarker}{Constants.SectionID_VideoClipUnmatched}>";
    }

    private static bool TryBuildWindowMarker(string[] tokens, string value, out string marker)
    {
        marker = null;
        if (tokens.Length != 1
            || !int.TryParse(tokens[0], out int clip) || clip < 0
            || !PromptSyntax.TryParseWindow(value, out double start, out double end))
        {
            return false;
        }
        marker = $"<{PromptSyntax.TagName}:w|{clip}|{PromptSyntax.FormatWindowBound(start)}"
            + $"|{PromptSyntax.FormatWindowBound(end)}{PromptSyntax.CidMarker}{Constants.SectionID_VideoClipUnmatched}>";
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
            Directives directives = GetDirectives(context);
            directives?.ClipOverrides.GetOrCreate(clip, () => []).Add((
                PromptSyntax.SanitizeOverrideText(tokens[1]),
                PromptSyntax.SanitizeOverrideText(value)));
            return;
        }
        if (tokens.Length == 3)
        {
            if (!int.TryParse(tokens[0], out int clip) || !int.TryParse(tokens[1], out int stage))
            {
                context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override requires numeric clip and stage indices; ignoring.");
                return;
            }
            Directives directives = GetDirectives(context);
            directives?.StageOverrides.GetOrCreate((clip, stage), () => []).Add((
                PromptSyntax.SanitizeOverrideText(tokens[2]),
                PromptSyntax.SanitizeOverrideText(value)));
            return;
        }
        context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override has an unsupported bracket arity; ignoring.");
    }

    private static Directives GetDirectives(
        T2IPromptHandling.PromptTagContext context)
    {
        if (context.Input?.ExtraMeta is null)
        {
            return null;
        }
        if (context.Input.ExtraMeta.GetValueOrDefault(OverridesKey)
            is Directives directives)
        {
            return directives;
        }
        directives = new();
        context.Input.ExtraMeta[OverridesKey] = directives;
        return directives;
    }

    private static void ReadWindows(string processedPrompt, Directives directives)
    {
        if (string.IsNullOrEmpty(processedPrompt)
            || !processedPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (PromptRegion.Part part in new PromptRegion(processedPrompt).Parts)
        {
            if (part.Prefix != PromptSyntax.TagName
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
            directives.ClipWindows.GetOrCreate(clip, () => []).Add(window);
        }
    }

    private static void CopyOverrides(T2IParamInput input, Directives directives)
    {
        if (input?.ExtraMeta is null
            || !input.ExtraMeta.TryGetValue(OverridesKey, out object raw)
            || raw is not Directives stored)
        {
            return;
        }
        directives.TopLevelOverrides.AddRange(stored.TopLevelOverrides);
        foreach ((int clip, List<(string Field, string Value)> values) in stored.ClipOverrides)
        {
            directives.ClipOverrides.GetOrCreate(clip, () => []).AddRange(values);
        }
        foreach (((int clip, int stage) target, List<(string Field, string Value)> values)
            in stored.StageOverrides)
        {
            directives.StageOverrides.GetOrCreate(target, () => []).AddRange(values);
        }
    }

    private static bool TryResolveSection(
        string selector,
        T2IPromptHandling.PromptTagContext context,
        out int sectionId)
    {
        sectionId = Constants.SectionID_VideoClip;
        if (string.IsNullOrEmpty(selector))
        {
            return true;
        }

        string clipToken = selector.BeforeAndAfter(',', out string stageToken);
        clipToken = clipToken.Trim();
        stageToken = stageToken.Trim();
        if (string.IsNullOrEmpty(stageToken))
        {
            if (int.TryParse(selector, out int clipOnly) && clipOnly >= 0)
            {
                sectionId = VideoStagesExtension.SectionIdForClip(clipOnly);
                return true;
            }
            sectionId = Constants.SectionID_VideoClipUnmatched;
            return false;
        }

        if (!int.TryParse(clipToken, out int clipId) || clipId < 0
            || !int.TryParse(stageToken, out int clipStageIndex) || clipStageIndex < 0)
        {
            sectionId = Constants.SectionID_VideoClipUnmatched;
            return false;
        }

        if (TryResolveStage(context.Input, clipId, clipStageIndex, context, out int stageSection))
        {
            sectionId = stageSection;
            return true;
        }
        sectionId = Constants.SectionID_VideoClipUnmatched;
        return false;
    }

    private static bool TryResolveStage(
        T2IParamInput input,
        int clipId,
        int clipStageIndex,
        T2IPromptHandling.PromptTagContext context,
        out int sectionId)
    {
        sectionId = Constants.SectionID_VideoClip;
        if (input is null)
        {
            context.TrackWarning("VideoStages: videoclip[clip,stage] requires prompt input.");
            return false;
        }

        TimelineSpec spec;
        try
        {
            spec = RequestCaches.GetTimelineSpecForPromptParse(input);
        }
        catch (Exception ex)
        {
            context.TrackWarning(
                $"VideoStages: could not parse Video Stages JSON for videoclip[{clipId},{clipStageIndex}]: "
                + $"{ex.Message}");
            return false;
        }

        foreach (ClipSpec clip in spec.Clips)
        {
            if (clip.Id != clipId)
            {
                continue;
            }
            foreach (StageSpec stage in clip.Stages)
            {
                if (stage.ClipStageIndex == clipStageIndex)
                {
                    sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                    return true;
                }
            }
        }

        context.TrackWarning(
            "VideoStages: no active stage videoclip["
            + $"{clipId},{clipStageIndex}] in the current Video Stages configuration.");
        return false;
    }
}
