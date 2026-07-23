using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

internal sealed class StageSequenceRunner(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner singleStageRunner,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs,
    RootVideoStageResizer rootVideoStageResizer,
    TimelineAssembler timelineAssembler,
    LtxManager ltxManager,
    AudioTimelineExecutor audioTimelineExecutor)
{
    private const int IntermediateStageSaveId = 52100;

    private readonly Dictionary<int, StageRefStore.StageRef> _stageOutputs = [];
    private StageRefStore.StageRef _previousStageRef;
    private readonly AudioTimelineExecutor _audioTimelineExecutor = audioTimelineExecutor;

    public void Run(
        IReadOnlyList<ClipSpec> clips,
        VideoExecutionPlan plan,
        PreparedAudioRuntimeSources preparedAudioSources,
        bool rootStageHandoff = false)
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        IReadOnlyList<ClipPlan> plannedClips = RequirePlannedClips(plan, clips);
        bool sourcedLeadWithGeneratedClips = clips.Count > 0
            && clips[0].SourceVideo is not null
            && clips.Any(clip => clip.SourceVideo is null);
        // In a text-to-video run the generated clips replace the root with their own empty
        // latents and self-generate audio, so a sourced-lead run has NO consumer for the root
        // generation. Its audio latent must not become the replacement clips' audio init: that
        // reference pins the whole unrelated root sampler alive in the graph (a third sampler
        // generating footage nothing uses).
        bool dropTextToVideoRootDonor = sourcedLeadWithGeneratedClips && spec.IsTextToVideo;
        ClipAudioRuntimeSources audioSources = new(
            dropTextToVideoRootDonor
                ? null
                : preparedAudioSources.NativeAudio ?? g.CurrentMedia?.AttachedAudio,
            preparedAudioSources.ClipAudios,
            preparedAudioSources.UploadedAudios,
            rootStageHandoff);
        _stageOutputs.Clear();
        _previousStageRef = null;
        List<int> usedSectionIds = [];
        bool parallelMultiClip = clips.Count > 1;
        try
        {
            if (audioSources.RootStageHandoff)
            {
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            }
            else if (dropTextToVideoRootDonor)
            {
                // Root is getting dropped: stamp the timeline dims as metadata only (the t2v
                // shortcut inside) — a pixel conform would add a scale node onto a chain that the
                // generated clips' root-replacement cleanup is about to prune. Strip the root's
                // attached audio too: every root-media clone inherits it, and a replacement
                // clip's empty latent adopting that audio-latent ref is exactly the pin that
                // keeps the root sampler alive. RootRuntimeSession owns displaced-root cleanup.
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
                if (g.CurrentMedia is not null)
                {
                    g.CurrentMedia.AttachedAudio = null;
                }
            }
            else if (sourcedLeadWithGeneratedClips)
            {
                // A sourced first clip keeps the root generation alive as the GENERATED clips'
                // source/audio donor; conform its pixels to the timeline resolution so every clip
                // (and the cross-clip merge) runs at the same dims. With no generated clip the root
                // is dropped outright — leave it untouched so its save retarget/prune still matches.
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToSurvivingRootMedia();
            }
            CaptureGeneratedReference();
            WGNodeData rootSourceMedia = g.CurrentMedia?.Duplicate();
            WGNodeData rootSourceVae = g.CurrentVae?.Duplicate();
            int totalStageCount = plan.Clips.Sum(clip => clip.Stages.Count);
            bool publishIntermediateStages =
                totalStageCount > 1
                && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
                && !g.UserInput.Get(T2IParamTypes.DoNotSave, false);
            StageExecutionOptions executionOptions = new(
                parallelMultiClip,
                publishIntermediateStages);
            TimelineAssemblySession assembly = timelineAssembler.Begin(plan);
            ClipSpec previousClip = null;
            WGNodeData previousClipOutput = null;
            SourcedClipInstaller sourcedClipInstaller = new(g);
            int clipIndex = 0;
            int completedStageCount = 0;
            List<RuntimeArtifact> clipOutputs = [];
            foreach (ClipSpec clip in clips)
            {
                ClipContext clipContext = new(clip, spec.Width, spec.Height, rootSourceMedia, rootSourceVae);
                RuntimeArtifact clipArtifact = null;
                ClipPlan plannedClip = plannedClips[clipIndex];
                WGNodeData sourcedMedia = null;
                if (clip.SourceVideo is not null)
                {
                    sourcedMedia = sourcedClipInstaller.TryInstall(clip, spec);
                    if (sourcedMedia is null)
                    {
                        throw new SwarmUserErrorException(
                            $"VideoStages: clip {clip.Id} source video could not be installed.");
                    }
                }
                if (parallelMultiClip && previousClip is not null)
                {
                    if (sourcedMedia is null)
                    {
                        if (clipContext.SourceMedia is null)
                        {
                            throw new SwarmUserErrorException(
                                $"VideoStages: clip {clip.Id} requires root media before its first stage.");
                        }

                        g.CurrentMedia = clipContext.SourceMedia.Duplicate();
                        if (clipContext.SourceVae is not null)
                        {
                            g.CurrentVae = clipContext.SourceVae.Duplicate();
                        }
                    }

                    if (assembly.TryGetContinueWindow(previousClip.Id, out int continuityWindow))
                    {
                        if (sourcedMedia is not null)
                        {
                            // The sourced clip's opening frames are fixed footage passed through by
                            // its first stage — there is no generation to condition on the previous
                            // clip's tail.
                            Logs.Warning(
                                $"VideoStages: Clip {previousClip.Id} boundary 'continue' flows into "
                                + $"sourced Clip {clip.Id}; treating the boundary as a cut.");
                            assembly.DegradeToCut(previousClip.Id, "target clip is sourced footage");
                        }
                        else
                        {
                            clipContext.ContinuityFrame = TryBuildContinuityFrame(
                                previousClip, previousClipOutput, clip, continuityWindow);
                            if (clipContext.ContinuityFrame is null)
                            {
                                assembly.DegradeToCut(previousClip.Id, "continuity input could not be built");
                            }
                        }
                    }
                }
                if (sourcedMedia is not null)
                {
                    // Per-clip refine: the conformed footage replaces root/generated media as the
                    // stage chain's input — stage 0 passes it through, later stages refine/upscale,
                    // a retake window regenerates part of it.
                    g.CurrentMedia = sourcedMedia;
                    if (clip.Stages.Count == 0)
                    {
                        // Every stage skipped: the footage itself is the clip's output.
                        if (parallelMultiClip)
                        {
                            RuntimeArtifact sourcedArtifact = CaptureStageInputArtifact(ArtifactOrigin.SourceVideo);
                            clipOutputs.Add(sourcedArtifact);
                            previousClipOutput = sourcedArtifact.Media.ToWGNodeData(g);
                        }
                        previousClip = clip;
                        clipIndex++;
                        continue;
                    }
                }

                StagePlan firstStage = plannedClip.Stages[0];
                _audioTimelineExecutor.ApplyControlNetClipLength(clip, plannedClip);
                // A sourced clip's "Native" audio is its own file's trimmed track, not the root's.
                ClipAudioRuntimeSources clipAudioSources =
                    sourcedMedia?.AttachedAudio is WGNodeData sourcedAudio
                        ? audioSources with { NativeAudio = sourcedAudio }
                        : audioSources;
                _audioTimelineExecutor.PrepareClipAudio(new(
                    clip,
                    firstStage,
                    plannedClip,
                    IsFirstClip: clipIndex == 0,
                    clipAudioSources));

                for (int clipStageIndex = 0; clipStageIndex < plannedClip.Stages.Count; clipStageIndex++)
                {
                    StagePlan plannedStage = plannedClip.Stages[clipStageIndex];
                    StageRefStore.StageRef guideRef = TryResolveGuideReference(plannedStage);
                    if (guideRef is null)
                    {
                        throw new SwarmUserErrorException(
                            $"VideoStages: Clip {clip.Id} stage {clipStageIndex} could not resolve "
                            + $"ImageReference '{plannedStage.Guide.RawValue}'.");
                    }

                    int sectionId = VideoStagesExtension.SectionIdForStage(plannedStage.StageId);
                    usedSectionIds.Add(sectionId);
                    PrepareStageOverrides(clipContext, plannedStage, sectionId);
                    RuntimeArtifact inputArtifact = clipArtifact ?? CaptureStageInputArtifact(
                        clip.SourceVideo is null ? ArtifactOrigin.HostRoot : ArtifactOrigin.SourceVideo);
                    inputArtifact.PublishTo(g);
                    clipArtifact = singleStageRunner.RunStage(
                        plannedStage,
                        sectionId,
                        guideRef,
                        store,
                        clipContext,
                        executionOptions);
                    CaptureStageOutput(plannedStage.StageId);
                    completedStageCount++;

                    if (completedStageCount < totalStageCount
                        && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
                        && !g.UserInput.Get(T2IParamTypes.DoNotSave, false))
                    {
                        g.CurrentMedia.SaveOutput(
                            g.CurrentVae,
                            g.CurrentAudioVae,
                            g.GetStableDynamicID(IntermediateStageSaveId, plannedStage.StageId));
                    }
                }

                if (parallelMultiClip)
                {
                    clipOutputs.Add(clipArtifact);
                    previousClipOutput = clipArtifact.Media.ToWGNodeData(g);
                }
                previousClip = clip;
                clipIndex++;
            }

            if (parallelMultiClip)
            {
                assembly.Assemble(clipOutputs);
            }
        }
        finally
        {
            foreach (int sectionId in usedSectionIds)
            {
                g.UserInput.SectionParamOverrides.Remove(sectionId);
            }
        }
    }

    /// <summary>
    /// Arms generation-time continuity for a "continue" boundary: extracts the previous clip's last
    /// <paramref name="window"/> rendered frames (the resolved overlap+1) so the next clip's first
    /// stage can freeze them as its opening latent context. Returns null (degrading the boundary to a
    /// cut) when the next clip can't consume it — a non-LTX-2 first stage, an explicit user
    /// first-frame ref, or an unknown/too-short previous frame count.
    /// </summary>
    private WGNodeData TryBuildContinuityFrame(
        ClipSpec previousClip, WGNodeData previousOutput, ClipSpec clip, int window)
    {
        if (clip.Stages.Count == 0 || !VideoStageModelCompat.IsLtxV2VideoModel(clip.Stages[0].Model))
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.Id} boundary 'continue' needs the next clip's first stage "
                + "on an LTX-2 model; treating the boundary as a cut.");
            return null;
        }
        foreach (ImageRefSpec reference in clip.ImageRefs)
        {
            if (!reference.FromEnd && reference.Frame == 1)
            {
                Logs.Warning(
                    $"VideoStages: Clip {clip.Id} has an explicit first-frame reference, which overrides the "
                    + $"incoming 'continue' boundary from clip {previousClip.Id}; treating the boundary as a cut.");
                return null;
            }
        }
        int? frames = previousOutput?.Frames ?? previousClip.Frames;
        if (previousOutput?.Path is not JArray previousOutputPath || frames is not int lastFrameCount || lastFrameCount <= 0)
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.Id} boundary 'continue' needs a known frame count for the "
                + "previous clip's output; treating the boundary as a cut.");
            return null;
        }
        if (window > lastFrameCount)
        {
            // The window was planned from spec frame counts; if the rendered output came up shorter, a
            // partial slice would desync generation from the merge plan — degrade to a cut instead.
            Logs.Warning(
                $"VideoStages: Clip {previousClip.Id} boundary 'continue' needs {window} overlap frames but "
                + $"its output has {lastFrameCount}; treating the boundary as a cut.");
            return null;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageFromBatchNode tailFrames = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: lastFrameCount - window,
            Length: window));
        tailFrames.Image.TryConnectFromPath(bridge, previousOutputPath);
        return new WGNodeData(WorkflowBridge.ToPath(tailFrames.IMAGE), g, WGNodeData.DT_IMAGE, previousOutput.Compat)
        {
            Width = previousOutput.Width,
            Height = previousOutput.Height,
            Frames = window
        };
    }

    private static IReadOnlyList<ClipPlan> RequirePlannedClips(
        VideoExecutionPlan plan,
        IReadOnlyList<ClipSpec> clips)
    {
        if (plan.Clips.Count == clips.Count
            && plan.Clips.Select((plannedClip, clipIndex) =>
                plannedClip.ClipId == clips[clipIndex].Id
                && plannedClip.Stages.Count == clips[clipIndex].Stages.Count
                && plannedClip.Stages.Select((plannedStage, stageIndex) =>
                    plannedStage.StageId == clips[clipIndex].Stages[stageIndex].Id).All(valid => valid))
                .All(valid => valid))
        {
            return plan.Clips;
        }

        throw new SwarmUserErrorException(
            "VideoStages: the LTX execution plan no longer matches the configured timeline. "
            + "Regenerate after updating the timeline.");
    }

    private RuntimeArtifact CaptureStageInputArtifact(ArtifactOrigin origin)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return RuntimeArtifact.Capture(g, bridge, origin);
    }

    private void CaptureGeneratedReference()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        store.Capture(StageRefStore.StageKind.Generated, referenceMedia, referenceVae);
    }

    private void CaptureStageOutput(int index)
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        StageRefStore.StageRef captured = new(referenceMedia, referenceVae);
        _stageOutputs[index] = captured;
        _previousStageRef = captured;
    }

    private void PrepareStageOverrides(
        ClipContext clipContext,
        StagePlan plannedStage,
        int sectionId)
    {
        string model = plannedStage.Core.Model;
        int steps = plannedStage.Core.Steps;
        double cfgScale = plannedStage.Core.CfgScale;
        string sampler = plannedStage.Core.Sampler;
        string scheduler = plannedStage.Core.Scheduler;
        ClipDimensionState dimensions = clipContext.Dimensions;
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        g.UserInput.SectionParamOverrides.Remove(sectionId);
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, model, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoSteps, steps, sectionId);
        g.UserInput.Set(T2IParamTypes.Steps, steps, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoCFG, cfgScale, sectionId);
        g.UserInput.Set(T2IParamTypes.CFGScale, cfgScale, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SamplerParam.Type, sampler, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SchedulerParam.Type, scheduler, sectionId);
        if (clipContext.Clip.Frames is int frames && frames > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFrames, frames, sectionId);
        }
        if (spec.FPS > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFPS, spec.FPS, sectionId);
        }
        if (dimensions.Width > 0)
        {
            g.UserInput.Set(T2IParamTypes.Width, dimensions.Width, sectionId);
        }
        if (dimensions.Height > 0)
        {
            g.UserInput.Set(T2IParamTypes.Height, dimensions.Height, sectionId);
        }
    }

    private StageRefStore.StageRef TryResolveGuideReference(StagePlan stage)
    {
        return stage.Guide.Kind switch
        {
            GuideReferenceKind.Base => WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists."),
            GuideReferenceKind.Refiner => WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists."),
            GuideReferenceKind.Generated => _previousStageRef ?? WarnIfMissing(
                store.Generated,
                "VideoStages: ImageReference 'Generated' requested, but no generated reference exists."),
            GuideReferenceKind.PreviousStage => ResolvePreviousStageReference(),
            GuideReferenceKind.ExplicitStage => ResolveExplicitStageReference(stage.Guide),
            GuideReferenceKind.Base2Edit => ResolveBase2EditReference(stage.Guide),
            _ => WarnUnknownGuideReference(stage.Guide.RawValue),
        };
    }

    private StageRefStore.StageRef ResolvePreviousStageReference()
    {
        if (_previousStageRef is null)
        {
            Logs.Warning("VideoStages: ImageReference 'PreviousStage' cannot be used for the first stage.");
        }
        return _previousStageRef;
    }

    private StageRefStore.StageRef ResolveExplicitStageReference(GuideReferencePlan guide)
    {
        int? stageIndex = guide.ReferencedStageIndex;
        if (stageIndex is int index && _stageOutputs.TryGetValue(index, out StageRefStore.StageRef reference))
        {
            return reference;
        }
        Logs.Warning(
            $"VideoStages: ImageReference '{guide.RawValue}' requested, but stage {stageIndex} does not exist.");
        return null;
    }

    private StageRefStore.StageRef ResolveBase2EditReference(GuideReferencePlan guide)
    {
        int? stageIndex = guide.ReferencedStageIndex;
        if (stageIndex is int index
            && base2EditPublishedStageRefs.TryGetStageRef(index, out StageRefStore.StageRef reference))
        {
            return reference;
        }
        Logs.Warning(
            $"VideoStages: ImageReference '{guide.RawValue}' requested, but Base2Edit stage {stageIndex} does not exist.");
        return null;
    }

    private static StageRefStore.StageRef WarnUnknownGuideReference(string rawValue)
    {
        Logs.Warning($"VideoStages: Unknown ImageReference value '{rawValue}'.");
        return null;
    }

    private static StageRefStore.StageRef WarnIfMissing(StageRefStore.StageRef r, string message)
    {
        if (r is null)
        {
            Logs.Warning(message);
        }
        return r;
    }

}
