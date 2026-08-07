namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Names an uploaded IC-LoRA drive in user-facing load errors. Preflight and runtime must pass the
/// same descriptor for the same media, so both call this rather than spelling the string out.
/// </summary>
internal static class IcLoraDriveDescriptor
{
    internal static string Image(int clipId) => $"clip {clipId} IC-LoRA drive image";

    internal static string Video(int clipId) => $"clip {clipId} IC-LoRA drive video";
}
