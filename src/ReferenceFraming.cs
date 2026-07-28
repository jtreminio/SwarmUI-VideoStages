namespace VideoStages;

/// <summary>How clip-level reference media is fitted to generation dimensions.</summary>
public enum ReferenceFramingMode
{
    Crop,
    Stretch,
    Fit,
    FitGreen,
}

internal static class ReferenceFraming
{
    internal static ReferenceFramingMode Parse(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Constants.ReferenceFramingStretch => ReferenceFramingMode.Stretch,
            Constants.ReferenceFramingFit => ReferenceFramingMode.Fit,
            Constants.ReferenceFramingFitGreen => ReferenceFramingMode.FitGreen,
            _ => ReferenceFramingMode.Crop,
        };

    internal static string ToWireValue(ReferenceFramingMode value) => value switch
    {
        ReferenceFramingMode.Stretch => Constants.ReferenceFramingStretch,
        ReferenceFramingMode.Fit => Constants.ReferenceFramingFit,
        ReferenceFramingMode.FitGreen => Constants.ReferenceFramingFitGreen,
        _ => Constants.ReferenceFramingCrop,
    };
}
