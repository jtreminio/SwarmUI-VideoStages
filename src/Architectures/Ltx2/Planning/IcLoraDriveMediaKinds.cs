namespace VideoStages.Architectures.Ltx2.Planning;

internal static class IcLoraDriveMediaKinds
{
    private static readonly IReadOnlySet<IcLoraDriveMediaKind> None =
        new HashSet<IcLoraDriveMediaKind>();

    private static readonly IReadOnlySet<IcLoraDriveMediaKind> Visual =
        new HashSet<IcLoraDriveMediaKind>
        {
            IcLoraDriveMediaKind.Image,
            IcLoraDriveMediaKind.Video,
        };

    private static readonly IReadOnlySet<IcLoraDriveMediaKind> Audio =
        new HashSet<IcLoraDriveMediaKind>
        {
            IcLoraDriveMediaKind.Audio,
            IcLoraDriveMediaKind.Video,
        };

    internal static IReadOnlySet<IcLoraDriveMediaKind> AcceptedFor(
        IcLoraDriveData stream,
        IReadOnlyList<string> authoredKinds = null)
    {
        IReadOnlySet<IcLoraDriveMediaKind> generic = stream switch
        {
            IcLoraDriveData.Visual => Visual,
            IcLoraDriveData.Audio => Audio,
            _ => None,
        };
        if (authoredKinds is null)
        {
            return generic;
        }

        HashSet<IcLoraDriveMediaKind> accepted = [];
        foreach (string rawKind in authoredKinds)
        {
            if (TryParse(rawKind, out IcLoraDriveMediaKind kind)
                && generic.Contains(kind))
            {
                accepted.Add(kind);
            }
        }
        return accepted;
    }

    internal static bool TryParse(string raw, out IcLoraDriveMediaKind kind)
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
