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
        DecodedClipArtifact output = session.Execute(context)
            ?? throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned no decoded clip artifact.");
        ValidateOutput(output, session, context);
        return output;
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

    /// <summary>
    /// The common enforcement point for what an architecture session may return: the artifact
    /// must belong to the planned clip, to the session's own architecture, and carry valid
    /// decoded media.
    /// </summary>
    private static void ValidateOutput(
        DecodedClipArtifact output,
        IVideoGenerationSession session,
        ArchitectureClipRuntimeContext context)
    {
        if (output.ClipId != context.Clip.ClipId)
        {
            throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned artifact for clip "
                + $"'{output.ClipId}' instead of planned clip '{context.Clip.ClipId}'.");
        }
        ArchitectureId plannedArchitectureId = context.Clip.Architecture.Id;
        if (output.ArchitectureId != session.ArchitectureId)
        {
            throw new InvalidOperationException(
                $"Architecture '{session.ArchitectureId}' returned artifact for architecture "
                + $"'{output.ArchitectureId}' instead of planned architecture "
                + $"'{plannedArchitectureId}' for clip '{context.Clip.ClipId}'.");
        }
        output.ValidateDecoded();
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
