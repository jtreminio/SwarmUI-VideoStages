using VideoStages.Architectures.Abstractions;

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
    /// <summary>The inverse of the wire mapping rather than a second copy of it, so a new mode
    /// cannot be readable without being writable. Anything unrecognized frames as Crop.</summary>
    internal static ReferenceFramingMode Parse(string value)
    {
        string wire = value?.Trim().ToLowerInvariant();
        return Enum.GetValues<ReferenceFramingMode>()
            .FirstOrDefault(
                mode => ArchitectureFeatureVocabulary.WireName(mode) == wire,
                ReferenceFramingMode.Crop);
    }
}
