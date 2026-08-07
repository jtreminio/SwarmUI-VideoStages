using FreneticUtilities.FreneticExtensions;
using SwarmUI.Utils;

namespace VideoStages.Authoring;

internal static class PromptText
{
    private static readonly HashSet<string> SectionStarters = [
        "base", "refiner", "pixeldecoder", "video", "videoswap", "edit",
        "region", "segment", "object", "clear", "extend"
    ];

    private readonly record struct Piece(string Text, bool IsLeadingText, int TagEnd)
    {
        public bool HasTag => TagEnd >= 0;
        public string Tag => Text[..TagEnd];
        public string Content => Text[(TagEnd + 1)..];
    }

    public static bool HasAnySectionForClip(string prompt, int clipIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt)
            || !prompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        string canonical = CanonicalizeBrackets(
            prompt,
            clipIndex,
            clipStageIndexWithinClip: null,
            globalCid,
            clipCid,
            PromptSyntax.NoMatchCid);

        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix == PromptSyntax.TagName
                && (part.ContextID == globalCid || part.ContextID == clipCid))
            {
                return true;
            }
        }
        return false;
    }

    public static string ResolveForTarget(
        string prompt,
        string originalPrompt,
        int clipIndex,
        int? clipStageFlatId = null,
        int? clipStageIndexWithinClip = null)
    {
        string extracted = SelectMatchingSections(prompt, clipIndex, clipStageFlatId, clipStageIndexWithinClip);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        if (!ShouldFallbackForTagOnlySection(prompt, originalPrompt, clipIndex))
        {
            return extracted.Trim();
        }

        for (int previousClip = clipIndex - 1; previousClip >= 0; previousClip--)
        {
            string previousPrompt = ResolveForTarget(prompt, originalPrompt, previousClip, null, null);
            if (!string.IsNullOrWhiteSpace(previousPrompt))
            {
                return previousPrompt;
            }
        }

        return SelectVideoOrGlobalPrompt(prompt);
    }

    public static string SelectMatchingSections(
        string prompt,
        int clipIndex,
        int? clipStageFlatId,
        int? clipStageIndexWithinClip)
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
            : PromptSyntax.NoMatchCid;

        string canonical = CanonicalizeBrackets(
            prompt,
            clipIndex,
            clipStageIndexWithinClip,
            globalCid,
            clipCid,
            stageCid);

        StringBuilder result = new();
        bool sawRelevant = false;
        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix != PromptSyntax.TagName)
            {
                continue;
            }
            int cid = part.ContextID;
            if (cid == globalCid
                || cid == clipCid
                || (stageCid != PromptSyntax.NoMatchCid && cid == stageCid))
            {
                sawRelevant = true;
                AppendWithBoundarySpace(result, part.Prompt);
            }
        }

        return sawRelevant ? result.ToString().Trim() : RemoveAllSections(canonical);
    }

    private static bool ShouldFallbackForTagOnlySection(
        string parsedPrompt,
        string originalPrompt,
        int clipIndex)
    {
        if (clipIndex < 0)
        {
            return false;
        }

        string sourcePrompt = string.IsNullOrWhiteSpace(originalPrompt) ? parsedPrompt : originalPrompt;
        if (!HasAnySectionForClip(sourcePrompt, clipIndex))
        {
            return false;
        }

        string sourceSection = SelectMatchingSections(sourcePrompt, clipIndex, null, null);
        if (string.IsNullOrWhiteSpace(sourceSection) || !sourceSection.Contains('<'))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(StripTags(sourceSection));
    }

    private static string StripTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('<'))
        {
            return text ?? "";
        }

        StringBuilder cleaned = new(text.Length);
        bool inTag = false;
        foreach (char character in text)
        {
            if (!inTag)
            {
                if (character == '<')
                {
                    inTag = true;
                }
                else
                {
                    cleaned.Append(character);
                }
            }
            else if (character == '>')
            {
                inTag = false;
            }
        }
        return cleaned.ToString();
    }

    /// <summary>Core's rule for which section a video model conditions on, in
    /// <c>WorkflowGenerator.CreateConditioning</c>'s <c>isVideo</c> arm: the video section when it
    /// has text, the global section otherwise. The extension bypasses that call, so it owes the
    /// rule one owner here rather than one per caller.</summary>
    internal static string SelectVideoOrGlobalPrompt(string prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? ""
            : SelectVideoOrGlobalPrompt(new PromptRegion(prompt));

    /// <summary>For a caller that already parsed the prompt for something else.</summary>
    internal static string SelectVideoOrGlobalPrompt(PromptRegion regionalizer)
    {
        string video = regionalizer.VideoPrompt.Trim();
        return video.Length > 0 ? video : regionalizer.GlobalPrompt.Trim();
    }

    private static string CanonicalizeBrackets(
        string prompt,
        int clipIndex,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipCid,
        int stageCid)
    {
        StringBuilder result = new(prompt.Length + 16);
        foreach (Piece piece in Scan(prompt))
        {
            if (piece.IsLeadingText)
            {
                result.Append(piece.Text);
                continue;
            }
            if (!piece.HasTag)
            {
                result.Append('<').Append(piece.Text);
                continue;
            }
            string tag = piece.Tag;
            if (tag.Contains(PromptSyntax.CidMarker, StringComparison.OrdinalIgnoreCase)
                || !PromptSyntax.TryParseAuthoringTag(tag, out string preData, out string value))
            {
                result.Append('<').Append(piece.Text);
                continue;
            }

            int cid = string.IsNullOrEmpty(value)
                ? ResolveBracketCid(preData, clipIndex, clipStageIndexWithinClip, globalCid, clipCid, stageCid)
                : PromptSyntax.NoMatchCid;
            result.Append(PromptSyntax.FormatSectionMarker(cid)).Append(piece.Content);
        }
        return result.ToString();
    }

    private static string RemoveAllSections(string canonicalPrompt)
    {
        if (!canonicalPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPrompt.Trim();
        }

        StringBuilder result = new();
        bool inVideoclip = false;
        foreach (Piece piece in Scan(canonicalPrompt))
        {
            if (piece.IsLeadingText)
            {
                result.Append(piece.Text);
                continue;
            }
            if (!piece.HasTag)
            {
                if (!inVideoclip)
                {
                    result.Append('<').Append(piece.Text);
                }
                continue;
            }
            string prefix = ExtractPrefixLower(piece.Tag);
            if (prefix == PromptSyntax.TagName)
            {
                inVideoclip = true;
                continue;
            }
            if (inVideoclip && !IsSectionStartingTag(prefix))
            {
                continue;
            }
            inVideoclip = false;
            result.Append('<').Append(piece.Text);
        }
        return result.ToString().Trim();
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
            string[] tokens = preTrimmed.Split(',', 2);
            if (!int.TryParse(tokens[0].Trim(), out int tagClip)
                || !int.TryParse(tokens[1].Trim(), out int tagStage)
                || tagClip != clipIndex)
            {
                return PromptSyntax.NoMatchCid;
            }
            if (!clipStageIndexWithinClip.HasValue)
            {
                return clipCid;
            }
            return tagStage == clipStageIndexWithinClip.Value ? stageCid : PromptSyntax.NoMatchCid;
        }
        return int.TryParse(preTrimmed, out int singleClip) && singleClip == clipIndex
            ? clipCid
            : PromptSyntax.NoMatchCid;
    }

    private static IEnumerable<Piece> Scan(string prompt)
    {
        string[] pieces = prompt.Split('<');
        yield return new Piece(pieces[0], IsLeadingText: true, TagEnd: -1);
        foreach (string piece in pieces.Skip(1))
        {
            if (!string.IsNullOrEmpty(piece))
            {
                yield return new Piece(piece, IsLeadingText: false, piece.IndexOf('>'));
            }
        }
    }

    private static string ExtractPrefixLower(string tag)
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

    private static bool IsSectionStartingTag(string prefixLower)
    {
        if (SectionStarters.Contains(prefixLower))
        {
            return true;
        }
        foreach (string prefix in PromptRegion.CustomPartPrefixes)
        {
            if (StringUtils.Equals(prefix, prefixLower))
            {
                return true;
            }
        }
        return false;
    }

    private static void AppendWithBoundarySpace(StringBuilder destination, string addition)
    {
        if (string.IsNullOrEmpty(addition))
        {
            return;
        }
        if (destination.Length > 0
            && !char.IsWhiteSpace(destination[destination.Length - 1])
            && !char.IsWhiteSpace(addition[0]))
        {
            destination.Append(' ');
        }
        destination.Append(addition);
    }
}
