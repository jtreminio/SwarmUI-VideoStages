namespace VideoStages;

internal static class Invariant
{
    internal static InvalidOperationException Failure(
        string detail,
        Exception innerException = null) =>
        new($"VideoStages bug: {detail}", innerException);
}
