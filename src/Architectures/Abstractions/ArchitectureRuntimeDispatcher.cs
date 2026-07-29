using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>Per-timeline dispatcher; sessions are selected independently for every clip.</summary>
internal sealed class ArchitectureRuntimeDispatcher : IDisposable
{
    private readonly IReadOnlyDictionary<ArchitectureId, IVideoGenerationSession> _sessions;

    internal ArchitectureRuntimeDispatcher(IEnumerable<IVideoGenerationSession> sessions)
    {
        Dictionary<ArchitectureId, IVideoGenerationSession> byId = [];
        foreach (IVideoGenerationSession session in sessions ?? [])
        {
            ArgumentNullException.ThrowIfNull(session);
            if (!byId.TryAdd(session.ArchitectureId, session))
            {
                throw new InvalidOperationException(
                    $"Duplicate runtime session for architecture '{session.ArchitectureId}'.");
            }
        }
        _sessions = byId;
    }

    internal DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IVideoGenerationSession session = ResolveSession(context.Clip);
        return Execute(session, context);
    }

    private IVideoGenerationSession ResolveSession(ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArchitectureId id = clip.Architecture?.Id
            ?? throw new InvalidOperationException(
                $"Clip {clip.ClipId} has no architecture identity.");
        if (!_sessions.TryGetValue(id, out IVideoGenerationSession session))
        {
            throw new InvalidOperationException(
                $"No runtime session is registered for architecture '{id}'.");
        }
        return session;
    }

    private static DecodedClipArtifact Execute(
        IVideoGenerationSession session,
        ArchitectureClipRuntimeContext context)
    {
        DecodedClipArtifact output = session.Execute(context)
            ?? throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned no decoded clip artifact.");
        ArchitectureId plannedArchitecture = context.Clip.Architecture.Id;
        if (output.ArchitectureId != plannedArchitecture
            || output.ClipId != context.Clip.ClipId)
        {
            throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned artifact identity "
                + $"'{output.ArchitectureId}/{output.ClipId}' for planned clip "
                + $"'{plannedArchitecture}/{context.Clip.ClipId}'.");
        }
        output.ValidateDecoded();
        return output;
    }

    public void Dispose()
    {
        foreach (IVideoGenerationSession session in _sessions.Values)
        {
            session.Dispose();
        }
    }
}
