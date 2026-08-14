namespace VideoStages.Planning;

internal enum FrameRefSourceKind
{
    Upload,
    Base,
    Refiner,
    Base2Edit,
    Unknown,
}

internal enum FrameRefEdge
{
    Start,
    End,
}

internal sealed record FrameRefPlan(
    FrameRefSourceKind SourceKind,
    string RawSource,
    int? Base2EditStageIndex,
    int Frame,
    FrameRefEdge FrameOrigin,
    double Strength,
    string UploadFileName,
    string InlineData)
{
    internal bool IsOpeningFrame => FrameOrigin == FrameRefEdge.Start && Frame == 1;

    internal bool IsClosingFrame => FrameOrigin == FrameRefEdge.End && Frame == 1;

    internal bool IsEndpoint => IsOpeningFrame || IsClosingFrame;

    internal int GuideFrameIndex => FrameOrigin == FrameRefEdge.End
        ? -Math.Max(1, Frame)
        : Math.Max(1, Frame);
}
