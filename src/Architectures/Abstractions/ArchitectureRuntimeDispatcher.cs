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
        try
        {
            foreach (IVideoGenerationSession session in sessions ?? [])
            {
                ArgumentNullException.ThrowIfNull(session);
                if (!byId.TryAdd(session.ArchitectureId, session))
                {
                    TryDispose(session);
                    throw new InvalidOperationException(
                        $"Duplicate runtime session for architecture '{session.ArchitectureId}'.");
                }
            }
        }
        catch
        {
            foreach (IVideoGenerationSession session in byId.Values)
            {
                TryDispose(session);
            }
            throw;
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
        if (output.ClipId != context.Clip.ClipId)
        {
            throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned artifact for clip "
                + $"'{output.ClipId}' instead of planned clip '{context.Clip.ClipId}'.");
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

    private static void TryDispose(IVideoGenerationSession session)
    {
        try
        {
            session.Dispose();
        }
        catch
        {
            // Preserve the construction failure that made rollback necessary.
        }
    }
}
