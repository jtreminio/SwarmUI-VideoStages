using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal sealed class VideoArchitectureExecutionHost
{
    private readonly WorkflowGenerator _generator;
    private readonly VideoExecutionPlan _plan;
    private readonly IReadOnlyDictionary<
        ArchitectureId,
        IArchitectureGenerationSessionProvider> _providers;
    private readonly IReadOnlyList<IArchitectureGenerationSessionProvider>
        _activeProviders;
    private readonly ArchitectureId? _rootOwner;
    private RootSnapshot _rootSnapshot;

    internal T2IParamInput RequestInput => _generator.UserInput;
    internal VideoExecutionPlan Plan => _plan;

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan) : this(
        generator,
        plan,
        CreateProductionProviderBinding(generator, plan))
    {
    }

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionProvider> providers) : this(
        generator,
        plan,
        CreateInjectedProviderBinding(plan, providers))
    {
    }

    private VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        RuntimeProviderBinding binding)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Dictionary<ArchitectureId, IArchitectureGenerationSessionProvider> byId = [];
        foreach (IArchitectureGenerationSessionProvider provider in binding.Providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!byId.TryAdd(provider.ArchitectureId, provider))
            {
                throw Invariant.Failure(
                    $"Duplicate generation runtime provider for architecture "
                    + $"'{provider.ArchitectureId}'.");
            }
        }
        _providers = byId;
        _activeProviders = ResolveActiveProviders(binding.ActiveArchitectureIds, byId);
        _rootOwner = binding.RootOwnerArchitectureId;
    }

    /// <summary>
    /// Runs request preflight before any VideoStages workflow phase mutates the host graph.
    /// </summary>
    internal IReadOnlyList<PlanDiagnostic> CollectPreflightDiagnostics()
    {
        UploadedMediaPreflight media = new(_generator.UserInput);
        List<PlanDiagnostic> diagnostics = [
            .. new TimelineFrameInterpolator(_generator).Preflight(_plan),
            .. media.Preflight(_plan),
            .. NativeFrameReferences.PreflightUploads(media, _plan)
        ];
        ArchitectureRequestPreflightContext context = new(_plan, _rootOwner);
        foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
        {
            diagnostics.AddRange(provider.PreflightRequest(context));
        }
        return diagnostics.AsReadOnly();
    }

    internal void CaptureControlNetPreprocessors()
    {
        new ControlNetCoreMediaCapture(_generator).Capture();
        foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
        {
            provider.CaptureControlNetPreprocessors(provider.ArchitectureId == _rootOwner);
        }
    }

    internal void CaptureBaseReference()
    {
        foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
        {
            provider.CaptureBaseReference(_plan);
        }
    }

    internal void CaptureRefinerReference()
    {
        foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
        {
            provider.CaptureRefinerReference(_plan);
        }
    }

    internal void CapturePreCoreMedia() => CapturePreCoreMediaCore();

    internal void DropCoreOutput() => DropCoreOutputCore();

    internal void ApplyRootAudioMaskDimensions() =>
        RootOwnerProvider()?.ApplyRootAudioMaskDimensions();

    internal void RunConfiguredStages()
    {
        if (_plan.Clips.Count == 0)
        {
            return;
        }
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(_generator, _plan);
        MultiClipParallelMerger merger = new(_generator);
        AudioRuntimeSources preparedAudioSources = new AudioRuntimeSourceResolver(
            _generator,
            new AudioHandler(_generator)).Resolve(_plan);

        _generator.LastID = Math.Max(
            _generator.LastID,
            Constants.StagedNodeIdReservationFloor);
        TimelineAssemblySession assembly = new(_generator, merger, _plan);
        ArchitectureTimelineSessionContext sessionContext = new(
            _plan,
            preparedAudioSources,
            assembly)
        {
            RootAdoption = new HostRootAdoption(
                _generator,
                _plan.Root,
                rootSession.OwnedRootComponentIds),
        };
        RuntimeArtifact finalArtifact;
        Dictionary<ArchitectureId, IVideoGenerationSession> sessions = [];
        bool sessionConstructionCompleted = false;
        try
        {
            foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
            {
                IVideoGenerationSession session = provider.CreateSession(
                    sessionContext with
                    {
                        OwnsGeneratedRoot = _rootOwner == provider.ArchitectureId,
                    });
                ArgumentNullException.ThrowIfNull(session);
                if (session.ArchitectureId != provider.ArchitectureId)
                {
                    TryDispose(session);
                    throw Invariant.Failure(
                        $"Generation runtime provider for architecture "
                            + $"'{provider.ArchitectureId}' created a session for architecture "
                            + $"'{session.ArchitectureId}'.");
                }
                sessions.Add(provider.ArchitectureId, session);
            }
            sessionConstructionCompleted = true;
            ClipPlan previousClip = null;
            DecodedClipArtifact previousClipOutput = null;
            List<DecodedClipArtifact> clipOutputs = [];
            for (int clipIndex = 0; clipIndex < _plan.Clips.Count; clipIndex++)
            {
                ClipPlan plannedClip = _plan.Clips[clipIndex];
                bool exposesPrevious = clipIndex > 0
                    && _plan.Boundaries[clipIndex - 1].Effective != BoundaryJoinType.Cut
                    && previousClip?.Architecture.Id == plannedClip.Architecture.Id;
                ArchitectureClipRuntimeContext runtimeContext = new(
                    plannedClip,
                    clipIndex,
                    PreviousClip: exposesPrevious ? previousClip : null,
                    PreviousClipOutput: exposesPrevious ? previousClipOutput : null,
                    PreviousTimelineClipOutput: clipIndex > 0 ? previousClipOutput : null);
                ArchitectureId architectureId = plannedClip.Architecture?.Id
                    ?? throw Invariant.Failure(
                        $"Clip {plannedClip.ClipId} has no architecture identity.");
                if (!sessions.TryGetValue(
                        architectureId,
                        out IVideoGenerationSession session))
                {
                    throw Invariant.Failure(
                        $"No runtime session is registered for architecture "
                            + $"'{architectureId}'.");
                }
                DecodedClipArtifact output = session.Execute(runtimeContext)
                    ?? throw Invariant.Failure(
                        $"Architecture '{session.ArchitectureId}' returned no decoded clip "
                            + "artifact.");
                ValidateOutput(output, session, runtimeContext);
                output = DecodedTimelineAudioOverlay.Apply(
                    _generator,
                    output,
                    plannedClip);
                clipOutputs.Add(output);
                previousClipOutput = output;
                previousClip = plannedClip;
            }

            finalArtifact = _plan.Clips.Count > 1
                ? assembly.Assemble(clipOutputs)
                : assembly.FinalizeSingleClip(clipOutputs[0]);
        }
        finally
        {
            foreach (IVideoGenerationSession session in sessions.Values)
            {
                if (sessionConstructionCompleted)
                {
                    session.Dispose();
                }
                else
                {
                    TryDispose(session);
                }
            }
        }
        finalArtifact = new TimelineFrameInterpolator(_generator).Apply(
            finalArtifact,
            _plan);
        if (finalArtifact.Media is not null)
        {
            finalArtifact.Media.Compat = null;
            if (finalArtifact.Media.AttachedAudio is not null)
            {
                finalArtifact.Media.AttachedAudio.Compat = null;
            }
        }
        finalArtifact.PublishTo(_generator);
        rootSession.PublishTimeline(finalArtifact);
        // Recorded, not acted on. Adoption is meant to leave this empty; a non-empty set says the
        // timeline built beside one of the host's nodes rather than on it, which is a regression
        // nothing else in the graph would show — the shipped workflow looks the same either way.
        VideoGraphHelpers.CacheRemovalRecord(
            _generator,
            Constants.SweptHostRootNodesKey,
            rootSession.DisplacedRootRemovals);
        CollapseDuplicateNodes();
    }

    /// <summary>
    /// Folds together nodes the timeline built more than once. A clip repeating the previous clip's
    /// prompt encodes it twice otherwise, and a stage repeating the previous stage's IC-LoRA builds
    /// the whole loader and guide chain again; neither is cheap, and both compute the same thing.
    /// <para>
    /// Runs last, once the graph is final. Merging a node invalidates any path still naming it, and
    /// the passes that rewrite a node in place for one stage — frame-count connection, the host
    /// clamp widening — have all had their turn by now, so nothing can mutate a node another stage
    /// has since been folded onto.
    /// </para>
    /// <para>
    /// Only a node something reads may be folded. One that nothing reads is there for its effect
    /// rather than for a value — a save, a preview — and two of those are two outputs the request
    /// asked for, not one computed twice; <c>outputintermediateimages</c> is exactly that case.
    /// </para>
    /// </summary>
    private void CollapseDuplicateNodes()
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(
            bridge,
            mergeable: node => node.Outputs.Any(
                output => bridge.Graph.FindInputsConnectedTo(output).Any()));
        if (merged.Count == 0)
        {
            return;
        }
        // Every tracker, not only the media: core's own later steps read the VAEs off the generator
        // to attach audio and to save, and a tracker naming a folded node would reach a node the
        // workflow no longer has.
        foreach (WGNodeData tracked in new[]
        {
            _generator.CurrentMedia,
            _generator.CurrentMedia?.AttachedAudio,
            _generator.CurrentModel,
            _generator.CurrentTextEnc,
            _generator.CurrentVae,
            _generator.CurrentAudioVae,
            _generator.BasicInputImage,
        })
        {
            Repoint(tracked?.Path, merged);
        }
        foreach (JArray tracked in new[]
        {
            _generator.FinalMask,
            _generator.FinalPrompt,
            _generator.FinalNegativePrompt,
            _generator.FinalTrimLatent,
            _generator.LoadingModel,
            _generator.LoadingClip,
            _generator.LoadingVAE,
        })
        {
            Repoint(tracked, merged);
        }
        VideoGraphHelpers.InvalidateForRemovedNodes(_generator.NodeHelpers, [.. merged.Keys]);
    }

    private static void Repoint(JArray path, IReadOnlyDictionary<string, string> merged)
    {
        if (path is { Count: 2 } && merged.TryGetValue($"{path[0]}", out string survivor))
        {
            path[0] = survivor;
        }
    }

    private static void ValidateOutput(
        DecodedClipArtifact output,
        IVideoGenerationSession session,
        ArchitectureClipRuntimeContext context)
    {
        if (output.ClipId != context.Clip.ClipId)
        {
            throw Invariant.Failure(
                $"Architecture '{session.ArchitectureId}' returned artifact for clip "
                    + $"'{output.ClipId}' instead of planned clip '{context.Clip.ClipId}'.");
        }
        ArchitectureId plannedArchitectureId = context.Clip.Architecture.Id;
        if (output.ArchitectureId != session.ArchitectureId)
        {
            throw Invariant.Failure(
                $"Architecture '{session.ArchitectureId}' returned artifact for architecture "
                    + $"'{output.ArchitectureId}' instead of planned architecture "
                    + $"'{plannedArchitectureId}' for clip '{context.Clip.ClipId}'.");
        }
        output.ValidateDecoded();
    }

    private static void TryDispose(IVideoGenerationSession session)
    {
        try
        {
            session.Dispose();
        }
        catch
        {
            // Preserve the session-construction failure.
        }
    }

    private void CapturePreCoreMediaCore()
    {
        _rootSnapshot = null;
        if (!ShouldHandoffRoot())
        {
            return;
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        try
        {
            RequireResolvableRoot(bridge, _generator.CurrentMedia, "host root media");
            if (_generator.CurrentVae is not null)
            {
                RequireResolvableRoot(bridge, _generator.CurrentVae, "host root VAE");
            }
            HashSet<string> preCoreNodeIds = [.. bridge.Graph.Nodes.Keys];
            if (preCoreNodeIds.Count == 0)
            {
                throw RootHandoffError("the pre-core workflow snapshot is empty");
            }
            _rootSnapshot = new(
                _generator.CurrentMedia,
                _generator.CurrentVae,
                preCoreNodeIds);
        }
        catch
        {
            _rootSnapshot = null;
            throw;
        }
    }

    private void DropCoreOutputCore()
    {
        if (!ShouldHandoffRoot())
        {
            _rootSnapshot = null;
            return;
        }
        try
        {
            RootSnapshot snapshot = _rootSnapshot
                ?? throw RootHandoffError("the captured root state is missing");
            using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
            RequireResolvableRoot(bridge, snapshot.Media, "host root media");
            if (snapshot.Vae is not null)
            {
                RequireResolvableRoot(bridge, snapshot.Vae, "host root VAE");
            }
            if (snapshot.PreCoreNodeIds.Any(id => bridge.Graph.GetNode(id) is null))
            {
                throw RootHandoffError(
                    "the pre-core workflow snapshot references removed nodes");
            }
            _generator.CurrentMedia = snapshot.Media;
            _generator.CurrentVae = snapshot.Vae;
            WorkflowGraphCleanup.PruneNodesAddedSince(
                bridge,
                snapshot.PreCoreNodeIds,
                _generator.NodeHelpers);
        }
        finally
        {
            _rootSnapshot = null;
        }
    }

    private void RequireResolvableRoot(
        WorkflowBridge bridge,
        WGNodeData data,
        string description)
    {
        if (data?.Path is not JArray { Count: 2 } path
            || path[1].Type != JTokenType.Integer
            || bridge.ResolvePath(path) is null)
        {
            throw RootHandoffError(
                $"{description} is missing or no longer resolves in the workflow");
        }
    }

    private static InvalidOperationException RootHandoffError(string detail) =>
        Invariant.Failure(
            $"VideoStages could not restore the host root because {detail}.");

    private bool ShouldHandoffRoot() =>
        _rootOwner is not null && _plan.Root.InterceptsHostCore;

    private IArchitectureGenerationSessionProvider? RootOwnerProvider()
    {
        if (_rootOwner is null)
        {
            return null;
        }
        if (!_providers.TryGetValue(
            _rootOwner.Value,
            out IArchitectureGenerationSessionProvider provider))
        {
            throw Invariant.Failure(
                $"No generation runtime provider is registered for root architecture "
                    + $"'{_rootOwner.Value}'.");
        }
        return provider;
    }

    private sealed record RuntimeProviderBinding(
        IReadOnlyList<ArchitectureId> ActiveArchitectureIds,
        IReadOnlyList<IArchitectureGenerationSessionProvider> Providers,
        ArchitectureId? RootOwnerArchitectureId);

    private sealed record RootSnapshot(
        WGNodeData Media,
        WGNodeData Vae,
        HashSet<string> PreCoreNodeIds);

    private static RuntimeProviderBinding CreateProductionProviderBinding(
        WorkflowGenerator generator,
        VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(generator);
        IReadOnlyList<ArchitectureId> activeArchitectureIds =
            ResolveActiveArchitectureIds(plan);
        return new(
            activeArchitectureIds,
            VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                activeArchitectureIds),
            ArchitectureRootOwnerResolver.Resolve(plan));
    }

    private static RuntimeProviderBinding CreateInjectedProviderBinding(
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return new(
            ResolveActiveArchitectureIds(plan),
            Array.AsReadOnly(providers.ToArray()),
            ArchitectureRootOwnerResolver.Resolve(plan));
    }

    private static IReadOnlyList<ArchitectureId> ResolveActiveArchitectureIds(
        VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Array.AsReadOnly(plan.Clips
            .Select(clip => clip.Architecture?.Id
                ?? throw Invariant.Failure(
                    $"Clip {clip.ClipId} has no architecture identity."))
            .Distinct()
            .ToArray());
    }

    private static IReadOnlyList<IArchitectureGenerationSessionProvider>
        ResolveActiveProviders(
            IReadOnlyList<ArchitectureId> activeArchitectureIds,
            IReadOnlyDictionary<
                ArchitectureId,
                IArchitectureGenerationSessionProvider> providers)
    {
        List<IArchitectureGenerationSessionProvider> active = [];
        foreach (ArchitectureId id in activeArchitectureIds)
        {
            active.Add(providers[id]);
        }
        return active;
    }
}
