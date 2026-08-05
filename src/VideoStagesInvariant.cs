namespace VideoStages;

internal static class VideoStagesInvariant
{
    internal static InvalidOperationException Failure(
        string detail,
        Exception innerException = null) =>
        new($"VideoStages bug: {detail}", innerException);
}
