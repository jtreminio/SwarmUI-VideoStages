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

        List<string> newLoras = [.. loras];
        List<string> newWeights = [.. weights];
        List<string> newTencWeights = [.. tencWeights];
        List<string> newConfinements = [.. confinements];

        while (newWeights.Count < loras.Count)
        {
            newWeights.Add("1");
        }
        while (newTencWeights.Count < loras.Count)
        {
            newTencWeights.Add(newWeights[newTencWeights.Count]);
        }
        while (newConfinements.Count < loras.Count)
        {
            newConfinements.Add("-1");
        }

        foreach (int index in selectedIndices)
        {
            string weight = index < weights.Count ? weights[index] : "1";
            string tencWeight = index < tencWeights.Count ? tencWeights[index] : weight;
            newLoras.Add(loras[index]);
            newWeights.Add(weight);
            newTencWeights.Add(tencWeight);
            newConfinements.Add($"{T2IParamInput.SectionID_Video}");
        }

        ParamSnapshot snapshot = ParamSnapshot.Of(input,
            T2IParamTypes.Loras.Type,
            T2IParamTypes.LoraWeights.Type,
            T2IParamTypes.LoraTencWeights.Type,
            T2IParamTypes.LoraSectionConfinement.Type);
        input.Set(T2IParamTypes.Loras, newLoras);
        input.Set(T2IParamTypes.LoraWeights, newWeights);
        input.Set(T2IParamTypes.LoraTencWeights, newTencWeights);
        input.Set(T2IParamTypes.LoraSectionConfinement, newConfinements);
        return snapshot;
    }

    /// <summary>
    /// Rewrites raw <c>&lt;videoclip&gt;</c>, <c>&lt;videoclip[X]&gt;</c>, and <c>&lt;videoclip[X,Y]&gt;</c>
    /// tags into <c>&lt;videoclip//cid=N&gt;</c> form so <see cref="PromptRegion"/> can segment them.
    /// The emitted cid <i>is</i> the predicate: matching tags get a real section id (global/clip/stage),
    /// non-matching tags get <see cref="NoMatchCid"/>. Tags that already carry <c>//cid=</c> are left alone.
    /// </summary>
    private static string CanonicalizeVideoclipBrackets(
        string prompt,
        int clipIndex,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipCid,
        int stageCid)
    {
        StringBuilder result = new(prompt.Length + 16);
        string[] pieces = prompt.Split('<');
        bool first = true;
        foreach (string piece in pieces)
        {
            if (first)
            {
                first = false;
                result.Append(piece);
                continue;
            }
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }
            int end = piece.IndexOf('>');
            if (end == -1)
            {
                result.Append('<').Append(piece);
                continue;
            }
            string tag = piece[..end];
            string content = piece[(end + 1)..];

            if (tag.Contains(VideoClipCidMarker, StringComparison.OrdinalIgnoreCase)
                || !TryParseVideoclipTag(tag, out string preData))
            {
                result.Append('<').Append(piece);
                continue;
            }

            int cid = ResolveBracketCid(preData, clipIndex, clipStageIndexWithinClip, globalCid, clipCid, stageCid);
            result.Append('<').Append(VideoClipTagName).Append(VideoClipCidMarker).Append(cid)
                  .Append('>').Append(content);
        }
        return result.ToString();
    }

    private static bool TryParseVideoclipTag(string tag, out string preData)
    {
        preData = null;
        string prefix = tag.BeforeAndAfter(':', out _);
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

    /// <summary>
    /// Strips every videoclip section (and its trailing content) from a canonicalized prompt,
    /// preserving everything else. Section transitions (e.g., <c>&lt;base&gt;</c>) end the strip.
    /// </summary>
    private static string RemoveAllVideoClipSections(string canonicalPrompt)
    {
        if (!canonicalPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPrompt.Trim();
        }

        StringBuilder result = new();
        bool first = true;
        bool inVideoclip = false;
        foreach (string piece in canonicalPrompt.Split('<'))
        {
            if (first)
            {
                first = false;
                result.Append(piece);
                continue;
            }
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }
            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (!inVideoclip)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }
            string prefix = ExtractTagPrefixLower(piece[..end]);
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
            result.Append('<').Append(piece);
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
