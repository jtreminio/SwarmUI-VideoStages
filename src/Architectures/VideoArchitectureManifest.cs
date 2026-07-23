using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.None;

namespace VideoStages.Architectures;

internal sealed record VideoArchitectureRegistration(
    IVideoArchitectureModule Module,
    Func<WorkflowGenerator, IArchitectureGenerationSessionFactoryProvider> CreateRuntimeProvider,
    Action RegisterHostHandlers,
    Action RegisterApiRoutes,
    Action RegisterDependencies);

/// <summary>
/// One production registration list owns model resolution, catalog publication, and runtime
/// dispatch. Adding an architecture cannot update one side while forgetting another.
/// </summary>
internal static class VideoArchitectureManifest
{
    internal static IReadOnlyList<VideoArchitectureRegistration> Production { get; } =
    [
        new(
            NoneArchitectureModule.Instance,
            generator => new SourceOnlyExecutionAdapter(generator),
            static () => { },
            static () => { },
            static () => { }),
        new(
            Ltx2ArchitectureModule.Instance,
            generator => new Ltx2ExecutionAdapter(generator),
            RootVideoStageResizer.RegisterHandlers,
            Ltx2ApiRoutes.Register,
            Ltx2HostIntegration.RegisterDependencies),
    ];

    internal static IReadOnlyList<IVideoArchitectureModule> ProductionModules =>
        Array.AsReadOnly(Production.Select(item => item.Module).ToArray());

    internal static IReadOnlyList<IArchitectureGenerationSessionFactoryProvider>
        CreateProductionRuntimeProviders(WorkflowGenerator generator) =>
        Array.AsReadOnly(Production
            .Select(item => item.CreateRuntimeProvider(generator))
            .ToArray());

    internal static void RegisterProductionHostHandlers()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterHostHandlers();
        }
    }

    internal static void RegisterProductionApiRoutes()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterApiRoutes();
        }
    }

    internal static void RegisterProductionDependencies()
    {
        foreach (VideoArchitectureRegistration registration in Production)
        {
            registration.RegisterDependencies();
        }
    }
}
