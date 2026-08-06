using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// The host's browser-visible warning channel for one request. Lines are deduplicated. SwarmUI
/// serializes <c>parser_warnings</c> with completed output metadata and gives that field warning
/// styling in the browser.
/// </summary>
internal static class RequestWarnings
{
    private const string HostWarningMetadataKey = "parser_warnings";

    internal static void Track(T2IParamInput input, string warning)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(warning);
        Logs.Warning(warning);

        lock (input.ExtraMeta)
        {
            List<string> warnings;
            if (!input.ExtraMeta.TryGetValue(HostWarningMetadataKey, out object existing))
            {
                warnings = [];
            }
            else if (existing is List<string> existingWarnings)
            {
                // T2IParamInput.Clone copies ExtraMeta's dictionary but not its values. Copy the
                // prompt-warning list before editing so parallel generation inputs never share a
                // mutable collection with one another or with output metadata serialization.
                warnings = [.. existingWarnings];
            }
            else
            {
                warnings = [$"{existing}"];
            }
            if (!warnings.Contains(warning, StringComparer.Ordinal))
            {
                warnings.Add(warning);
            }
            input.ExtraMeta[HostWarningMetadataKey] = warnings;
        }
    }
}
