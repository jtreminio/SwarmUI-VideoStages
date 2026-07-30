using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

internal static class UploadedMediaResolver
{
    /// <summary>Resolves an uploaded-media material string to a data string ready for
    /// <c>*File.FromDataString</c>. A server-side path (inputs/, raw/, or Starred/) is loaded via the
    /// source session; any other value is returned unchanged. Returns null (after logging) when a
    /// server path cannot be resolved. <paramref name="material"/> must be non-empty.</summary>
    public static string ResolveDataString(
        WorkflowGenerator g,
        string material,
        string descriptor,
        StringComparison pathComparison)
    {
        if (!material.StartsWith("inputs/", pathComparison)
            && !material.StartsWith("raw/", pathComparison)
            && !material.StartsWith("Starred/", pathComparison))
        {
            return material;
        }

        if (g.UserInput?.SourceSession is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: {descriptor} uses a server-side path (inputs/, raw/, or Starred/) "
                + "but no session is available; cannot load the file.");
            return null;
        }

        try
        {
            return T2IParamTypes.FilePathToDataString(
                g.UserInput.SourceSession,
                material,
                $"for VideoStages {descriptor}");
        }
        catch (SwarmReadableErrorException ex)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Could not resolve uploaded {descriptor} path '{material}': {ex.Message}");
            return null;
        }
    }
}
