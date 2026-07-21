using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;

namespace VideoStages;

public static class ImageReference
{
    private const string VideoStagePrefix = "Stage";
    private const string Base2EditStagePrefix = "edit";

    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex) =>
        TryParseNonNegativeIndexAfterPrefix(StringUtils.Compact(rawValue), VideoStagePrefix, out stageIndex);

    public static bool TryParseBase2EditStageIndex(string rawValue, out int stageIndex) =>
        TryParseNonNegativeIndexAfterPrefix(StringUtils.Compact(rawValue), Base2EditStagePrefix, out stageIndex);

    public static string FormatBase2EditStageIndex(int stageIndex) => $"{Base2EditStagePrefix}{stageIndex}";

    public static ImageFile MaterializeUploadedRefImage(WorkflowGenerator g, ImageRefSpec spec, string descriptor)
    {
        string material = spec.Data ?? spec.UploadFileName;
        if (string.IsNullOrEmpty(material))
        {
            Logs.Warning($"VideoStages: Upload {descriptor} is missing inline data and a file name.");
            return null;
        }

        material = UploadedMediaResolver.ResolveDataString(
            g, material, descriptor, StringComparison.OrdinalIgnoreCase);
        if (material is null)
        {
            return null;
        }

        try
        {
            return ImageFile.FromDataString(material);
        }
        catch (Exception ex)
        {
            Logs.Warning($"VideoStages: Ignoring invalid {descriptor} payload: {ex.Message}");
            return null;
        }
    }

    private static bool TryParseNonNegativeIndexAfterPrefix(string compact, string prefix, out int stageIndex)
    {
        stageIndex = -1;
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }
        if (!compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!int.TryParse(compact.AsSpan(prefix.Length), out int parsedIndex) || parsedIndex < 0)
        {
            return false;
        }
        stageIndex = parsedIndex;
        return true;
    }
}
