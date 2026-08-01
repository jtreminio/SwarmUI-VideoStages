using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>Maps authored clip and clip-stage selectors to SwarmUI prompt section identifiers.</summary>
internal static class VideoClipSectionResolver
{
    public static bool TryResolve(
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
            || !int.TryParse(stageToken, out int clipStageIndex) || clipStageIndex < 0)
        {
            sectionId = Constants.SectionID_VideoClipUnmatched;
            return false;
        }

        if (TryResolveFlattenedStage(context.Input, clipId, clipStageIndex, context, out int stageSection))
        {
            sectionId = stageSection;
            return true;
        }

        sectionId = Constants.SectionID_VideoClipUnmatched;
        return false;
    }

    private static bool TryResolveFlattenedStage(
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

        VideoStagesSpec spec;
        try
        {
            spec = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
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
