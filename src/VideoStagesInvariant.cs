namespace VideoStages;

internal static class VideoStagesInvariant
{
    internal static InvalidOperationException Failure(
        string detail,
        Exception innerException = null)
    {
        const string productPrefix = "VideoStages: ";
        if (detail.StartsWith(productPrefix, StringComparison.Ordinal))
        {
            detail = detail[productPrefix.Length..];
        }
        return new($"VideoStages bug: {detail}", innerException);
    }
}
