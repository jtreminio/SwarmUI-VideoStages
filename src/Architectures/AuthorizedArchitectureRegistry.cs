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

    /// <summary>
    /// Authorizes the canonical resolved name, never the authored spelling: core resolves both
    /// <c>name</c> and <c>name.safetensors</c>, while blacklist/whitelist prefixes match the exact
    /// string they are given. Refusal is reported as an unresolved model so the caller cannot use
    /// this boundary to enumerate models it may not have.
    /// </summary>
    public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved)
    {
        resolved = null;
        if (!registry.TryResolveModel(modelName, out ResolvedVideoModel match)
            || !IsAllowed(match.ModelName))
        {
            return false;
        }
        resolved = match;
        return true;
    }

    /// <inheritdoc cref="TryResolveModel(string, out ResolvedVideoModel)"/>
    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        resolved = null;
        if (!registry.TryResolveModel(model, out ResolvedVideoModel match)
            || !IsAllowed(match.ModelName))
        {
            return false;
        }
        resolved = match;
        return true;
    }

    private bool IsAllowed(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName) && session.User.IsAllowedModel(modelName);
}

internal static class AuthorizedArchitectureRegistryExtensions
{
    /// <summary>
    /// Applies a request session's model authorization to <paramref name="registry"/>.
    /// <para>
    /// A session without a user carries no authorization context, and the unfiltered registry is
    /// returned. This is deliberate and follows the host convention already used for session-owned
    /// media (<see cref="UploadedMediaResolver"/>): a sessionless generator is an internal or test
    /// caller, not an unauthenticated request. Both production entry points — the catalog API route
    /// and plan compilation — are reached only with a real request session.
    /// </para>
    /// </summary>
    internal static IVideoArchitectureRegistry ForSession(
        this IVideoArchitectureRegistry registry,
        Session session) =>
        session?.User is null ? registry : new AuthorizedArchitectureRegistry(registry, session);
}
