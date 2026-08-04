using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal sealed class VideoArchitectureExecutionHost
{
    private readonly WorkflowGenerator _generator;
    private readonly VideoExecutionPlan _plan;
    private readonly IReadOnlyDictionary<
        ArchitectureId,
        IArchitectureGenerationSessionProvider> _providers;
    private readonly IReadOnlyList<ArchitectureId> _activeArchitectureIds;
    private readonly IReadOnlyList<IArchitectureGenerationSessionProvider>
        _activeProviders;
    private readonly ArchitectureId? _rootOwner;
    private VideoExecutionPlanContext _executionContext;

    internal T2IParamInput RequestInput => _generator.UserInput;

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlanContext context) : this(
        generator,
        context?.Plan,
        CreateProductionProviderBinding(generator, context))
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
                throw VideoStagesInvariant.Failure(
                    $"Duplicate generation runtime provider for architecture "
                    + $"'{provider.ArchitectureId}'.");
            }
        }
        _providers = byId;
        _activeArchitectureIds = binding.ActiveArchitectureIds;
        _activeProviders = ResolveActiveProviders(_activeArchitectureIds, byId);
        _rootOwner = binding.RootOwnerArchitectureId;
    }

    /// <summary>
    /// Runs request preflight before any VideoStages workflow phase mutates the host graph.
    /// </summary>
    internal IReadOnlyList<PlanDiagnostic> CollectPreflightDiagnostics()
    {
        List<PlanDiagnostic> diagnostics = [
            .. new TimelineFrameInterpolator(_generator).Preflight(_plan)
        ];
        ArchitectureRequestPreflightContext context = new(_plan, _rootOwner);
        foreach (IArchitectureGenerationSessionProvider provider in _activeProviders)
        {
            diagnostics.AddRange(provider.PreflightRequest(context));
        }
        return diagnostics.AsReadOnly();
    }

    internal void BindExecutionContext(VideoExecutionPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Plan, _plan))
        {
            throw VideoStagesInvariant.Failure(
                "The execution host cannot bind a different video execution plan.");
        }
        if (_executionContext is not null && !ReferenceEquals(_executionContext, context))
        {
            throw VideoStagesInvariant.Failure(
                "The execution host is already bound to another VideoStages request.");
        }
        _executionContext = context;
    }

    internal void DispatchHostPhase(ArchitectureHostPhase phase)
    {
        RequireExecutionContext().ExecutePrepared(
            this,
            () => DispatchHostPhaseCore(phase));
    }

    private void DispatchHostPhaseCore(ArchitectureHostPhase phase)
    {
        if (phase == ArchitectureHostPhase.CaptureControlNetPreprocessors)
        {
            new ControlNetCoreMediaCapture(_generator).Capture();
        }
        IEnumerable<IArchitectureGenerationSessionProvider> providers =
            ArchitectureHostPhases.IsRootOwnerOnly(phase)
                ? RootOwnerProvider()
                : _activeProviders;
        foreach (IArchitectureHostPhaseParticipant participant in providers
            .OfType<IArchitectureHostPhaseParticipant>())
        {
            participant.ExecuteHostPhase(new(phase, _plan, _rootOwner));
        }
    }

    internal void RunConfiguredStages()
    {
        VideoExecutionPlanContext context = RequireExecutionContext();
        context.ExecuteToCompletion(this, () => RunConfiguredStagesCore(context));
    }

    private void RunConfiguredStagesCore(VideoExecutionPlanContext context)
    {
        if (_plan.Clips.Count == 0)
        {
            return;
        }
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(_generator, context);
        MultiClipParallelMerger merger = new(_generator);
        RootExecutionPolicy rootPolicy = new(_plan);
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
            rootPolicy,
            assembly)
        {
            RootAdoption = new HostRootAdoption(
                _generator,
                rootPolicy,
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
                        OwnsGeneratedRoot =
                            context.RootOwnerArchitectureId == provider.ArchitectureId,
                    });
                ArgumentNullException.ThrowIfNull(session);
                if (!sessions.TryAdd(session.ArchitectureId, session))
                {
                    TryDispose(session);
                    throw VideoStagesInvariant.Failure(
                        $"Duplicate runtime session for architecture "
                            + $"'{session.ArchitectureId}'.");
                }
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
                    ?? throw VideoStagesInvariant.Failure(
                        $"Clip {plannedClip.ClipId} has no architecture identity.");
                if (!sessions.TryGetValue(
                        architectureId,
                        out IVideoGenerationSession session))
                {
                    throw VideoStagesInvariant.Failure(
                        $"No runtime session is registered for architecture "
                            + $"'{architectureId}'.");
                }
                DecodedClipArtifact output = session.Execute(runtimeContext)
                    ?? throw VideoStagesInvariant.Failure(
                        $"Architecture '{session.ArchitectureId}' returned no decoded clip "
                            + "artifact.");
                ValidateOutput(output, session, runtimeContext);
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
    }

    private static void ValidateOutput(
        DecodedClipArtifact output,
        IVideoGenerationSession session,
        ArchitectureClipRuntimeContext context)
    {
        if (output.ClipId != context.Clip.ClipId)
        {
            throw VideoStagesInvariant.Failure(
                $"Architecture '{session.ArchitectureId}' returned artifact for clip "
                    + $"'{output.ClipId}' instead of planned clip '{context.Clip.ClipId}'.");
        }
        ArchitectureId plannedArchitectureId = context.Clip.Architecture.Id;
        if (output.ArchitectureId != session.ArchitectureId)
        {
            throw VideoStagesInvariant.Failure(
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

    private VideoExecutionPlanContext RequireExecutionContext() =>
        _executionContext
        ?? throw VideoStagesInvariant.Failure(
            "The execution host is not bound to a prepared VideoStages request.");

    private IEnumerable<IArchitectureGenerationSessionProvider> RootOwnerProvider()
    {
        if (_rootOwner is null)
        {
            return [];
        }
        if (!_providers.TryGetValue(
            _rootOwner.Value,
            out IArchitectureGenerationSessionProvider provider))
        {
            throw VideoStagesInvariant.Failure(
                $"No generation runtime provider is registered for root architecture "
                    + $"'{_rootOwner.Value}'.");
        }
        return [provider];
    }

    private sealed record RuntimeProviderBinding(
        IReadOnlyList<ArchitectureId> ActiveArchitectureIds,
        IReadOnlyList<IArchitectureGenerationSessionProvider> Providers,
        ArchitectureId? RootOwnerArchitectureId);

    private static RuntimeProviderBinding CreateProductionProviderBinding(
        WorkflowGenerator generator,
        VideoExecutionPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(context);
        return new(
            context.ActiveArchitectureIds,
            VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                context.ActiveArchitectureIds),
            context.RootOwnerArchitectureId);
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
                ?? throw VideoStagesInvariant.Failure(
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
