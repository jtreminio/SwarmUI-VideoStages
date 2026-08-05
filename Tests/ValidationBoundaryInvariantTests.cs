using System.Runtime.CompilerServices;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class ValidationBoundaryInvariantTests
{
    [Fact]
    public void User_facing_exceptions_exist_only_at_request_and_plan_boundaries()
    {
        string sourceRoot = Path.GetFullPath(Path.Combine(TestDirectory(), "..", "src"));
        HashSet<string> allowed =
        [
            Path.Combine(sourceRoot, "Authoring", "DocumentJson.cs"),
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

    [Fact]
    public void Internal_failures_use_the_invariant_exception_factory()
    {
        string sourceRoot = Path.GetFullPath(Path.Combine(TestDirectory(), "..", "src"));
        string factoryPath = Path.Combine(sourceRoot, "Invariant.cs");
        string[] offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => path != factoryPath)
            .Where(path => File.ReadAllText(path).Contains(
                "new InvalidOperationException",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string TestDirectory([CallerFilePath] string caller = "") =>
        Path.GetDirectoryName(caller)!;
}
