using System.Globalization;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class PromptParser
{
    private const string VideoClipTagName = "videoclip";
    private const string VideoClipCidMarker = "//cid=";
    private const int NoMatchCid = -1;
    public const string OverridesKey = "videostages_overrides";
    private static readonly HashSet<string> TopLevelFields =
        new(["width", "height", "fps"], StringComparer.OrdinalIgnoreCase);

    public static string ProcessVideoClipTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        string preData = context.PreData?.Trim() ?? "";
        string[] tokens = preData.Length == 0 ? [] : [.. preData.Split(',').Select(t => t.Trim())];
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

        TryResolveVideoclipSectionId(preData, context, out int sectionId);
        context.SectionID = sectionId;
        return $"<{VideoClipTagName}{VideoClipCidMarker}{sectionId}>";
    }

    public static string ProcessVideoStagesTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        string preData = context.PreData?.Trim() ?? "";
        if (preData.Length == 0)
        {
            context.TrackWarning("VideoStages: the legacy <videostages> JSON prompt section is no longer supported; discarding it.");
            return UnmatchedSectionMarker(context);
        }
        string[] tokens = [.. preData.Split(',').Select(t => t.Trim())];
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
        StashOverride(context, $"t|{Sanitize(field)}|{Sanitize(value)}");
        return "";
    }

    private static string UnmatchedSectionMarker(T2IPromptHandling.PromptTagContext context)
    {
        context.SectionID = Constants.SectionID_VideoClipUnmatched;
        return $"<{VideoClipTagName}{VideoClipCidMarker}{Constants.SectionID_VideoClipUnmatched}>";
    }

    private static bool TryBuildWindowMarker(string[] tokens, string value, out string marker)
    {
        marker = null;
        if (tokens.Length != 1
            || !int.TryParse(tokens[0], out int clip) || clip < 0
            || !TryParseWindow(value, out double start, out double end))
        {
            return false;
        }
        marker = $"<{VideoClipTagName}:w|{clip}|{Fmt(start)}|{Fmt(end)}{VideoClipCidMarker}{Constants.SectionID_VideoClipUnmatched}>";
        return true;
    }

    private static void StashClipOrStageOverride(string[] tokens, string value, T2IPromptHandling.PromptTagContext context)
    {
        if (tokens.Length == 2)
        {
            if (!int.TryParse(tokens[0], out int clip))
            {
                context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override has a non-numeric clip index; ignoring.");
                return;
            }
            StashOverride(context, $"c|{clip}|{Sanitize(tokens[1])}|{Sanitize(value)}");
            return;
        }
        if (tokens.Length == 3)
        {
            if (!int.TryParse(tokens[0], out int clip) || !int.TryParse(tokens[1], out int stage))
            {
                context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override requires numeric clip and stage indices; ignoring.");
                return;
            }
            StashOverride(context, $"s|{clip}|{stage}|{Sanitize(tokens[2])}|{Sanitize(value)}");
            return;
        }
        context.TrackWarning($"VideoStages: <videoclip[{string.Join(',', tokens)}]> override has an unsupported bracket arity; ignoring.");
    }

    private static void StashOverride(T2IPromptHandling.PromptTagContext context, string encoded)
    {
        if (context.Input?.ExtraMeta is null)
        {
            return;
        }
        List<string> overrides = context.Input.ExtraMeta.GetOrCreate(OverridesKey, () => new List<string>()) as List<string>;
        overrides.Add(encoded);
    }

    private static bool TryParseWindow(string value, out double start, out double end)
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

    private static readonly Regex WindowMarkerPattern = new(
        $@"<{VideoClipTagName}:w\|(\d+)\|([0-9.]+)\|([0-9.]+){Regex.Escape(VideoClipCidMarker)}-?\d+>",
        RegexOptions.Compiled);

    private static readonly Regex SectionMarkerPattern = new(
        $@"<{VideoClipTagName}{Regex.Escape(VideoClipCidMarker)}(-?\d+)>",
        RegexOptions.Compiled);

    /// <summary>Restores processed <c>videoclip</c> markers back to the user-facing tag syntax, for metadata.
    /// Windows (<c>&lt;videoclip:w|0|0.5|4//cid=N&gt;</c>) become <c>&lt;videoclip[0]:0.5-4&gt;</c>; clip section
    /// markers become <c>&lt;videoclip&gt;</c>/<c>&lt;videoclip[N]&gt;</c>. Flattened clip-stage and unmatched
    /// section ids are not reversible without the spec and are left as-is.</summary>
    public static string RestoreTagsForMetadata(string prompt)
    {
        if (string.IsNullOrEmpty(prompt) || !prompt.Contains($"<{VideoClipTagName}", StringComparison.OrdinalIgnoreCase))
        {
            return prompt;
        }
        prompt = WindowMarkerPattern.Replace(
            prompt,
            m => $"<{VideoClipTagName}[{m.Groups[1].Value}]:{m.Groups[2].Value}-{m.Groups[3].Value}>");
        prompt = SectionMarkerPattern.Replace(prompt, m =>
        {
            int cid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (cid == Constants.SectionID_VideoClip)
            {
                return $"<{VideoClipTagName}>";
            }
            if (cid > Constants.SectionID_VideoClip && cid < Constants.SectionID_VideoClipUnmatched)
            {
                return $"<{VideoClipTagName}[{cid - Constants.SectionID_VideoClip - 1}]>";
            }
            return m.Value;
        });
        return prompt;
    }

    // '|' is stripped because it is the ExtraMeta override-stash delimiter (see
    // StashOverride/ReadOverrides): a pipe inside a FIELD token would shift the
    // bounded Split and silently decode as a different field.
    private static string Sanitize(string value) =>
        (value ?? "").Replace("<", "").Replace(">", "").Replace("|", "").Replace(VideoClipCidMarker, "").Replace("\n", " ").Replace("\r", " ").Trim();

    private static string Fmt(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public sealed class VideoStageTagData
    {
        public readonly Dictionary<int, List<PromptWindowSpec>> ClipWindows = [];
        public readonly Dictionary<int, List<(string Field, string Value)>> ClipOverrides = [];
        public readonly Dictionary<(int Clip, int Stage), List<(string Field, string Value)>> StageOverrides = [];
        public readonly List<(string Field, string Value)> TopLevelOverrides = [];
    }

    public static VideoStageTagData ExtractTagData(string processedPrompt, T2IParamInput input)
    {
        VideoStageTagData data = new();
        ExtractWindows(processedPrompt, data);
        ReadOverrides(input, data);
        return data;
    }

    private static void ExtractWindows(string processedPrompt, VideoStageTagData data)
    {
        if (string.IsNullOrEmpty(processedPrompt)
            || !processedPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (PromptRegion.Part part in new PromptRegion(processedPrompt).Parts)
        {
            if (part.Prefix != VideoClipTagName || string.IsNullOrEmpty(part.DataText) || !part.DataText.StartsWith("w|"))
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

    private static void ReadOverrides(T2IParamInput input, VideoStageTagData data)
    {
        if (input?.ExtraMeta is null
            || !input.ExtraMeta.TryGetValue(OverridesKey, out object raw)
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
                    if (tokens.Length == 5 && int.TryParse(tokens[1], out int clip) && int.TryParse(tokens[2], out int stage))
                    {
                        data.StageOverrides.GetOrCreate((clip, stage), () => []).Add((tokens[3], tokens[4]));
                    }
                    break;
                }
            }
        }
    }

    public static bool TryResolveVideoclipSectionId(
        string preDataTrimmed,
        T2IPromptHandling.PromptTagContext context,
        out int sectionId)
    {
        sectionId = Constants.SectionID_VideoClip;
        if (string.IsNullOrEmpty(preDataTrimmed))
        {
            return true;
        }

        string clipToken = preDataTrimmed.BeforeAndAfter(',', out string stageToken);
        clipToken = clipToken.Trim();
        stageToken = stageToken.Trim();

        if (string.IsNullOrEmpty(stageToken))
        {
            if (int.TryParse(preDataTrimmed, out int clipOnly) && clipOnly >= 0)
            {
                sectionId = VideoStagesExtension.SectionIdForClip(clipOnly);
                return true;
            }

            sectionId = Constants.SectionID_VideoClipUnmatched;
            return false;
        }

        if (!int.TryParse(clipToken, out int clipId) || clipId < 0
            || !int.TryParse(stageToken, out int clipStageIdx) || clipStageIdx < 0)
        {
            sectionId = Constants.SectionID_VideoClipUnmatched;
            return false;
        }

        if (TryFlattenedStageSectionId(context.Input, clipId, clipStageIdx, context, out int stageSection))
        {
            sectionId = stageSection;
            return true;
        }

        sectionId = Constants.SectionID_VideoClipUnmatched;
        return false;
    }

    public static bool HasAnyVideoClipSectionForClip(string prompt, int clipIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt)
            || !prompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        string canonical = CanonicalizeVideoclipBrackets(
            prompt, clipIndex, clipStageIndexWithinClip: null,
            globalCid, clipCid, NoMatchCid);

        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix == VideoClipTagName
                && (part.ContextID == globalCid || part.ContextID == clipCid))
            {
                return true;
            }
        }
        return false;
    }

    public static string ExtractPrompt(
        string prompt,
        string originalPrompt,
        int clipIndex,
        int? clipStageFlatId = null,
        int? clipStageIndexWithinClip = null)
    {
        string extracted = ExtractPromptWithoutReferences(prompt, clipIndex, clipStageFlatId, clipStageIndexWithinClip);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        if (!ShouldFallbackForTagOnlyVideoClipSection(prompt, originalPrompt, clipIndex))
        {
            return extracted.Trim();
        }

        for (int prevClip = clipIndex - 1; prevClip >= 0; prevClip--)
        {
            string prevPrompt = ExtractPrompt(prompt, originalPrompt, prevClip);
            if (!string.IsNullOrWhiteSpace(prevPrompt))
            {
                return prevPrompt;
            }
        }

        string videoText = GetVideoPromptText(prompt);
        if (!string.IsNullOrWhiteSpace(videoText))
        {
            return videoText;
        }

        return GetGlobalPromptText(prompt);
    }

    public static string ExtractPromptWithoutReferences(
        string prompt,
        int clipIndex,
        int? clipStageFlatId = null,
        int? clipStageIndexWithinClip = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }
        if (!prompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return prompt.Trim();
        }

        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        int stageCid = clipStageFlatId.HasValue
            ? VideoStagesExtension.SectionIdForStage(clipStageFlatId.Value)
            : NoMatchCid;

        string canonical = CanonicalizeVideoclipBrackets(
            prompt, clipIndex, clipStageIndexWithinClip,
            globalCid, clipCid, stageCid);

        StringBuilder result = new();
        bool sawRelevant = false;
        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix != VideoClipTagName)
            {
                continue;
            }
            int cid = part.ContextID;
            if (cid == globalCid || cid == clipCid || (stageCid != NoMatchCid && cid == stageCid))
            {
                sawRelevant = true;
                AppendWithBoundarySpace(result, part.Prompt);
            }
        }

        if (sawRelevant)
        {
            return result.ToString().Trim();
        }
        return RemoveAllVideoClipSections(canonical);
    }

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

    public static ParamSnapshot ApplyLoraScope(T2IParamInput input, int clipIndex, int stageSectionId)
    {
        if (!input.TryGet(T2IParamTypes.Loras, out List<string> loras)
            || loras is null
            || loras.Count == 0)
        {
            return null;
        }

        List<string> confinements = input.Get(T2IParamTypes.LoraSectionConfinement) ?? [];
        if (confinements.Count == 0)
        {
            return null;
        }

        List<string> weights = input.Get(T2IParamTypes.LoraWeights) ?? [];
        List<string> tencWeights = input.Get(T2IParamTypes.LoraTencWeights) ?? [];
        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        List<int> selectedIndices = [];

        for (int i = 0; i < loras.Count; i++)
        {
            if (i >= confinements.Count || !int.TryParse(confinements[i], out int confinementId))
            {
                continue;
            }
            if (confinementId == globalCid || confinementId == clipCid || confinementId == stageSectionId)
            {
                selectedIndices.Add(i);
            }
        }

        if (selectedIndices.Count == 0)
        {
            return null;
        }

        List<(string, string, string)> rows = [];
        foreach (int index in selectedIndices)
        {
            string weight = index < weights.Count ? weights[index] : "1";
            string tencWeight = index < tencWeights.Count ? tencWeights[index] : weight;
            rows.Add((loras[index], weight, tencWeight));
        }

        return LoraParams.AppendVideoScoped(
            input,
            [.. loras],
            [.. weights],
            [.. tencWeights],
            [.. confinements],
            rows);
    }

    /// <summary>
    /// One piece of a prompt split on '&lt;': the leading pre-tag text (<see cref="IsLeadingText"/>),
    /// or a tag candidate. <see cref="HasTag"/> is false when the piece has no closing '&gt;'.
    /// </summary>
    private readonly record struct PromptTagPiece(string Piece, bool IsLeadingText, int TagEnd)
    {
        public bool HasTag => TagEnd >= 0;
        public string Tag => Piece[..TagEnd];
        public string Content => Piece[(TagEnd + 1)..];
    }

    /// <summary>
    /// The shared tag-scanner skeleton of <see cref="CanonicalizeVideoclipBrackets"/> and
    /// <see cref="RemoveAllVideoClipSections"/>: splits on '&lt;', yields the leading text as-is, skips
    /// empty pieces (consecutive '&lt;'), and marks each remaining piece with its '&gt;' position.
    /// Non-leading pieces re-serialize as <c>'&lt;' + Piece</c>.
    /// </summary>
    private static IEnumerable<PromptTagPiece> ScanTagPieces(string prompt)
    {
        string[] pieces = prompt.Split('<');
        yield return new PromptTagPiece(pieces[0], IsLeadingText: true, TagEnd: -1);
        foreach (string piece in pieces.Skip(1))
        {
            if (!string.IsNullOrEmpty(piece))
            {
                yield return new PromptTagPiece(piece, IsLeadingText: false, piece.IndexOf('>'));
            }
        }
    }

    private static string CanonicalizeVideoclipBrackets(
        string prompt,
        int clipIndex,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipCid,
        int stageCid)
    {
        StringBuilder result = new(prompt.Length + 16);
        foreach (PromptTagPiece piece in ScanTagPieces(prompt))
        {
            if (piece.IsLeadingText)
            {
                result.Append(piece.Piece);
                continue;
            }
            if (!piece.HasTag)
            {
                result.Append('<').Append(piece.Piece);
                continue;
            }
            string tag = piece.Tag;

            if (tag.Contains(VideoClipCidMarker, StringComparison.OrdinalIgnoreCase)
                || !TryParseVideoclipTag(tag, out string preData, out string value))
            {
                result.Append('<').Append(piece.Piece);
                continue;
            }

            int cid = string.IsNullOrEmpty(value)
                ? ResolveBracketCid(preData, clipIndex, clipStageIndexWithinClip, globalCid, clipCid, stageCid)
                : NoMatchCid;
            result.Append('<').Append(VideoClipTagName).Append(VideoClipCidMarker).Append(cid)
                  .Append('>').Append(piece.Content);
        }
        return result.ToString();
    }

    private static bool TryParseVideoclipTag(string tag, out string preData, out string value)
    {
        preData = null;
        string prefix = tag.BeforeAndAfter(':', out value);
        value = value?.Trim() ?? "";
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            (prefix, preData) = prefix.BeforeLast(']').BeforeAndAfter('[');
        }
        return prefix.Equals(VideoClipTagName, StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveBracketCid(
        string preData,
        int clipIndex,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipCid,
        int stageCid)
    {
        if (string.IsNullOrWhiteSpace(preData))
        {
            return globalCid;
        }
        string preTrimmed = preData.Trim();

        if (preTrimmed.Contains(','))
        {
            string first = preTrimmed.BeforeAndAfter(',', out string second);
            if (!int.TryParse(first.Trim(), out int tagClip)
                || !int.TryParse(second.Trim(), out int tagStage))
            {
                return NoMatchCid;
            }
            if (tagClip != clipIndex)
            {
                return NoMatchCid;
            }
            if (!clipStageIndexWithinClip.HasValue)
            {
                return clipCid;
            }
            return tagStage == clipStageIndexWithinClip.Value ? stageCid : NoMatchCid;
        }

        return int.TryParse(preTrimmed, out int singleClip) && singleClip == clipIndex
            ? clipCid
            : NoMatchCid;
    }

    private static bool ShouldFallbackForTagOnlyVideoClipSection(
        string parsedPrompt,
        string originalPrompt,
        int clipIndex)
    {
        if (clipIndex < 0)
        {
            return false;
        }

        string sourcePrompt = string.IsNullOrWhiteSpace(originalPrompt) ? parsedPrompt : originalPrompt;
        if (!HasAnyVideoClipSectionForClip(sourcePrompt, clipIndex))
        {
            return false;
        }

        string sourceSection = ExtractPromptWithoutReferences(sourcePrompt, clipIndex, null, null);
        if (string.IsNullOrWhiteSpace(sourceSection) || !sourceSection.Contains('<'))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(StripPromptTags(sourceSection));
    }

    private static string StripPromptTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('<'))
        {
            return text ?? "";
        }

        StringBuilder cleaned = new(text.Length);
        bool inTag = false;
        foreach (char c in text)
        {
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                }
                else
                {
                    cleaned.Append(c);
                }
            }
            else if (c == '>')
            {
                inTag = false;
            }
        }
        return cleaned.ToString();
    }

    private static string GetGlobalPromptText(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }
        return new PromptRegion(prompt).GlobalPrompt.Trim();
    }

    private static string GetVideoPromptText(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }
        return new PromptRegion(prompt).VideoPrompt.Trim();
    }

    private static string RemoveAllVideoClipSections(string canonicalPrompt)
    {
        if (!canonicalPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPrompt.Trim();
        }

        StringBuilder result = new();
        bool inVideoclip = false;
        foreach (PromptTagPiece piece in ScanTagPieces(canonicalPrompt))
        {
            if (piece.IsLeadingText)
            {
                result.Append(piece.Piece);
                continue;
            }
            if (!piece.HasTag)
            {
                if (!inVideoclip)
                {
                    result.Append('<').Append(piece.Piece);
                }
                continue;
            }
            string prefix = ExtractTagPrefixLower(piece.Tag);
            if (prefix == VideoClipTagName)
            {
                inVideoclip = true;
                continue;
            }
            if (inVideoclip && !IsSectionStartingTag(prefix))
            {
                continue;
            }
            inVideoclip = false;
            result.Append('<').Append(piece.Piece);
        }
        return result.ToString().Trim();
    }

    private static string ExtractTagPrefixLower(string tag)
    {
        string prefix = tag.BeforeAndAfter(':', out _);
        int slash = prefix.IndexOf('/');
        if (slash != -1)
        {
            prefix = prefix[..slash];
        }
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            prefix = prefix[..prefix.LastIndexOf('[')];
        }
        return prefix.ToLowerInvariant();
    }

    private static readonly HashSet<string> BuiltInSectionStarters = [
        "base", "refiner", "video", "videoswap", "edit",
        "region", "segment", "object", "clear", "extend"
    ];

    private static bool IsSectionStartingTag(string prefixLower)
    {
        if (BuiltInSectionStarters.Contains(prefixLower))
        {
            return true;
        }
        foreach (string p in PromptRegion.CustomPartPrefixes)
        {
            if (StringUtils.Equals(p, prefixLower))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryFlattenedStageSectionId(
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

        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/"
        };

        VideoStagesSpec spec;
        try
        {
            spec = generator.GetVideoStagesSpec();
        }
        catch (SwarmUserErrorException)
        {
            throw;
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

    private static void AppendWithBoundarySpace(StringBuilder dest, string add)
    {
        if (string.IsNullOrEmpty(add))
        {
            return;
        }
        if (dest.Length > 0
            && !char.IsWhiteSpace(dest[dest.Length - 1])
            && !char.IsWhiteSpace(add[0]))
        {
            dest.Append(' ');
        }
        dest.Append(add);
    }
}
