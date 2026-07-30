using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Fails closed on a malformed or stale immutable Wan clip before its session can install source
/// media, open host parameter sections, or mutate the workflow graph.
/// </summary>
internal static class WanRuntimeClipContract
{
    internal static void Validate(VideoExecutionPlan plan, ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(clip);
        WanClipPayload clipPayload = clip.ArchitecturePayload as WanClipPayload
            ?? throw Invalid(clip, "has no canonical Wan clip payload");
        if (!ReferenceEquals(
                clip.Architecture,
                WanArchitectureModule.Instance.Descriptor)
            || clip.Architecture?.Id != WanArchitectureModule.ArchitectureId
            || clipPayload.ArchitectureId != WanArchitectureModule.ArchitectureId
            || clipPayload.ClipId != clip.ClipId
            || !WanArchitectureModule.IsSupportedProfile(clipPayload.ProfileId)
            || string.IsNullOrWhiteSpace(clipPayload.CompatibilityClassId)
            || !clip.Architecture.Profiles.Any(
                profile => profile.Id == clipPayload.ProfileId))
        {
            throw Invalid(
                clip,
                "has incongruent Wan architecture, profile, or clip-payload identity");
        }

        bool sourcedEntry =
            clip.EntryMode == ArchitectureEntryMode.SourceVideo
            && clip.Input == ClipInputKind.SourceVideo
            && clip.IsSourced
            && clip.SourceVideo is not null;
        bool generatedEntry =
            clip.EntryMode == ArchitectureEntryMode.ImageToVideo
            && clip.Input == ClipInputKind.RootMedia
            && !clip.IsSourced
            && clip.SourceVideo is null;
        bool textEntry =
            clip.EntryMode == ArchitectureEntryMode.TextToVideo
            && clip.Input == ClipInputKind.EmptyLatent
            && !clip.IsSourced
            && clip.SourceVideo is null
            && clipPayload.ProfileId == WanArchitectureModule.Ti2v5bProfileId
            && plan.Root is
            {
                HostKind: HostRootKind.TextToVideoRoot,
                Use: RootUse.Discard,
                CoreDisposition: HostCoreDisposition.Drop,
                OutputDisposition: TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition: NativeAudioDisposition.DiscardWithRoot,
            };
        if (!sourcedEntry && !generatedEntry && !textEntry)
        {
            throw Invalid(clip, "has incongruent entry mode, clip input, or source-video plan");
        }
        if (sourcedEntry
            && (string.IsNullOrWhiteSpace(clip.SourceVideo.Data)
                || !double.IsFinite(clip.SourceVideo.StartSeconds)
                || clip.SourceVideo.StartSeconds < 0
                || clip.SourceVideo.TargetWidth <= 0
                || clip.SourceVideo.TargetHeight <= 0
                || clip.SourceVideo.TargetFramesPerSecond != plan.FramesPerSecond
                || clip.Frames is not int sourceFrames
                || sourceFrames <= 0))
        {
            throw Invalid(clip, "has an invalid source-video execution plan");
        }
        if (clip.Stages.Count == 0)
        {
            throw Invalid(clip, "has no Wan stage");
        }

        HashSet<int> rawIndices = [];
        int terminalGeneratingIndex = clip.Stages
            .Select((stage, index) => (stage, index))
            .Where(item => !item.stage.IsPassthrough)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Last();
        bool exactLegacyEndFrameClip =
            generatedEntry
            && clip.Stages.All(stage =>
                stage.ResolvedModel?.ModelProfileId
                    == WanArchitectureModule.ImageToVideoProfileId
                && string.Equals(
                        stage.ResolvedModel.ModelClassId,
                        WanArchitectureModule.ImageToVideoModelClassId,
                        StringComparison.OrdinalIgnoreCase));
        for (int index = 0; index < clip.Stages.Count; index++)
        {
            StagePlan stage = clip.Stages[index];
            WanStagePayload payload = stage.ArchitecturePayload as WanStagePayload
                ?? throw Invalid(clip, $"stage {stage.StageId} has no canonical Wan stage payload");
            StageInputKind expectedInput = index == 0
                ? sourcedEntry
                    ? StageInputKind.SourceVideo
                    : textEntry ? StageInputKind.EmptyLatent : StageInputKind.RootMedia
                : StageInputKind.PreviousStage;
            if (stage.ClipStageIndex != index
                || stage.ClipStageRawIndex < 0
                || !rawIndices.Add(stage.ClipStageRawIndex)
                || stage.Input != expectedInput)
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} has an invalid position or decoded-input owner");
            }
            bool decodedInput =
                stage.Input is StageInputKind.SourceVideo or StageInputKind.PreviousStage;
            ValidateUpscale(clip, stage, payload.Upscale, decodedInput);
            bool passthrough = decodedInput && payload.Control == 0;
            bool expectedEndFrameOwner =
                exactLegacyEndFrameClip
                && index == terminalGeneratingIndex;
            if (payload.OwnsVideoEndFrame != expectedEndFrameOwner)
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} has invalid request-global end-frame ownership");
            }
            if (payload.Loras.IsDefault
                || payload.Loras.Any(lora =>
                    lora is null
                    || string.IsNullOrWhiteSpace(lora.Name)
                    || !double.IsFinite(lora.ModelWeight)
                    || !double.IsFinite(lora.TextEncoderWeight)
                    || lora.ModelWeight == 0))
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} has an invalid immutable normal-LoRA payload");
            }
            if (passthrough && !payload.Loras.IsDefaultOrEmpty)
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} is a samplerless passthrough with a normal-LoRA plan");
            }
            if (!double.IsFinite(payload.Control)
                || decodedInput && (payload.Control < 0 || payload.Control > 1)
                || !decodedInput && payload.Control != 1
                || stage.IsPassthrough != passthrough
                || payload.Steps <= 0)
            {
                throw Invalid(clip, $"stage {stage.StageId} has an invalid Wan control contract");
            }
            if (payload.Control > 0
                && WanStageSchedulePolicy.IsQuantizedZeroPartial(
                    payload.Steps,
                    payload.Control))
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} has a partial control that quantizes to step 0");
            }
            ResolvedVideoModel resolved = stage.ResolvedModel;
            if (payload.ArchitectureId != WanArchitectureModule.ArchitectureId
                || resolved?.ArchitectureId != WanArchitectureModule.ArchitectureId
                || resolved.ModelProfileId != payload.ProfileId
                || !WanArchitectureModule.IsSupportedProfile(resolved.ModelProfileId)
                || string.IsNullOrWhiteSpace(resolved.ModelClassId)
                || string.IsNullOrWhiteSpace(resolved.CompatibilityClassId)
                || !string.Equals(
                    resolved.ModelClassId,
                    payload.ModelClassId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    resolved.CompatibilityClassId,
                    payload.CompatibilityClassId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    resolved.CompatibilityClassId,
                    clipPayload.CompatibilityClassId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    resolved.ModelName,
                    payload.Model,
                    StringComparison.Ordinal)
                || !ReferenceEquals(
                    resolved.Architecture,
                    WanArchitectureModule.Instance.Descriptor)
                || resolved.Architecture?.Id != WanArchitectureModule.ArchitectureId
                || !resolved.Architecture.Profiles.Any(
                    profile => profile.Id == resolved.ModelProfileId))
            {
                throw Invalid(
                    clip,
                    $"stage {stage.StageId} does not preserve the clip's canonical Wan model facts");
            }
        }
    }

    private static void ValidateUpscale(
        ClipPlan clip,
        StagePlan stage,
        StageUpscalePlan upscale,
        bool decodedInput)
    {
        bool validFactor = upscale is not null
            && double.IsFinite(upscale.Factor)
            && upscale.Factor > 0;
        bool canonicalPixelMethod = upscale is not null
            && !string.IsNullOrWhiteSpace(upscale.RawMethod)
            && !string.IsNullOrWhiteSpace(upscale.MethodName)
            && upscale.RawMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                upscale.RawMethod["pixel-".Length..],
                upscale.MethodName,
                StringComparison.Ordinal);
        bool canonicalNone = upscale?.Mode == StageUpscaleMode.None
            && upscale.Factor == 1;
        bool canonicalPixel = upscale?.Mode == StageUpscaleMode.Pixel
            && upscale.Factor != 1
            && decodedInput
            && canonicalPixelMethod;
        if (!validFactor || !canonicalNone && !canonicalPixel)
        {
            throw Invalid(
                clip,
                $"stage {stage.StageId} has an invalid immutable pixel-upscale payload");
        }
    }

    private static InvalidOperationException Invalid(ClipPlan clip, string detail) =>
        new($"Clip {clip.ClipId} reached the Wan runtime with a malformed plan: {detail}.");
}
