namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Declarative LTX interpretation of persisted DriveData and DriveMediaKinds. DriveData selects
/// runtime stream behavior; DriveMediaKinds optionally narrows acceptable source containers.
/// </summary>
internal sealed record IcLoraDriveMediaContract(
    IcLoraDriveData DriveData,
    IReadOnlySet<IcLoraDriveMediaKind> AcceptedKinds,
    bool RequiresInput)
{
    internal bool Accepts(IcLoraDriveMediaKind kind) => AcceptedKinds.Contains(kind);

    internal bool ConsumesVisual => DriveData == IcLoraDriveData.Visual;

    internal bool ConsumesAudio => DriveData == IcLoraDriveData.Audio;
}

internal static class IcLoraDriveMediaContracts
{
    private static readonly IcLoraDriveMediaContract None = new(
        IcLoraDriveData.None,
        new HashSet<IcLoraDriveMediaKind>(),
        RequiresInput: false);

    private static readonly IcLoraDriveMediaContract Visual = new(
        IcLoraDriveData.Visual,
        new HashSet<IcLoraDriveMediaKind>
        {
            IcLoraDriveMediaKind.Image,
            IcLoraDriveMediaKind.Video,
        },
        RequiresInput: true);

    private static readonly IcLoraDriveMediaContract Audio = new(
        IcLoraDriveData.Audio,
        new HashSet<IcLoraDriveMediaKind>
        {
            IcLoraDriveMediaKind.Audio,
            IcLoraDriveMediaKind.Video,
        },
        RequiresInput: true);

    internal static IcLoraDriveMediaContract Resolve(
        IcLoraDriveData driveData,
        IReadOnlyList<string> driveMediaKinds = null)
    {
        IcLoraDriveMediaContract generic = driveData switch
        {
            IcLoraDriveData.Visual => Visual,
            IcLoraDriveData.Audio => Audio,
            _ => None,
        };
        if (driveMediaKinds is null)
        {
            return generic;
        }

        HashSet<IcLoraDriveMediaKind> accepted = [];
        foreach (string rawKind in driveMediaKinds)
        {
            if (TryParseKind(rawKind, out IcLoraDriveMediaKind kind)
                && generic.Accepts(kind))
            {
                accepted.Add(kind);
            }
        }
        return generic with { AcceptedKinds = accepted };
    }

    internal static bool TryParseKind(string raw, out IcLoraDriveMediaKind kind)
    {
        string compact = StringUtils.Compact(raw);
        if (StringUtils.Equals(compact, nameof(IcLoraDriveMediaKind.Image)))
        {
            kind = IcLoraDriveMediaKind.Image;
            return true;
        }
        if (StringUtils.Equals(compact, nameof(IcLoraDriveMediaKind.Video)))
        {
            kind = IcLoraDriveMediaKind.Video;
            return true;
        }
        if (StringUtils.Equals(compact, nameof(IcLoraDriveMediaKind.Audio)))
        {
            kind = IcLoraDriveMediaKind.Audio;
            return true;
        }
        kind = IcLoraDriveMediaKind.Unknown;
        return false;
    }
}
