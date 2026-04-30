using FreneticUtilities.FreneticExtensions;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class PromptParser
{
    private const string VideoClipCidMarker = "//cid=";

    private static readonly HashSet<string> SectionEndingTags = [
        "base",
        "refiner",
        "video",
        "videoswap",
        "edit",
        "region",
        "segment",
        "object",
        "clear",
        "extend"
    ];

    private static bool IsSectionEndingTag(string tagPrefixLower)
    {
        if (SectionEndingTags.Contains(tagPrefixLower))
        {
            return true;
        }
        foreach (string prefix in PromptRegion.CustomPartPrefixes)
        {
            if (StringUtils.Equals(prefix, tagPrefixLower))
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryExtractTagPrefix(string tag, out string prefixName, out string preData)
    {
        prefixName = null;
        preData = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string prefixPart = tag;
        int colon = tag.IndexOf(':');
        if (colon != -1)
        {
            prefixPart = tag[..colon];
        }
        prefixPart = prefixPart.Split('/')[0];
        if (string.IsNullOrWhiteSpace(prefixPart))
        {
            return false;
        }

        prefixName = prefixPart;
        if (prefixName.EndsWith(']') && prefixName.Contains('['))
        {
            int open = prefixName.LastIndexOf('[');
            preData = prefixName[(open + 1)..^1];
            prefixName = prefixName[..open];
        }

        return !string.IsNullOrWhiteSpace(prefixName);
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

            return false;
        }

        if (!int.TryParse(clipToken, out int clipId) || clipId < 0
            || !int.TryParse(stageToken, out int clipStageIdx) || clipStageIdx < 0)
        {
            return false;
        }

        if (TryFlattenedStageSectionId(context.Input, clipId, clipStageIdx, context, out int stageSection))
        {
            sectionId = stageSection;
            return true;
        }

        return false;
    }

    public static bool TryFlattenedStageSectionId(
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

        List<JsonParser.StageSpec> flat;
        try
        {
            flat = new JsonParser(generator).ParseStages();
        }
        catch (Exception ex)
        {
            context.TrackWarning(
                $"VideoStages: could not parse Video Stages JSON for videoclip[{clipId},{clipStageIndex}]: "
                + $"{ex.Message}");
            return false;
        }

        foreach (JsonParser.StageSpec stage in flat)
        {
            if (stage.ClipId == clipId && stage.ClipStageIndex == clipStageIndex)
            {
                sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                return true;
            }
        }

        context.TrackWarning(
            "VideoStages: no active stage videoclip["
            + $"{clipId},{clipStageIndex}] in the current Video Stages configuration.");
        return false;
    }

    private static bool VideoClipTagAppliesToClip(
        string tag,
        string preData,
        int clipId,
        int? clipStageFlatId,
        int? clipStageIndexWithinClip,
        int globalCid,
        int clipSectionCid)
    {
        int cidCut = tag.LastIndexOf(VideoClipCidMarker, StringComparison.OrdinalIgnoreCase);
        if (cidCut != -1)
        {
            if (int.TryParse(tag[(cidCut + VideoClipCidMarker.Length)..], out int cid))
            {
                if (cid == globalCid || cid == clipSectionCid)
                {
                    return true;
                }
                if (clipStageFlatId.HasValue
                    && cid == VideoStagesExtension.SectionIdForStage(clipStageFlatId.Value))
                {
                    return true;
                }

                return false;
            }
        }
        if (preData is null)
        {
            return true;
        }
        string preTrimmed = preData.Trim();
        if (preTrimmed.Contains(','))
        {
            string first = preTrimmed.BeforeAndAfter(',', out string second);
            if (int.TryParse(first.Trim(), out int tagClipComma)
                && int.TryParse(second.Trim(), out int tagStageComma))
            {
                if (tagClipComma != clipId)
                {
                    return false;
                }
                if (!clipStageIndexWithinClip.HasValue)
                {
                    return true;
                }

                return tagStageComma == clipStageIndexWithinClip.Value;
            }
        }
        return int.TryParse(preTrimmed, out int tagClipSingle) && tagClipSingle == clipId;
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

        foreach (string piece in prompt.Split('<'))
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                continue;
            }

            string tag = piece[..end];
            if (!TryExtractTagPrefix(tag, out string prefixName, out string preData)
                || !StringUtils.Equals(prefixName, "videoclip"))
            {
                continue;
            }

            if (VideoClipTagAppliesToClip(
                    tag,
                    preData,
                    clipIndex,
                    clipStageFlatId: null,
                    clipStageIndexWithinClip: null,
                    globalCid,
                    clipCid))
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

        return GetGlobalPromptText(prompt);
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

    public static bool ShouldFallbackForTagOnlyVideoClipSection(
        string parsedPrompt,
        string originalPrompt,
        int clipIndex)
    {
        if (clipIndex < 0 || !HasAnyVideoClipSectionForClip(parsedPrompt, clipIndex))
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

    public static string StripPromptTags(string text)
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
        StringBuilder result = new();
        string[] pieces = prompt.Split('<');
        bool inWantedSection = false;
        bool sawRelevantVideoClipTag = false;

        foreach (string piece in pieces)
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (inWantedSection)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }

            string tag = piece[..end];
            string content = piece[(end + 1)..];
            if (!TryExtractTagPrefix(tag, out string prefixName, out string preData))
            {
                if (inWantedSection)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }

            string tagPrefixLower = prefixName.ToLowerInvariant();
            bool isVideoClipTag = tagPrefixLower == "videoclip";
            if (isVideoClipTag)
            {
                bool wantThisSection = VideoClipTagAppliesToClip(
                    tag,
                    preData,
                    clipIndex,
                    clipStageFlatId,
                    clipStageIndexWithinClip,
                    globalCid,
                    clipCid);

                if (wantThisSection)
                {
                    sawRelevantVideoClipTag = true;
                }

                inWantedSection = wantThisSection;
                if (inWantedSection)
                {
                    AppendWithBoundarySpace(result, content);
                }
            }
            else if (inWantedSection)
            {
                if (IsSectionEndingTag(tagPrefixLower))
                {
                    inWantedSection = false;
                }
                else
                {
                    result.Append('<').Append(piece);
                }
            }
        }

        if (!sawRelevantVideoClipTag)
        {
            return RemoveAllVideoClipSections(prompt);
        }

        return result.ToString().Trim();
    }

    public static string GetGlobalPromptText(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }

        PromptRegion region = new(prompt);
        return region.GlobalPrompt.Trim();
    }

    public static LoraOverrideScope ApplyLoraScope(T2IParamInput input, int clipIndex, int stageSectionId)
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

        LoraOverrideScope scope = new(input);
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

        input.Set(T2IParamTypes.Loras, newLoras);
        input.Set(T2IParamTypes.LoraWeights, newWeights);
        input.Set(T2IParamTypes.LoraTencWeights, newTencWeights);
        input.Set(T2IParamTypes.LoraSectionConfinement, newConfinements);
        return scope;
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

    private static string RemoveAllVideoClipSections(string fullPrompt)
    {
        if (string.IsNullOrWhiteSpace(fullPrompt)
            || !fullPrompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase))
        {
            return (fullPrompt ?? "").Trim();
        }

        StringBuilder result = new();
        bool inAnyVideoClipSection = false;
        string[] pieces = fullPrompt.Split('<');
        bool isFirstPiece = true;

        foreach (string piece in pieces)
        {
            if (isFirstPiece)
            {
                isFirstPiece = false;
                if (!inAnyVideoClipSection)
                {
                    result.Append(piece);
                }
                continue;
            }

            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (!inAnyVideoClipSection)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }

            string tag = piece[..end];
            if (!TryExtractTagPrefix(tag, out string prefixName, out _))
            {
                if (!inAnyVideoClipSection)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }

            string tagPrefixLower = prefixName.ToLowerInvariant();
            bool isVideoClipTag = tagPrefixLower == "videoclip";
            if (isVideoClipTag)
            {
                inAnyVideoClipSection = true;
                continue;
            }

            if (inAnyVideoClipSection)
            {
                if (IsSectionEndingTag(tagPrefixLower))
                {
                    inAnyVideoClipSection = false;
                }
                else
                {
                    continue;
                }
            }

            result.Append('<').Append(piece);
        }

        return result.ToString().Trim();
    }

    internal sealed class LoraOverrideScope : IDisposable
    {
        private readonly T2IParamInput _input;
        private readonly bool _hadLoras;
        private readonly bool _hadWeights;
        private readonly bool _hadTencWeights;
        private readonly bool _hadConfinements;
        private readonly List<string> _loras;
        private readonly List<string> _weights;
        private readonly List<string> _tencWeights;
        private readonly List<string> _confinements;

        public LoraOverrideScope(T2IParamInput input)
        {
            _input = input;
            _hadLoras = input.TryGet(T2IParamTypes.Loras, out List<string> loras);
            _hadWeights = input.TryGet(T2IParamTypes.LoraWeights, out List<string> weights);
            _hadTencWeights = input.TryGet(T2IParamTypes.LoraTencWeights, out List<string> tencWeights);
            _hadConfinements = input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> confinements);
            _loras = loras is null ? null : [.. loras];
            _weights = weights is null ? null : [.. weights];
            _tencWeights = tencWeights is null ? null : [.. tencWeights];
            _confinements = confinements is null ? null : [.. confinements];
        }

        public void Dispose()
        {
            Restore(T2IParamTypes.Loras, _hadLoras, _loras);
            Restore(T2IParamTypes.LoraWeights, _hadWeights, _weights);
            Restore(T2IParamTypes.LoraTencWeights, _hadTencWeights, _tencWeights);
            Restore(T2IParamTypes.LoraSectionConfinement, _hadConfinements, _confinements);
        }

        private void Restore(T2IRegisteredParam<List<string>> param, bool hadValue, List<string> value)
        {
            if (hadValue)
            {
                _input.Set(param, value);
            }
            else
            {
                _input.Remove(param);
            }
        }
    }
}
