using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class StageSequenceRunner(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner singleStageRunner,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs,
    RootVideoStageHandoff rootVideoStageHandoff,
    RootVideoStageResizer rootVideoStageResizer,
    MultiClipParallelMerger multiClipParallelMerger,
    LtxManager ltxManager)
{
    private const int IntermediateStageSaveId = 52100;

    private readonly Dictionary<int, StageRefStore.StageRef> _stageOutputs = [];
    private StageRefStore.StageRef _previousStageRef;

    private sealed class RunContext
    {
        public WGNodeData NativeAudio { get; init; }
        public IReadOnlyDictionary<int, WGNodeData> ClipAudios { get; init; }
        public IReadOnlyDictionary<int, WGNodeData> UploadedAudios { get; init; }
        public bool RootStageHandoff { get; init; }
    }

    public void Run(
        IReadOnlyList<ClipSpec> clips,
        WGNodeData nativeAudio = null,
        IReadOnlyDictionary<int, WGNodeData> clipAudios = null,
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios = null,
        bool rootStageHandoff = false)
    {
        RunContext context = new()
        {
            NativeAudio = nativeAudio ?? g.CurrentMedia?.AttachedAudio,
            ClipAudios = clipAudios,
            UploadedAudios = uploadedAudios,
            RootStageHandoff = rootStageHandoff
        };
        _stageOutputs.Clear();
        _previousStageRef = null;
        List<int> usedSectionIds = [];
        bool parallelMultiClip = clips.Count > 1;
        List<WGNodeData> clipParallelOutputs = [];
        try
        {
            if (context.RootStageHandoff)
            {
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            }
            CaptureGeneratedReference();
            WGNodeData rootSourceMedia = g.CurrentMedia?.Duplicate();
            WGNodeData rootSourceVae = g.CurrentVae?.Duplicate();
            if (parallelMultiClip)
            {
                g.NodeHelpers[MultiClipParallelMerger.NodeHelperKey] = "1";
            }

            int totalStageCount = TotalStageCount(clips);
            VideoStagesSpec spec = g.GetVideoStagesSpec();
            List<string> effectiveBoundaryOuts = [.. clips.Select(clip => clip.BoundaryOut)];
            ClipSpec previousClip = null;
            WGNodeData previousClipOutput = null;
            int clipIndex = 0;
            foreach (ClipSpec clip in clips)
            {
                ClipContext clipContext = new(clip, spec.Width, spec.Height, rootSourceMedia, rootSourceVae);
                if (parallelMultiClip && previousClip is not null)
                {
                    if (clipContext.SourceMedia is null)
                    {
                        Logs.Error(
                            "VideoStages: parallel clips require root media before the first stage. "
                            + "Stopping further stages.");
                        break;
                    }

                    g.CurrentMedia = clipContext.SourceMedia.Duplicate();
                    if (clipContext.SourceVae is not null)
                    {
                        g.CurrentVae = clipContext.SourceVae.Duplicate();
                    }

                    if (StringUtils.Equals(previousClip.BoundaryOut, Constants.BoundaryOutContinue))
                    {
                        clipContext.ContinuityFrame = TryBuildContinuityFrame(previousClip, previousClipOutput, clip);
                        if (clipContext.ContinuityFrame is null)
                        {
                            effectiveBoundaryOuts[clipIndex - 1] = Constants.BoundaryOutCut;
                        }
                    }
                }

                StageSpec firstStage = clip.Stages[0];
                ApplyControlNetClipLengthIfApplicable(clip, firstStage);
                PrepareClipAudio(clip, firstStage, context);

                int clipStageIndex = 0;
                foreach (StageSpec stage in clip.Stages)
                {
                    StageRefStore.StageRef guideRef = TryResolveGuideReference(stage);
                    if (guideRef is null)
                    {
                        throw new SwarmUserErrorException(
                            $"VideoStages: Clip {clip.Id} stage {clipStageIndex} could not resolve "
                            + $"ImageReference '{stage.ImageReference}'.");
                    }

                    int sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                    usedSectionIds.Add(sectionId);
                    PrepareStageOverrides(clipContext, stage, sectionId);
                    singleStageRunner.RunStage(stage, sectionId, guideRef, store, clipContext);
                    CaptureStageOutput(stage.Id);
                    clipStageIndex++;

                    if (stage.Id < totalStageCount - 1
                        && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false))
                    {
                        g.CurrentMedia.SaveOutput(
                            g.CurrentVae,
                            g.CurrentAudioVae,
                            g.GetStableDynamicID(IntermediateStageSaveId, stage.Id));
                    }
                }

                if (parallelMultiClip)
                {
                    clipParallelOutputs.Add(g.CurrentMedia.Duplicate());
                    previousClipOutput = clipParallelOutputs[^1];
                }
                previousClip = clip;
                clipIndex++;
            }

            if (parallelMultiClip && clipParallelOutputs.Count > 1)
            {
                // clips and clipParallelOutputs are index-aligned (one output per active clip), so BoundaryOut
                // zips by index. "continue" entries survive only where continuity conditioning was actually
                // armed above (else the boundary was degraded to "cut").
                multiClipParallelMerger.Apply(clipParallelOutputs, rootSourceMedia, effectiveBoundaryOuts);
            }
        }
        finally
        {
            if (parallelMultiClip)
            {
                _ = g.NodeHelpers.Remove(MultiClipParallelMerger.NodeHelperKey);
            }

            foreach (int sectionId in usedSectionIds)
            {
                g.UserInput.SectionParamOverrides.Remove(sectionId);
            }
        }
    }

    /// <summary>
    /// Arms generation-time continuity for a "continue" boundary: extracts the previous clip's final
    /// rendered frame so the next clip's first stage can use it as a first-frame guide. Returns null
    /// (degrading the boundary to a cut) when the next clip can't consume it — a non-LTX-2 first stage,
    /// an explicit user first-frame ref, or an unknown previous frame count.
    /// </summary>
    private WGNodeData TryBuildContinuityFrame(ClipSpec previousClip, WGNodeData previousOutput, ClipSpec clip)
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

        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageFromBatchNode lastFrame = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: lastFrameCount - 1,
            Length: 1));
        lastFrame.Image.TryConnectFromPath(bridge, previousOutputPath);
        return new WGNodeData(WorkflowBridge.ToPath(lastFrame.IMAGE), g, WGNodeData.DT_IMAGE, previousOutput.Compat)
        {
            Width = previousOutput.Width,
            Height = previousOutput.Height,
            Frames = 1
        };
    }

    private static int TotalStageCount(IReadOnlyList<ClipSpec> clips)
    {
        int total = 0;
        foreach (ClipSpec clip in clips)
        {
            total += clip.Stages.Count;
        }
        return total;
    }

    private void PrepareClipAudio(ClipSpec clip, StageSpec stage, RunContext context)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootStageHandoff
            && rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(stage);
        WGNodeData clipAudio = ClipAudioWorkflowHelper.ResolveClipAudio(
            clip.Id,
            clip.AudioSource,
            context.NativeAudio,
            context.ClipAudios,
            context.UploadedAudios,
            suppressNative,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        // Overlay any per-clip audio segments onto the resolved base audio, BEFORE the cross-clip merge so
        // boundary trims apply to the combined result. Generation-time audio conditioning still uses the
        // pure base audio (segments are post-hoc overlay pieces, not a generation driver).
        int? clipFps = g.CurrentMedia?.GetRawFPS();
        double clipDurationSeconds = clip.Frames is int f && clipFps is int fps && fps > 0
            ? (double)f / fps
            : 0;
        WGNodeData combinedAudio = new AudioSegmentCombiner(g).Combine(clip, clipAudio, clipDurationSeconds);
        currentMedia.AttachedAudio = combinedAudio;
        g.CurrentMedia = currentMedia;
        if (context.RootStageHandoff
            && ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                clip.AudioSource,
                clip.ClipLengthFromAudio,
                restrictLengthMatchToUploadOrAce: true))
        {
            _ = ltxManager.TryInjectAudio(clipAudio);
        }
    }

    private void ApplyControlNetClipLengthIfApplicable(ClipSpec clip, StageSpec stage)
    {
        if (clip.ClipLengthFromControlNet && VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))
        {
            _ = ltxManager.TryApplyControlNetFrameCount(clip.ControlNetSource);
        }
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

    private void PrepareStageOverrides(ClipContext clipContext, StageSpec stage, int sectionId)
    {
        ClipDimensionState dimensions = clipContext.Dimensions;
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        g.UserInput.SectionParamOverrides.Remove(sectionId);
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, stage.Model, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoSteps, stage.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.Steps, stage.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoCFG, stage.CfgScale, sectionId);
        g.UserInput.Set(T2IParamTypes.CFGScale, stage.CfgScale, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SamplerParam.Type, stage.Sampler, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SchedulerParam.Type, stage.Scheduler, sectionId);
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

    private StageRefStore.StageRef TryResolveGuideReference(StageSpec stage)
    {
        if (StringUtils.Equals(stage.ImageReference, "Base"))
        {
            return WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "Refiner"))
        {
            return WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "Generated"))
        {
            if (_previousStageRef is not null)
            {
                return _previousStageRef;
            }
            return WarnIfMissing(
                store.Generated,
                "VideoStages: ImageReference 'Generated' requested, but no generated reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "PreviousStage"))
        {
            if (_previousStageRef is null)
            {
                Logs.Warning(
                    "VideoStages: ImageReference 'PreviousStage' cannot be used for the first stage.");
                return null;
            }
            return _previousStageRef;
        }
        if (ImageReference.TryParseExplicitStageIndex(stage.ImageReference, out int explicitStage))
        {
            if (!_stageOutputs.TryGetValue(explicitStage, out StageRefStore.StageRef explicitRef))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference '{stage.ImageReference}' requested, but stage {explicitStage} "
                    + "does not exist.");
                return null;
            }
            return explicitRef;
        }
        if (ImageReference.TryParseBase2EditStageIndex(stage.ImageReference, out int editStage))
        {
            if (!base2EditPublishedStageRefs.TryGetStageRef(editStage, out StageRefStore.StageRef publishedEditRef))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference '{stage.ImageReference}' requested, but Base2Edit stage "
                    + $"{editStage} does not exist.");
                return null;
            }
            return publishedEditRef;
        }
        Logs.Warning($"VideoStages: Unknown ImageReference value '{stage.ImageReference}'.");
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
