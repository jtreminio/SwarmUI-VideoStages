using System.Runtime.CompilerServices;

namespace VideoStages.Tests;

/// <summary>The test assembly runs from an out-of-tree build directory, so checked-in sources are
/// located from this file's own compile-time path rather than from the working directory.</summary>
internal static class RepoFiles
{
    /// <summary>Reads a file under <c>frontend/</c>, named with forward slashes.</summary>
    internal static string ReadFrontend(string relativePath) =>
        File.ReadAllText(Path.Combine(
            TestsDirectory(),
            "..",
            "frontend",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Reads a file under <c>Tests/fixtures/</c>.</summary>
    internal static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(TestsDirectory(), "fixtures", name));

    private static string TestsDirectory([CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
