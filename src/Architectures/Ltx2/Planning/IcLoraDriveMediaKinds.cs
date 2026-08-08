using VideoStages.Authoring;

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
        IReadOnlyList<ClipReferenceKind> authoredKinds = null)
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
        foreach (ClipReferenceKind authored in authoredKinds)
        {
            IcLoraDriveMediaKind kind = From(authored);
            if (generic.Contains(kind))
            {
                accepted.Add(kind);
            }
        }
        return accepted;
    }

    internal static IcLoraDriveMediaKind From(ClipReferenceKind kind) => kind switch
    {
        ClipReferenceKind.Image => IcLoraDriveMediaKind.Image,
        ClipReferenceKind.Video => IcLoraDriveMediaKind.Video,
        ClipReferenceKind.Audio => IcLoraDriveMediaKind.Audio,
        _ => IcLoraDriveMediaKind.Unknown,
    };
}
