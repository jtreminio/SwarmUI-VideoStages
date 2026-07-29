using SwarmUI.Accounts;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures;

internal sealed class AuthorizedArchitectureRegistry(
    IVideoArchitectureRegistry registry,
    Session session) : IVideoArchitectureRegistry
{
    public IReadOnlyList<VideoArchitectureDescriptor> Catalog => registry.Catalog;

    public IReadOnlyList<ResolvedVideoModel> ResolvedModels =>
        Array.AsReadOnly(registry.ResolvedModels
            .Where(resolved => IsAllowed(resolved.ModelName))
            .ToArray());

    public IVideoArchitectureModule GetModule(ArchitectureId architectureId) =>
        registry.GetModule(architectureId);

    public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved)
    {
        resolved = null;
        return IsAllowed(modelName) && registry.TryResolveModel(modelName, out resolved);
    }

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        resolved = null;
        return IsAllowed(model?.Name) && registry.TryResolveModel(model, out resolved);
    }

    private bool IsAllowed(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName) && session.User.IsAllowedModel(modelName);
}

internal static class AuthorizedArchitectureRegistryExtensions
{
    internal static IVideoArchitectureRegistry ForSession(
        this IVideoArchitectureRegistry registry,
        Session session) =>
        session?.User is null ? registry : new AuthorizedArchitectureRegistry(registry, session);
}
