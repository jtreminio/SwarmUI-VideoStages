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
            IReadOnlyList<ArchitectureEntryMode> entryModes = descriptor.EntryModes ?? [];
            ArchitectureEntryMode[] duplicateEntryModes = [
                .. entryModes
                    .GroupBy(mode => mode)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
            ];
            if (entryModes.Count == 0
                || duplicateEntryModes.Length > 0
                || entryModes.Any(mode => !Enum.IsDefined(mode)))
            {
                throw new InvalidOperationException(
                    $"Video architecture '{descriptor.Id}' has missing, duplicate, or invalid "
                        + "entry modes.");
            }
            BoundaryJoinType[] missingBoundaryModes = [
                .. Enum.GetValues<BoundaryJoinType>()
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
                    || match.Architecture?.Id != module.Descriptor.Id
                    || match.FrameGrid < 1)
                {
                    throw new InvalidOperationException(
                        $"Video architecture module '{module.Descriptor.Id}' returned an invalid "
                        + $"model resolution for '{model?.Name}'.");
                }
                matches.Add(match.WithArchitecture(module.Descriptor));
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
