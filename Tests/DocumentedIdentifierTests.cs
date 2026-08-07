using System.Text.RegularExpressions;
using Xunit;

namespace VideoStages.Tests;

/// <summary>The maps a new reader greps. A backticked name in one of them is a promise that the
/// name is in the tree, and renames have broken that promise repeatedly without the suite
/// noticing.</summary>
[Collection("VideoStagesTests")]
public class DocumentedIdentifierTests
{
    private static readonly string[] MappingDocs =
    [
        "ARCHITECTURE.md",
        "FRONTEND_ARCHITECTURE.md",
        "README.md",
        Path.Combine("docs", "STAGE_RUNTIME.md"),
        Path.Combine("docs", "ARCHITECTURE_FLOW.md"),
    ];

    /// <summary>MSBuild properties the docs name in order to say the project does not set them.
    /// Absence from the tree is the point, so they cannot be checked like every other name.</summary>
    private static readonly string[] DeliberatelyAbsent =
        ["PublishTrimmed", "TrimMode", "PublishAot"];

    private static readonly string[] SkippedDirectories =
        [".git", ".stfolder", ".stversions", "Assets", "bin", "node_modules", "nonversioned", "obj"];

    /// <summary>An identifier or a dotted chain of them, which is how the docs write both a type
    /// and one of its members. Anything with a space, slash or bracket is prose or a wire key
    /// shape, not a name to resolve.</summary>
    private static readonly Regex IdentifierChain =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    private static readonly Regex FencedBlock = new("```.*?```", RegexOptions.Singleline);

    private static readonly Regex InlineCode = new("`([^`\n]+)`", RegexOptions.Compiled);

    private static readonly Regex Word = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>Names the ghost sweep repointed to. They keep the filters above honest: if one
    /// stops admitting names, these vanish from the checked set and this goes red instead of
    /// passing on nothing.</summary>
    private static readonly string[] MustBeChecked =
        ["RequestCaches", "TimelineSpec", "VideoStagesExtension"];

    [Fact]
    public void EveryNameTheMappingDocsBacktickIsInTheTree()
    {
        HashSet<string> tree = CollectTreeWords();
        HashSet<string> checkedNames = new(StringComparer.Ordinal);
        List<string> unresolved = [];
        foreach (string doc in MappingDocs)
        {
            string text = FencedBlock.Replace(
                File.ReadAllText(Path.Combine(RepoFiles.RepoRoot, doc)),
                string.Empty);
            foreach (Match span in InlineCode.Matches(text))
            {
                string name = span.Groups[1].Value.Trim();
                if (!IdentifierChain.IsMatch(name)
                    || name.EndsWith(".md", StringComparison.Ordinal)
                    || DeliberatelyAbsent.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }
                foreach (string part in name.Split('.'))
                {
                    checkedNames.Add(part);
                    if (!tree.Contains(part))
                    {
                        unresolved.Add($"{doc}: `{name}` names {part}");
                    }
                }
            }
        }
        Assert.Empty(unresolved.Distinct(StringComparer.Ordinal));
        Assert.Empty(MustBeChecked.Except(checkedNames, StringComparer.Ordinal));
    }

    /// <summary>Every word in the sources plus every path segment, so a doc may name a file or a
    /// directory as freely as it names a type.</summary>
    private static HashSet<string> CollectTreeWords()
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        void Walk(string directory)
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (SkippedDirectories.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }
                words.Add(name);
                Walk(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                AddWords(words, Path.GetFileName(file));
                if (Path.GetExtension(file) is ".cs" or ".ts" or ".py")
                {
                    AddWords(words, File.ReadAllText(file));
                }
            }
        }
        Walk(RepoFiles.RepoRoot);
        return words;
    }

    private static void AddWords(HashSet<string> words, string text)
    {
        foreach (Match match in Word.Matches(text))
        {
            words.Add(match.Value);
        }
    }
}
