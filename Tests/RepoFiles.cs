using System.Runtime.CompilerServices;

namespace VideoStages.Tests;

/// <summary>The test assembly runs from an out-of-tree build directory, so checked-in sources are
/// located from this file's own compile-time path rather than from the working directory.</summary>
internal static class RepoFiles
{
    internal static string RepoRoot { get; } =
        Path.GetFullPath(Path.Combine(TestsDirectory(), ".."));

    internal static string SourceRoot { get; } = Path.Combine(RepoRoot, "src");

    internal static string ReadFrontend(string relativePath) =>
        File.ReadAllText(Path.Combine(TestsDirectory(), "..", "frontend", relativePath));

    internal static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(TestsDirectory(), "fixtures", name));

    private static string TestsDirectory([CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
