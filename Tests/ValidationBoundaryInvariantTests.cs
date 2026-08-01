using System.Runtime.CompilerServices;
using Xunit;

namespace VideoStages.Tests;

public sealed class ValidationBoundaryInvariantTests
{
    [Fact]
    public void User_facing_exceptions_exist_only_at_parser_and_plan_boundaries()
    {
        string sourceRoot = Path.GetFullPath(Path.Combine(TestDirectory(), "..", "src"));
        HashSet<string> allowed =
        [
            Path.Combine(sourceRoot, "VideoStagesJsonReader.cs"),
            Path.Combine(sourceRoot, "Planning", "PlanDiagnosticReporter.cs"),
        ];
        string[] offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !allowed.Contains(path))
            .Where(path => File.ReadAllText(path).Contains(
                "SwarmUserErrorException",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string TestDirectory([CallerFilePath] string caller = "") =>
        Path.GetDirectoryName(caller)!;
}
