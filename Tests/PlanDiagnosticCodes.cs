using Newtonsoft.Json.Linq;

namespace VideoStages.Tests;

/// <summary>Plan diagnostic codes that also appear in the frontend, read from the fixture both
/// languages share so neither side can rename its half alone.</summary>
internal static class PlanDiagnosticCodes
{
    private static readonly JObject Codes =
        JObject.Parse(RepoFiles.ReadFixture("plan-diagnostic-codes.json"));

    internal static string RetakeSourceRequired => Codes.Value<string>("retakeSourceRequired");
}
