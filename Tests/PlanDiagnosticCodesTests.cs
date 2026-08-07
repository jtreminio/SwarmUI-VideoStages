using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PlanDiagnosticCodesTests
{
    [Fact]
    public void Generated_typescript_plan_diagnostic_codes_are_current()
    {
        Assert.Equal(
            PlanDiagnosticCodes.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedPlanDiagnostics.ts"));
    }
}
