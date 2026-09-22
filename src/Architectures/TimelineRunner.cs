using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Execution;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal sealed class TimelineRunner
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

    internal TimelineRunner(
        WorkflowGenerator generator,
        VideoExecutionPlan plan) : this(
        generator,
        plan,
        CreateProductionProviderBinding(generator, plan))
    {
    }

    internal TimelineRunner(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionProvider> providers) : this(
        generator,
        plan,
        CreateInjectedProviderBinding(plan, providers))
    {
    }

    private TimelineRunner(
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

    internal IReadOnlyList<PlanDiagnostic> CollectPreflightDiagnostics()
    {
        UploadedMediaPreflight media = new(_generator.UserInput);
        List<PlanDiagnostic> diagnostics = [
            .. new Timeline.FrameInterpolator(_generator).Preflight(_plan),
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

    internal void ApplyRootAudioMaskDimensions() =>
        RootOwnerProvider()?.ApplyRootAudioMaskDimensions();

    internal void RunConfiguredStages()
    {
        if (_plan.Clips.Count == 0)
        {
            return;
        }
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(_generator, _plan);
        Timeline.Merger merger = new(_generator);
        AudioRuntimeSources preparedAudioSources = new AudioRuntimeSourceResolver(
            _generator,
            new AudioHandler(_generator)).Resolve(_plan);

        _generator.LastID = Math.Max(
            _generator.LastID,
            Constants.StagedNodeIdReservationFloor);
        Timeline.Boundaries boundaries = new(_generator, merger, _plan);
        ArchitectureTimelineSessionContext sessionContext = new(
            _plan,
            preparedAudioSources,
            boundaries)
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
                if (clipIndex > 0)
                {
                    // Refine Video installs its source only on the first sliced clip.
                    _generator.UserInput.ExtraMeta.Remove(VideoSamplerSeed.MetadataKey);
                }
                ClipPlan plannedClip = _plan.Clips[clipIndex];
                bool exposesPrevious = clipIndex > 0
                    && _plan.Boundaries[clipIndex - 1].EffectiveJoin != BoundaryJoinType.Cut
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
                if (output.Video is not null)
                {
                    output = RestoreWithSeedVr(output, plannedClip);
                    output = DecodedTimelineAudioOverlay.Apply(
                        _generator,
                        output,
                        plannedClip,
                        previousClipOutput?.ToAudioHostMedia(_generator));
                    VideoSamplerSeed.RecordOutputFromMedia(
                        _generator,
                        output.ToVideoHostMedia(_generator));
                }
                else if (clipIndex + 1 < _plan.Clips.Count
                    && _plan.Clips[clipIndex + 1].Audio.Base.Kind
                        != AudioSourceKind.PreviousClip
                    && !_plan.Clips[clipIndex + 1].Audio.Spans.Any(span =>
                        span.PostDecodeOnly))
                {
                    output.ToAudioHostMedia(_generator).SaveOutput(
                        null,
                        _generator.CurrentAudioVae,
                        _generator.GetStableDynamicID(
                            StableNodeIds.AudioOnlyClipSave,
                            plannedClip.ClipId));
                }
                clipOutputs.Add(output);
                previousClipOutput = output;
                previousClip = plannedClip;
            }

            finalArtifact = boundaries.Merge(clipOutputs);
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
        if (finalArtifact.Media?.DataType == WGNodeData.DT_VIDEO)
        {
            finalArtifact = new Timeline.GlobalVideoFrameTrimmer(_generator).Apply(finalArtifact);
            finalArtifact = new Timeline.FrameInterpolator(_generator).Apply(
                finalArtifact,
                _plan);
        }
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
        // A non-empty set means the timeline built beside the host root instead of adopting it.
        VideoGraphHelpers.CacheRemovalRecord(
            _generator,
            Constants.SweptHostRootNodesKey,
            rootSession.DisplacedRootRemovals);
        LtxPreprocessInputs.EnsureRgb(_generator);
        CollapseDuplicateNodes();
    }

    private DecodedClipArtifact RestoreWithSeedVr(
        DecodedClipArtifact output,
        ClipPlan clip)
    {
        if (!clip.UseSeedVr)
        {
            return output;
        }
        if (!_generator.UserInput.TryGet(
                ComfyUIBackendExtension.SeedVRModel,
                out T2IModel seedVrModel)
            || seedVrModel is null)
        {
            return output;
        }
        _generator.CurrentMedia = output.ToVideoHostMedia(_generator);
        WGNodeData vae = _generator.CurrentVae;
        _generator.SaveOptionalIntermediate();
        long seed = _generator.UserInput.Get(T2IParamTypes.Seed) + 500;
        WGNodeData media = _generator.CurrentMedia;
        double scale = _generator.UserInput.Get(
            ComfyUIBackendExtension.SeedVRUpscale,
            1);
        double downscale = _generator.UserInput.Get(
            ComfyUIBackendExtension.SeedVRPreDownscale,
            1);
        if (downscale <= 0)
        {
            throw new SwarmReadableErrorException(
                $"Invalid SeedVR pre-downscale value {downscale}. Must be greater than 0.");
        }
        if (downscale < 1)
        {
            media = media.AsRawImage(vae);
            int width = (int)Math.Round(
                (media.Width ?? _generator.UserInput.GetImageWidth()) * downscale);
            int height = (int)Math.Round(
                (media.Height ?? _generator.UserInput.GetImageHeight()) * downscale);
            string scaled = _generator.CreateNode("ImageScale", new JObject()
            {
                ["image"] = media.Path,
                ["width"] = width,
                ["height"] = height,
                ["upscale_method"] = "bilinear",
                ["crop"] = "disabled",
            });
            media = media.WithPath([scaled, 0]);
            media.Width = width;
            media.Height = height;
            scale /= downscale;
        }
        if (scale != 1)
        {
            media = UpscaleSeedVrInput(media, vae, seed, scale);
        }
        _generator.CurrentMedia = _generator.CreateSeedVR2Restore(
            seedVrModel,
            media,
            vae,
            seed);
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        return DecodedClipArtifact.FromRuntime(
            RuntimeArtifact.Capture(_generator, bridge),
            clip);
    }

    private WGNodeData UpscaleSeedVrInput(
        WGNodeData media,
        WGNodeData vae,
        long seed,
        double scale)
    {
        string method = _generator.UserInput.Get(
            ComfyUIBackendExtension.SeedVRUpscaleMethod,
            "pixel-lanczos");
        int width = (int)Math.Round(
            (media.Width ?? _generator.UserInput.GetImageWidth()) * scale) / 16 * 16;
        int height = (int)Math.Round(
            (media.Height ?? _generator.UserInput.GetImageHeight()) * scale) / 16 * 16;
        if (method.StartsWith("latent-"))
        {
            media = media.AsLatentImage(vae);
            string scaled = _generator.CreateNode("LatentUpscaleBy", new JObject()
            {
                ["samples"] = media.Path,
                ["upscale_method"] = method[7..],
                ["scale_by"] = scale,
            });
            media = media.WithPath([scaled, 0]);
            media.Width = width;
            media.Height = height;
            return media;
        }

        media = media.AsRawImage(vae);
        if (method.StartsWith("pidmodel-"))
        {
            T2IModel model = ComfyUIBackendExtension.GetPidModel(
                method[9..],
                _generator.UserInput.SourceSession);
            return _generator.CreatePixelDecode(model, media, vae, seed);
        }
        if (method.StartsWith("model-"))
        {
            string loader = _generator.CreateNode("UpscaleModelLoader", new JObject()
            {
                ["model_name"] = method[6..],
            });
            string scaled = _generator.CreateNode("ImageUpscaleWithModel", new JObject()
            {
                ["upscale_model"] = new JArray(loader, 0),
                ["image"] = media.Path,
            });
            media = media.WithPath([scaled, 0]);
            media.Width = null;
            media.Height = null;
            return media;
        }
        if (!method.StartsWith("pixel-"))
        {
            throw new SwarmReadableErrorException(
                $"Unsupported upscale method '{method}' for SeedVR");
        }
        if (media.Width == width && media.Height == height)
        {
            return media;
        }
        string imageScale = _generator.CreateNode("ImageScale", new JObject()
        {
            ["image"] = media.Path,
            ["width"] = width,
            ["height"] = height,
            ["upscale_method"] = method[6..],
            ["crop"] = "disabled",
        });
        media = media.WithPath([imageScale, 0]);
        media.Width = width;
        media.Height = height;
        return media;
    }

    // Runs only after every graph rewrite so merged nodes cannot be mutated later.
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
        // Core's later audio and save steps also read the non-media trackers.
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

    internal void CapturePreCoreMedia()
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

    internal void DropCoreOutput()
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
            HashSet<string> preservedNodeIds = [.. snapshot.PreCoreNodeIds];
            if (_generator.CurrentAudioVae?.Path is JArray { Count: 2 } audioVaePath
                && bridge.ResolvePath(audioVaePath) is not null)
            {
                preservedNodeIds.Add($"{audioVaePath[0]}");
            }
            _generator.CurrentMedia = snapshot.Media;
            _generator.CurrentVae = snapshot.Vae;
            WorkflowGraphCleanup.PruneNodesAddedSince(
                bridge,
                preservedNodeIds,
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
