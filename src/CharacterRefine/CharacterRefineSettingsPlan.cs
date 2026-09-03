using VideoStages.Authoring;

namespace VideoStages.CharacterRefine;

internal sealed record CharacterRefineSettingsPlan(
    string Finder,
    string Subject,
    string Part,
    double SubjectConfidence,
    double PartConfidence,
    int CropWidth,
    int CropHeight)
{
    internal static CharacterRefineSettingsPlan From(CharacterRefineSpec settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            settings.Finder,
            settings.Subject,
            settings.Part,
            settings.SubjectConfidence,
            settings.PartConfidence,
            settings.CropWidth,
            settings.CropHeight);
    }
}
