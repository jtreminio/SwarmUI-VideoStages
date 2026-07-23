namespace VideoStages;

/// <summary>Converts authored bracket selectors to processed section markers and removes unselected clip sections.</summary>
internal static class VideoClipTagCanonicalizer
{
    public static string CanonicalizeBrackets(
        string prompt,
        int clipIndex,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipCid,
        int stageCid)
    {
        StringBuilder result = new(prompt.Length + 16);
        foreach (PromptTagScanner.Piece piece in PromptTagScanner.Scan(prompt))
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

            if (tag.Contains(VideoClipPromptSyntax.CidMarker, StringComparison.OrdinalIgnoreCase)
                || !VideoClipPromptSyntax.TryParseAuthoringTag(tag, out string preData, out string value))
            {
                result.Append('<').Append(piece.Text);
                continue;
            }

            int cid = string.IsNullOrEmpty(value)
                ? ResolveBracketCid(preData, clipIndex, clipStageIndexWithinClip, globalCid, clipCid, stageCid)
                : VideoClipPromptSyntax.NoMatchCid;
            result.Append('<').Append(VideoClipPromptSyntax.TagName).Append(VideoClipPromptSyntax.CidMarker).Append(cid)
                .Append('>').Append(piece.Content);
        }
        return result.ToString();
    }

    public static string RemoveAllSections(string canonicalPrompt)
    {
        if (!canonicalPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPrompt.Trim();
        }

        StringBuilder result = new();
        bool inVideoclip = false;
        foreach (PromptTagScanner.Piece piece in PromptTagScanner.Scan(canonicalPrompt))
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
            string prefix = PromptTagScanner.ExtractPrefixLower(piece.Tag);
            if (prefix == VideoClipPromptSyntax.TagName)
            {
                inVideoclip = true;
                continue;
            }
            if (inVideoclip && !PromptTagScanner.IsSectionStartingTag(prefix))
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
                || !int.TryParse(tokens[1].Trim(), out int tagStage))
            {
                return VideoClipPromptSyntax.NoMatchCid;
            }
            if (tagClip != clipIndex)
            {
                return VideoClipPromptSyntax.NoMatchCid;
            }
            if (!clipStageIndexWithinClip.HasValue)
            {
                return clipCid;
            }
            return tagStage == clipStageIndexWithinClip.Value ? stageCid : VideoClipPromptSyntax.NoMatchCid;
        }

        return int.TryParse(preTrimmed, out int singleClip) && singleClip == clipIndex
            ? clipCid
            : VideoClipPromptSyntax.NoMatchCid;
    }
}
