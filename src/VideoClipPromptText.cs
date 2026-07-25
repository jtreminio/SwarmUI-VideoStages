using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Selects the prompt prose owned by a clip or stage and applies the documented fallback chain.</summary>
internal static class VideoClipPromptText
{
    public static bool HasAnySectionForClip(string prompt, int clipIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt)
            || !prompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        string canonical = VideoClipTagCanonicalizer.CanonicalizeBrackets(
            prompt,
            clipIndex,
            clipStageIndexWithinClip: null,
            globalCid,
            clipCid,
            VideoClipPromptSyntax.NoMatchCid);

        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix == VideoClipPromptSyntax.TagName
                && (part.ContextID == globalCid || part.ContextID == clipCid))
            {
                return true;
            }
        }
        return false;
    }

    public static string Extract(
        string prompt,
        string originalPrompt,
        int clipIndex,
        int? clipStageFlatId,
        int? clipStageIndexWithinClip)
    {
        string extracted = ExtractWithoutReferences(prompt, clipIndex, clipStageFlatId, clipStageIndexWithinClip);
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
            string previousPrompt = Extract(prompt, originalPrompt, previousClip, null, null);
            if (!string.IsNullOrWhiteSpace(previousPrompt))
            {
                return previousPrompt;
            }
        }

        string videoText = GetVideoPromptText(prompt);
        return !string.IsNullOrWhiteSpace(videoText) ? videoText : GetGlobalPromptText(prompt);
    }

    public static string ExtractWithoutReferences(
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
            : VideoClipPromptSyntax.NoMatchCid;

        string canonical = VideoClipTagCanonicalizer.CanonicalizeBrackets(
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
            if (part.Prefix != VideoClipPromptSyntax.TagName)
            {
                continue;
            }
            int cid = part.ContextID;
            if (cid == globalCid
                || cid == clipCid
                || (stageCid != VideoClipPromptSyntax.NoMatchCid && cid == stageCid))
            {
                sawRelevant = true;
                AppendWithBoundarySpace(result, part.Prompt);
            }
        }

        return sawRelevant ? result.ToString().Trim() : VideoClipTagCanonicalizer.RemoveAllSections(canonical);
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

        string sourceSection = ExtractWithoutReferences(sourcePrompt, clipIndex, null, null);
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

    private static string GetGlobalPromptText(string prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? "" : new PromptRegion(prompt).GlobalPrompt.Trim();

    private static string GetVideoPromptText(string prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? "" : new PromptRegion(prompt).VideoPrompt.Trim();

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
