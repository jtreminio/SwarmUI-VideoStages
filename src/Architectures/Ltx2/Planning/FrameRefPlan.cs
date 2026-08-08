namespace VideoStages.Architectures.Ltx2.Planning;

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
    /// <summary>
    /// The clip's very first frame, which LTX merges in place instead of adding as a guide.
    /// </summary>
    internal bool IsOpeningFrame => FrameOrigin == FrameRefEdge.Start && Frame == 1;
}
