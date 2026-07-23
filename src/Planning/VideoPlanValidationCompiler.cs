namespace VideoStages.Planning;

/// <summary>Validates option combinations that cannot be represented safely by the LTX runtime.</summary>
internal static class VideoPlanValidationCompiler
{
    internal static IReadOnlyList<VideoPlanDiagnostic> Validate(IReadOnlyList<ClipPlan> clips)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in clips)
        {
            if (clip.Audio.Length.Owner is AudioLengthOwner.Audio or AudioLengthOwner.ControlNet
                && clip.Stages.Any(stage => !stage.PromptRelay.AuthoredWindows.IsDefaultOrEmpty))
            {
                diagnostics.Add(Error(
                    "prompt-relay-dynamic-length-unsupported",
                    "Prompt relay cannot be combined with audio-owned or ControlNet-owned clip length because the relay schedule requires a fixed frame count.",
                    clip.ClipId));
            }

            foreach (StagePlan stage in clip.Stages)
            {
                if (stage.Retake is not null && !stage.FrameReferences.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Error(
                        "retake-frame-references-unsupported",
                        $"Clip {clip.ClipId} stage {stage.ClipStageIndex} combines a retake with frame references; guide merges would overwrite the retake mask.",
                        clip.ClipId,
                        stage.StageId));
                }
            }
        }

        if (clips.Count > 1)
        {
            bool[] hdrClips = [.. clips.Select(HdrIcLoraPolicy.IsActive)];
            if (hdrClips.Any(value => value) && hdrClips.Any(value => !value))
            {
                diagnostics.Add(Error(
                    "mixed-hdr-timeline-unsupported",
                    "A multi-clip timeline cannot mix HDR IC-LoRA clips with non-HDR clips because final HDR conversion applies to the complete timeline."));
            }
        }
        return diagnostics.AsReadOnly();
    }
    private static VideoPlanDiagnostic Error(
        string code,
        string message,
        int? clipId = null,
        int? stageId = null) =>
        new(VideoPlanDiagnosticSeverity.Error, code, message, clipId, stageId);
}
