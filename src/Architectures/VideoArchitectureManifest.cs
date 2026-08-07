using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;
using VideoStages.Execution;
using VideoStages.Architectures.Ltx2.Runtime;

namespace VideoStages.Architectures;

internal sealed record VideoArchitectureRegistration(
    VideoArchitectureDescriptor Descriptor,
    IVideoArchitectureModule Module,
    Func<WorkflowGenerator, IArchitectureGenerationSessionProvider> CreateRuntimeProvider,
    Action RegisterHostHandlers = null,
    Action RegisterApiRoutes = null,
    Action RegisterDependencies = null);

/// <summary>
/// Production registrations for model resolution, catalog publication, and runtime dispatch.
/// </summary>
internal static class VideoArchitectureManifest
{
    internal static IReadOnlyList<VideoArchitectureRegistration> Production { get; } =
    [
        new(
            NoneArchitecture.Descriptor,
            null,
            generator => new SourceOnlyExecutionAdapter(generator)),
        new(
            Ltx2ArchitectureModule.Instance.Descriptor,
            Ltx2ArchitectureModule.Instance,
            generator => new Ltx2ExecutionAdapter(generator),
            RootVideoStageResizer.RegisterHandlers,
            Ltx2ApiRoutes.Register,
            Ltx2HostIntegration.RegisterDependencies),
        new(
            MiniMaxArchitectureModule.Instance.Descriptor,
            MiniMaxArchitectureModule.Instance,
            generator => new MiniMaxExecutionAdapter(generator)),
        // Wan's graph is built by SwarmUI's stock image-to-video path. Narrow host callbacks keep
        // request-global options out of the discarded core pass; the authored session applies the
        // supported values itself.
        new(
            WanArchitectureModule.Instance.Descriptor,
            WanArchitectureModule.Instance,
            generator => new WanExecutionAdapter(generator),
            WanHostHandlers.Register),
        // The fallback resolves only exact model classes with proven stock host video branches.
        // Its resolution tier keeps every specialized module authoritative.
        new(
            HostVideoArchitectureModule.Instance.Descriptor,
            HostVideoArchitectureModule.Instance,
            generator => new HostVideoExecutionAdapter(generator),
            HostVideoCorePassIsolation.RegisterHandlers),
    ];

    internal static IReadOnlyList<IVideoArchitectureModule> ProductionModules { get; } =
        Array.AsReadOnly(Production
            .Where(item => item.Module is not null)
            .Select(item => item.Module)
            .ToArray());

    internal static IReadOnlyList<VideoArchitectureDescriptor> ProductionDescriptors { get; } =
        Array.AsReadOnly(Production.Select(item => item.Descriptor).ToArray());

    internal static IReadOnlyList<IArchitectureGenerationSessionProvider>
        CreateProductionRuntimeProviders(
            WorkflowGenerator generator,
            IEnumerable<ArchitectureId> activeArchitectureIds)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(activeArchitectureIds);
        Dictionary<ArchitectureId, VideoArchitectureRegistration> registrations =
            Production.ToDictionary(item => item.Descriptor.Id);
        List<IArchitectureGenerationSessionProvider> providers = [];
        foreach (ArchitectureId architectureId in activeArchitectureIds.Distinct())
        {
            if (!registrations.TryGetValue(
                architectureId,
                out VideoArchitectureRegistration registration))
            {
                throw Invariant.Failure(
                    $"No generation runtime provider is registered for architecture "
                        + $"'{architectureId}'.");
            }
            providers.Add(registration.CreateRuntimeProvider(generator));
        }
        return providers.AsReadOnly();
    }

    internal static void RegisterProductionHostHandlers()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterHostHandlers?.Invoke();
        }
    }

    internal static void RegisterProductionApiRoutes()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterApiRoutes?.Invoke();
        }
    }

    internal static void RegisterProductionDependencies()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterDependencies?.Invoke();
        }
    }
}
