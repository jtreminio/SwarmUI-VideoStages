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
    StageExecutionAdapter stageExecutionAdapter,
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
        bool rootStageHandoff = false,
        VideoExecutionPlan plan = null)
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        IReadOnlyList<ClipPlan> plannedClips = ResolvePlannedClips(plan, clips);
        bool sourcedLeadWithGeneratedClips = clips.Count > 0
            && clips[0].SourceVideo is not null
            && clips.Any(clip => clip.SourceVideo is null);
        // In a text-to-video run the generated clips replace the root with their own empty
        // latents and self-generate audio, so a sourced-lead run has NO consumer for the root
        // generation. Its audio latent must not become the replacement clips' audio init: that
        // reference pins the whole unrelated root sampler alive in the graph (a third sampler
        // generating footage nothing uses).
        bool dropTextToVideoRootDonor = sourcedLeadWithGeneratedClips && spec.IsTextToVideo;
        RunContext context = new()
        {
            NativeAudio = dropTextToVideoRootDonor
                ? null
                : nativeAudio ?? g.CurrentMedia?.AttachedAudio,
            ClipAudios = clipAudios,
            UploadedAudios = uploadedAudios,
            RootStageHandoff = rootStageHandoff
        };
        _stageOutputs.Clear();
        _previousStageRef = null;
        List<int> usedSectionIds = [];
        bool parallelMultiClip = clips.Count > 1;
        List<WGNodeData> clipParallelOutputs = [];
        HashSet<string> droppedRootNodeIds = [];
        try
        {
            if (context.RootStageHandoff)
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
                // keeps the root sampler alive. Remember the root's node ids for the final sweep.
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
                List<string> rootSeedIds = [];
                if (g.CurrentMedia?.Path is JArray rootMediaPath && rootMediaPath.Count == 2)
                {
                    rootSeedIds.Add($"{rootMediaPath[0]}");
                }
                if (g.CurrentMedia?.AttachedAudio?.Path is JArray rootAudioPath
                    && rootAudioPath.Count == 2)
                {
                    rootSeedIds.Add($"{rootAudioPath[0]}");
                }
                if (rootSeedIds.Count > 0)
                {
                    // Capture the component NOW: the per-clip root-replacement cleanups delete
                    // the seed nodes themselves, and a sweep walked from already-deleted ids
                    // would never reach the survivors they used to pin.
                    using WorkflowBridge bridge = BridgeSync.For(g);
                    droppedRootNodeIds.UnionWith(
                        WorkflowGraphCleanup.CollectComponentIds(bridge, rootSeedIds));
                }
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
            int totalStageCount = TotalStageCount(clips);
            bool publishIntermediateStages =
                totalStageCount > 1
                && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
                && !g.UserInput.Get(T2IParamTypes.DoNotSave, false);
            if (parallelMultiClip || publishIntermediateStages)
            {
                // Dedicated decode branches are also required for intermediate publications:
                // otherwise the next LTX stage rewires the same native decode chain in place and
                // every earlier save silently advances to the final stage artifact.
                g.NodeHelpers[MultiClipParallelMerger.NodeHelperKey] = "1";
            }

            List<string> effectiveBoundaryOuts = [.. clips.Select(clip => clip.BoundaryOut)];
            int[] boundaryOverlapPrefs = [.. clips.Select(clip => clip.BoundaryOutOverlap)];
            int[] continueWindows = MultiClipParallelMerger.ResolveContinueWindows(
                [.. clips.Select(clip => clip.Frames)],
                effectiveBoundaryOuts,
                boundaryOverlapPrefs);
            ClipSpec previousClip = null;
            WGNodeData previousClipOutput = null;
            SourcedClipInstaller sourcedClipInstaller = new(g);
            int clipIndex = 0;
            int completedStageCount = 0;
            foreach (ClipSpec clip in clips)
            {
                ClipContext clipContext = new(clip, spec.Width, spec.Height, rootSourceMedia, rootSourceVae);
                RuntimeArtifact clipArtifact = null;
                ClipPlan plannedClip = plannedClips is null ? null : plannedClips[clipIndex];
                WGNodeData sourcedMedia = null;
                if (clip.SourceVideo is not null)
                {
                    sourcedMedia = sourcedClipInstaller.TryInstall(clip, spec);
                    if (sourcedMedia is null)
                    {
                        Logs.Error(
                            $"VideoStages: Clip {clip.Id} source video could not be installed. "
                            + "Stopping further stages.");
                        break;
                    }
                }
                if (parallelMultiClip && previousClip is not null)
                {
                    if (sourcedMedia is null)
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
                    }

                    if (StringUtils.Equals(previousClip.BoundaryOut, Constants.BoundaryOutContinue))
                    {
                        if (sourcedMedia is not null)
                        {
                            // The sourced clip's opening frames are fixed footage passed through by
                            // its first stage — there is no generation to condition on the previous
                            // clip's tail.
                            Logs.Warning(
                                $"VideoStages: Clip {previousClip.Id} boundary 'continue' flows into "
                                + $"sourced Clip {clip.Id}; treating the boundary as a cut.");
                            effectiveBoundaryOuts[clipIndex - 1] = Constants.BoundaryOutCut;
                        }
                        else
                        {
                            clipContext.ContinuityFrame = TryBuildContinuityFrame(
                                previousClip, previousClipOutput, clip, continueWindows[clipIndex - 1]);
                            if (clipContext.ContinuityFrame is null)
                            {
                                effectiveBoundaryOuts[clipIndex - 1] = Constants.BoundaryOutCut;
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
                            clipParallelOutputs.Add(sourcedMedia);
                            previousClipOutput = sourcedMedia;
                        }
                        else
                        {
                            RetargetRootSavesToSourcedOutput(rootSourceMedia, sourcedMedia);
                        }
                        previousClip = clip;
                        clipIndex++;
                        continue;
                    }
                }

                StageSpec firstStage = clip.Stages[0];
                ApplyControlNetClipLengthIfApplicable(clip, firstStage);
                // A sourced clip's "Native" audio is its own file's trimmed track, not the root's.
                RunContext clipRunContext = sourcedMedia?.AttachedAudio is WGNodeData sourcedAudio
                    ? new RunContext
                    {
                        NativeAudio = sourcedAudio,
                        ClipAudios = context.ClipAudios,
                        UploadedAudios = context.UploadedAudios,
                        RootStageHandoff = context.RootStageHandoff
                    }
                    : context;
                PrepareClipAudio(clip, firstStage, clipRunContext, isFirstClip: clipIndex == 0);

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
                    StagePlan plannedStage = plannedClip is null ? null : plannedClip.Stages[clipStageIndex];
                    if (plannedStage is not null)
                    {
                        RuntimeArtifact inputArtifact = clipArtifact ?? CaptureStageInputArtifact(
                            clip.SourceVideo is null ? ArtifactOrigin.HostRoot : ArtifactOrigin.SourceVideo);
                        clipArtifact = stageExecutionAdapter.Execute(
                            plannedStage,
                            sectionId,
                            new StageExecutionAdapterContext(
                                guideRef,
                                store,
                                clipContext,
                                parallelMultiClip,
                                clips.Count,
                                clipIndex,
                                clipStageIndex),
                            inputArtifact);
                    }
                    else
                    {
                        // Non-LTX and plan-mismatch paths retain the exact historical invocation.
                        singleStageRunner.RunStage(stage, sectionId, guideRef, store, clipContext);
                    }
                    CaptureStageOutput(stage.Id);
                    clipStageIndex++;
                    completedStageCount++;

                    if (completedStageCount < totalStageCount
                        && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
                        && !g.UserInput.Get(T2IParamTypes.DoNotSave, false))
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
                else if (sourcedMedia is not null)
                {
                    // A sourced clip never absorbs the root generation (its stages refine its own
                    // footage), so the root's save still points at the unrelated root output.
                    RetargetRootSavesToSourcedOutput(rootSourceMedia, g.CurrentMedia);
                }
                previousClip = clip;
                clipIndex++;
            }

            if (parallelMultiClip && clipParallelOutputs.Count > 1)
            {
                // clips and clipParallelOutputs are index-aligned (one output per active clip), so BoundaryOut
                // zips by index. "continue" entries survive only where continuity conditioning was actually
                // armed above (else the boundary was degraded to "cut").
                multiClipParallelMerger.Apply(
                    clipParallelOutputs,
                    rootSourceMedia,
                    effectiveBoundaryOuts,
                    continueWindows,
                    boundaryOverlapPrefs);
            }

            if (droppedRootNodeIds.Count > 0)
            {
                // The dropped t2v root generation still lingers when dead consumers hang off it
                // (its audio-decode sibling, transient detached chains) — the root-replacement
                // cleanup only walks upstream, so those pin the root sampler alive. Same
                // bidirectional sweep as the lone-sourced retarget path.
                SweepDroppedTextToVideoRoot(droppedRootNodeIds);
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
    /// A lone sourced clip has no merge step to retarget the core root generation's save, so point
    /// any save consuming the root output at the sourced clip's result (the raw footage, or its
    /// refined stage output) — otherwise the run would emit the unrelated root video alongside
    /// (and execute a generation nobody consumes).
    /// </summary>
    private void RetargetRootSavesToSourcedOutput(WGNodeData rootSourceMedia, WGNodeData sourced)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput rootOutput = bridge.ResolvePath(rootSourceMedia?.Path);
        INodeOutput images = bridge.ResolvePath(sourced.Path);
        if (rootOutput is null || images is null)
        {
            return;
        }
        INodeOutput audio = bridge.ResolvePath(sourced.AttachedAudio?.Path);
        HashSet<string> staleAudioNodeIds = [];
        SaveAnimationRetargeter.Retarget(
            bridge,
            save => save.Images.Connection is INodeOutput existing
                && existing.Node.Id == rootOutput.Node.Id
                && existing.SlotIndex == rootOutput.SlotIndex
                && OutputRegistry.CanAdvanceFinalHostSave(g, save.Id),
            images,
            audio,
            retargetAudio: true,
            save =>
            {
                if (save.Audio.Connection is INodeOutput oldAudioOutput)
                {
                    staleAudioNodeIds.Add(oldAudioOutput.Node.Id);
                }
            });

        // The root generation (sampler, empty latents, conditioning) is now consumed by nothing the
        // saves depend on — sweep its whole dead component so the workflow doesn't carry a dangling
        // chain. A bidirectional sweep (not an upstream walk) is required: dead consumers hanging
        // off the root — its audio-decode sibling, transient detached guide chains — would
        // otherwise pin the sampler alive past the host's cleanup. Live = anything a save (or the
        // sourced result) depends on, so shared loaders survive.
        HashSet<string> liveRoots = [images.Node.Id];
        if (audio is not null)
        {
            liveRoots.Add(audio.Node.Id);
        }
        liveRoots.UnionWith(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Select(save => save.Id));
        HashSet<string> deadStarts = [rootOutput.Node.Id, .. staleAudioNodeIds];
        if (bridge.ResolvePath(rootSourceMedia.AttachedAudio?.Path) is INodeOutput rootAudio)
        {
            deadStarts.Add(rootAudio.Node.Id);
        }
        WorkflowGraphCleanup.RemoveDeadComponentAround(bridge, deadStarts, liveRoots, g.NodeHelpers);
    }

    /// <summary>
    /// Sweeps the dropped text-to-video root generation's whole dead component (the ids were
    /// captured before any pruning, so already-removed nodes are skipped). Live = anything a save
    /// or the current media/audio depends on, so shared loaders survive.
    /// </summary>
    private void SweepDroppedTextToVideoRoot(IEnumerable<string> rootNodeIds)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        HashSet<string> liveRoots =
            [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Select(save => save.Id)];
        if (g.CurrentMedia?.Path is JArray currentPath && currentPath.Count == 2)
        {
            liveRoots.Add($"{currentPath[0]}");
        }
        if (g.CurrentMedia?.AttachedAudio?.Path is JArray audioPath && audioPath.Count == 2)
        {
            liveRoots.Add($"{audioPath[0]}");
        }
        WorkflowGraphCleanup.RemoveDeadComponentAround(bridge, rootNodeIds, liveRoots, g.NodeHelpers);
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

    private static int TotalStageCount(IReadOnlyList<ClipSpec> clips)
    {
        int total = 0;
        foreach (ClipSpec clip in clips)
        {
            total += clip.Stages.Count;
        }
        return total;
    }

    internal static IReadOnlyList<ClipPlan> ResolvePlannedClips(
        VideoExecutionPlan plan,
        IReadOnlyList<ClipSpec> clips)
    {
        if (plan is null)
        {
            return null;
        }

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

        Logs.Warning(
            "VideoStages: LTX execution plan did not match the parsed stage sequence; "
                + "falling back to the legacy stage runner for this workflow.");
        return null;
    }

    private RuntimeArtifact CaptureStageInputArtifact(ArtifactOrigin origin)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return RuntimeArtifact.Capture(g, bridge, origin);
    }

    private void PrepareClipAudio(ClipSpec clip, StageSpec stage, RunContext context, bool isFirstClip)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootStageHandoff
            && rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(stage, clip);
        WGNodeData clipAudio = ClipAudioWorkflowHelper.ResolveClipAudio(
            clip.Id,
            clip.AudioSource,
            context.NativeAudio,
            context.ClipAudios,
            context.UploadedAudios,
            suppressNative,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        // Overlay any per-clip audio segments onto the resolved base audio, BEFORE the cross-clip merge so
        // boundary trims apply to the combined result.
        int? clipFps = g.CurrentMedia?.GetRawFPS();
        double clipDurationSeconds = clip.Frames is int f && clipFps is int fps && fps > 0
            ? (double)f / fps
            : 0;
        WGNodeData combinedAudio = new AudioSegmentCombiner(g).Combine(
            clip,
            clipAudio,
            clipDurationSeconds,
            out IReadOnlyList<(double Start, double End)> segmentWindows);

        InjectClipConditioningAudio(
            clip,
            context,
            isFirstClip,
            currentMedia,
            clipAudio,
            combinedAudio,
            clipDurationSeconds,
            segmentWindows);
    }

    /// <summary>
    /// The audio-injection decision tree of <see cref="PrepareClipAudio"/>: decides how the clip's
    /// combined audio conditions generation (injected sampling latent, preserve-windowed attachment, or
    /// plain attachment), publishes the media, then runs the base-track injections that bake segments
    /// into fully-preserved conditioning audio.
    /// </summary>
    private void InjectClipConditioningAudio(
        ClipSpec clip,
        RunContext context,
        bool isFirstClip,
        WGNodeData currentMedia,
        WGNodeData clipAudio,
        WGNodeData combinedAudio,
        double clipDurationSeconds,
        IReadOnlyList<(double Start, double End)> segmentWindows)
    {
        // Segment conditioning needs a known positive clip duration so the combiner built a
        // full-clip-length silent bed; a bed-less (shorter-than-clip) track wired into the AV concat
        // would mismatch the video latent's length.
        bool segmentsOverNoBase = segmentWindows.Count > 0
            && clipAudio is null
            && clipDurationSeconds > 0;

        // Segments over NO locked base track: inject the combined audio (silent bed + segments) as the
        // sampling audio latent, preserving only the segment windows so the model generates the gaps and
        // the video attends to the locked segment audio (matches LTX Director's audio-inpaint design).
        // First clip only — the injectable (empty-audio) AV concat in the graph belongs to the root
        // generation, which is the first clip's; later clips build their own AV latents at sample time.
        bool segmentsConditionGeneration = isFirstClip
            && segmentsOverNoBase
            && ltxManager.TryInjectAudio(
                combinedAudio,
                matchVideoLengthToAudio: false,
                preserveWindows: segmentWindows);

        if (segmentsConditionGeneration)
        {
            // The sampled audio (segments + generated gaps) is the mux source; clear any stale attached
            // track (e.g. a native route from the replaced root stage) so it cannot override it.
            currentMedia.AttachedAudio = null;
        }
        else if (segmentsOverNoBase
            && ltxManager.TryBuildPreserveWindowedAudioLatent(
                combinedAudio, segmentWindows, stableIdSlot: clip.Id + 1) is WGNodeData windowedLatent)
        {
            // No injectable concat for this clip (later clip, or a flow without one): attach the
            // pre-encoded windowed latent instead of the raw combined audio. AsSamplingLatent concats a
            // latent attachment as-is, keeping the preserve-windows mask — raw audio would be baked
            // fully-preserved, locking the silent-bed gaps instead of letting the model generate them.
            currentMedia.AttachedAudio = windowedLatent;
        }
        else
        {
            currentMedia.AttachedAudio = combinedAudio;
        }
        g.CurrentMedia = currentMedia;

        bool uploadInjectPath = context.RootStageHandoff
            && ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                clip.AudioSource,
                clip.ClipLengthFromAudio,
                restrictLengthMatchToUploadOrAce: true);
        if (uploadInjectPath && clipAudio is not null)
        {
            // A locked base track (upload / AceStepFun): inject the combined audio so segments are baked
            // into the fully-preserved conditioning audio and the video reacts to them too. With no
            // segments, Combine returned the base by reference, so this is the plain base injection.
            _ = ltxManager.TryInjectAudio(combinedAudio);
        }
        else if (!context.RootStageHandoff
            && isFirstClip
            && segmentWindows.Count > 0
            && clipAudio is not null)
        {
            // Non-handoff flows: VideoStagesCoordinator normally injects the first clip's base audio,
            // but it defers to us when the clip has segments so the combined track (segments baked in)
            // conditions generation here, with the coordinator's match-length semantics.
            _ = ltxManager.TryInjectAudio(
                combinedAudio,
                ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                    clip.AudioSource,
                    clip.ClipLengthFromAudio,
                    restrictLengthMatchToUploadOrAce: false));
        }
    }

    private void ApplyControlNetClipLengthIfApplicable(ClipSpec clip, StageSpec stage)
    {
        if (clip.ClipLengthFromControlNet && VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))
        {
            _ = ltxManager.TryApplyControlNetFrameCount(clip.PrimarySlotEntry?.Source);
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
