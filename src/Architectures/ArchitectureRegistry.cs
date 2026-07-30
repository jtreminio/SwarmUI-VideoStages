using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal sealed class VideoArchitectureRegistry : IVideoArchitectureRegistry
{
    private readonly IReadOnlyList<IVideoArchitectureModule> _modules;

    internal VideoArchitectureRegistry(IEnumerable<IVideoArchitectureModule> modules)
    {
        IVideoArchitectureModule[] resolved = (modules ?? []).ToArray();
        ArchitectureId[] duplicates = [
            .. resolved.GroupBy(module => module.Descriptor.Id)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
        ];
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate video architecture ids: "
                + string.Join(", ", duplicates.Select(id => $"'{id}'")));
        }
        foreach (IVideoArchitectureModule module in resolved)
        {
            VideoArchitectureDescriptor descriptor = module.Descriptor;
            if (descriptor.FrameGrid < 1)
            {
                throw new InvalidOperationException(
                    $"Video architecture '{descriptor.Id}' has invalid frame grid "
                        + $"{descriptor.FrameGrid}; grids must be positive.");
            }
            if (module is not IArchitectureEffectiveRequestProjector projector)
            {
                if (descriptor.IgnoredUnsupportedFeatures.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Video architecture '{descriptor.Id}' publishes ignored unsupported "
                            + "features but does not own an effective-request projector.");
                }
            }
            else if (projector.ProjectedUnsupportedFeatures is null
                || !descriptor.IgnoredUnsupportedFeatures.SetEquals(
                    projector.ProjectedUnsupportedFeatures))
            {
                throw new InvalidOperationException(
                    $"Video architecture '{descriptor.Id}' publishes ignored unsupported "
                        + "features that do not match its effective-request projector.");
            }
            ModelProfileId[] duplicateProfiles = [
                .. descriptor.Profiles
                    .GroupBy(profile => profile.Id)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
            ];
            if (duplicateProfiles.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Video architecture '{descriptor.Id}' has duplicate legacy profile ids: "
                    + string.Join(", ", duplicateProfiles.Select(id => $"'{id}'")));
            }
            foreach (VideoModelProfileDescriptor profile in descriptor.Profiles)
            {
                ArchitectureEntryMode[] duplicateEntryModes = [
                    .. profile.EntryModes
                        .GroupBy(mode => mode)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key)
                ];
                if (profile.EntryModes is not { Count: > 0 }
                    || duplicateEntryModes.Length > 0
                    || profile.EntryModes.Any(mode => !Enum.IsDefined(mode)))
                {
                    throw new InvalidOperationException(
                        $"Video architecture '{descriptor.Id}' legacy profile '{profile.Id}' "
                            + "has missing, duplicate, or invalid entry-mode aliases.");
                }
            }
            BoundaryExecutionMode[] missingBoundaryModes = [
                .. Enum.GetValues<BoundaryExecutionMode>()
                    .Where(mode => !descriptor.BoundaryRules.ContainsKey(mode))
            ];
            if (missingBoundaryModes.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Video architecture '{descriptor.Id}' is missing boundary rules for: "
                    + string.Join(", ", missingBoundaryModes));
            }
        }
        _modules = Array.AsReadOnly(resolved);
    }

    internal static VideoArchitectureRegistry Production { get; } =
        new(VideoArchitectureManifest.ProductionModules);

    public IReadOnlyList<VideoArchitectureDescriptor> Catalog =>
        Array.AsReadOnly(_modules.Select(module => module.Descriptor).ToArray());

    public IVideoArchitectureModule GetModule(ArchitectureId architectureId) =>
        _modules.SingleOrDefault(module => module.Descriptor.Id == architectureId)
        ?? throw new KeyNotFoundException(
            $"No module is registered for architecture '{architectureId}'.");

    public IReadOnlyList<ResolvedVideoModel> ResolvedModels =>
        Array.AsReadOnly(Program.MainSDModels.Models.Values
            .Select(model => TryResolveModel(model, out ResolvedVideoModel resolved) ? resolved : null)
            .Where(resolved => resolved is not null)
            .OrderBy(resolved => resolved.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }
        T2IModel model = Program.MainSDModels.GetModel(modelName, null);
        return TryResolveModel(model, out resolved);
    }

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        List<ResolvedVideoModel> matches = [];
        foreach (IVideoArchitectureModule module in _modules)
        {
            if (module.TryResolveModel(model, out ResolvedVideoModel match))
            {
                if (match is null
                    || match.ArchitectureId != module.Descriptor.Id
                    || match.Architecture?.Id != module.Descriptor.Id
                    || string.IsNullOrWhiteSpace(match.ModelClassId)
                    || string.IsNullOrWhiteSpace(match.CompatibilityClassId)
                    || match.EntryAbilities == VideoModelEntryAbility.None
                    || match.FrameGrid < 1
                    || !match.HostFactsAuthoritative)
                {
                    throw new InvalidOperationException(
                        $"Video architecture module '{module.Descriptor.Id}' returned an invalid "
                        + $"model resolution for '{model?.Name}'.");
                }
                matches.Add(match with
                {
                    Architecture = module.Descriptor,
                });
            }
        }
        if (matches.Count > 0)
        {
            ArchitectureResolutionTier winningTier = matches.Min(match =>
                GetModule(match.ArchitectureId).ResolutionTier);
            matches = matches
                .Where(match =>
                    GetModule(match.ArchitectureId).ResolutionTier == winningTier)
                .ToList();
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Model '{model?.Name}' ambiguously resolves within the winning architecture "
                + "tier to "
                + string.Join(", ", matches.Select(match => $"'{match.ArchitectureId}'")));
        }
        resolved = matches.SingleOrDefault();
        return resolved is not null;
    }
}
