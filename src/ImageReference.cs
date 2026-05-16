using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
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

        if (material.StartsWith("inputs/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("Starred/", StringComparison.OrdinalIgnoreCase))
        {
            if (g.UserInput?.SourceSession is null)
            {
                Logs.Warning(
                    $"VideoStages: {descriptor} uses a server-side path but no session is available; "
                    + "cannot load the file.");
                return null;
            }

            try
            {
                material = T2IParamTypes.FilePathToDataString(
                    g.UserInput.SourceSession,
                    material,
                    $"for VideoStages {descriptor}");
            }
            catch (SwarmReadableErrorException ex)
            {
                Logs.Warning(
                    $"VideoStages: Could not resolve uploaded {descriptor} path '{material}': {ex.Message}");
                return null;
            }
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
