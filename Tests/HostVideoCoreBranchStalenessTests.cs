using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed partial class HostVideoCoreBranchStalenessTests
{
    private enum SelectorKind
    {
        ModelClass,
        CompatibilityClass,
    }

    private sealed record CoreBranchSelector(
        SelectorKind Kind,
        string Value);

    [Fact]
    public void Every_named_core_image_to_video_branch_has_a_proven_owner()
    {
        using SwarmUiTestContext context = new();
        string sourcePath = CoreWorkflowGeneratorPath();
        string source = File.ReadAllText(sourcePath);
        string method = ExtractPrepFullCond(source);
        IReadOnlyList<string> branchConditions = ExtractTopLevelBranchConditions(method);

        Assert.NotEmpty(branchConditions);
        List<CoreBranchSelector> selectors = [];
        foreach (string condition in branchConditions)
        {
            IReadOnlyList<CoreBranchSelector> branchSelectors =
                ExtractSelectors(condition);
            Assert.True(
                branchSelectors.Count > 0,
                "The core image-to-video branch sentinel could not interpret condition "
                    + $"'{Condense(condition)}'. Update the parser and decide whether the branch "
                    + "belongs in HostVideoArchitectureModule.ProvenPaths or a specialized "
                    + "VideoStages architecture.");
            selectors.AddRange(branchSelectors);
        }

        foreach (CoreBranchSelector selector in selectors.Distinct())
        {
            Assert.True(
                HasProvenOwner(selector),
                $"Core image-to-video branch selector '{SelectorName(selector)}' has no proven "
                    + "VideoStages owner. Add the exact compatibility/model pair to "
                    + "HostVideoArchitectureModule.ProvenPaths, add a specialized architecture, "
                    + "or explicitly reject the new core path.");
        }
    }

    private static bool HasProvenOwner(CoreBranchSelector selector)
    {
        if (selector.Kind == SelectorKind.ModelClass)
        {
            bool hostOwnsModel =
                HostVideoArchitectureModule.ProvenPaths.Any(path =>
                    string.Equals(
                        path.ModelClassId,
                        selector.Value,
                        StringComparison.OrdinalIgnoreCase));
            if (hostOwnsModel)
            {
                return true;
            }
            return selector.Value switch
            {
                "wan-2_2-image2video-14b" => HasSpecializedOwner(
                    selector.Value,
                    T2IModelClassSorter.CompatWan21_14b),
                _ => false,
            };
        }

        bool hostOwnsCompatibility =
            HostVideoArchitectureModule.ProvenPaths.Any(path =>
                string.Equals(
                    path.CompatibilityClassId,
                    selector.Value,
                    StringComparison.Ordinal));
        if (hostOwnsCompatibility)
        {
            return true;
        }
        return selector.Value switch
        {
            "wan-21-14b" => HasSpecializedOwner(
                "wan-2_1-image2video-14b",
                T2IModelClassSorter.CompatWan21_14b),
            "wan-21-1_3b" => HasSpecializedOwner(
                "wan-2_1-image2video-1_3b",
                T2IModelClassSorter.CompatWan21_1_3b),
            "wan-22-5b" => HasSpecializedOwner(
                "wan-2_2-ti2v-5b",
                T2IModelClassSorter.CompatWan22_5b),
            _ => false,
        };
    }

    private static bool HasSpecializedOwner(
        string modelClassId,
        T2IModelCompatClass compatibility)
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = modelClassId,
            CompatClass = compatibility,
        };
        return VideoArchitectureRegistry.Production.TryResolveModel(
                models.VideoModel,
                out ResolvedVideoModel resolved)
            && resolved.ArchitectureId
                != HostVideoArchitectureModule.ArchitectureId;
    }

    private static IReadOnlyList<CoreBranchSelector> ExtractSelectors(
        string condition)
    {
        List<CoreBranchSelector> selectors = [];
        selectors.AddRange(
            ModelClassLiteralRegex().Matches(condition)
                .Select(match => new CoreBranchSelector(
                    SelectorKind.ModelClass,
                    match.Groups["value"].Value)));
        selectors.AddRange(
            CompatibilityLiteralRegex().Matches(condition)
                .Select(match => new CoreBranchSelector(
                    SelectorKind.CompatibilityClass,
                    match.Groups["value"].Value)));
        foreach (Match match in CompatibilityConstantRegex().Matches(condition))
        {
            string symbol = match.Groups["symbol"].Value;
            FieldInfo field = typeof(T2IModelClassSorter).GetField(
                symbol,
                BindingFlags.Public | BindingFlags.Static);
            T2IModelCompatClass compatibility =
                Assert.IsType<T2IModelCompatClass>(field?.GetValue(null));
            selectors.Add(new(
                SelectorKind.CompatibilityClass,
                compatibility.ID));
        }
        return selectors
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractTopLevelBranchConditions(
        string method)
    {
        List<string> conditions = [];
        foreach (Match match in TopLevelBranchStartRegex().Matches(method))
        {
            int open = method.IndexOf('(', match.Index);
            int depth = 0;
            for (int index = open; index < method.Length; index++)
            {
                depth += method[index] == '(' ? 1 : 0;
                depth -= method[index] == ')' ? 1 : 0;
                if (depth == 0)
                {
                    conditions.Add(method[(open + 1)..index]);
                    break;
                }
            }
        }
        return conditions;
    }

    private static string ExtractPrepFullCond(string source)
    {
        const string signature = "public void PrepFullCond(";
        const string nextMember =
            "/// <summary>Creates the execution logic for an Image-To-Video model.";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int end = source.IndexOf(nextMember, start, StringComparison.Ordinal);
        Assert.True(
            start >= 0 && end > start,
            "Could not locate WorkflowGenerator.ImageToVideoGenInfo.PrepFullCond in SwarmUI "
                + "core. Update the temporary host-video branch sentinel for the new core API.");
        return source[start..end];
    }

    private static string SelectorName(CoreBranchSelector selector) =>
        selector.Kind == SelectorKind.ModelClass
            ? $"model:{selector.Value}"
            : $"compatibility:{selector.Value}";

    private static string Condense(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private static string CoreWorkflowGeneratorPath(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "..",
            "..",
            "BuiltinExtensions",
            "ComfyUIBackend",
            "WorkflowGenerator.cs"));

    [GeneratedRegex(
        @"VideoModel\.ModelClass\?\.ID\s*==\s*""(?<value>[^""]+)""")]
    private static partial Regex ModelClassLiteralRegex();

    [GeneratedRegex(
        @"VideoModel\.ModelClass\?\.CompatClass\?\.ID\s*==\s*""(?<value>[^""]+)""")]
    private static partial Regex CompatibilityLiteralRegex();

    [GeneratedRegex(
        @"VideoModel\.ModelClass\?\.CompatClass\?\.ID\s*==\s*"
            + @"T2IModelClassSorter\.(?<symbol>[A-Za-z0-9_]+)\.ID")]
    private static partial Regex CompatibilityConstantRegex();

    [GeneratedRegex(@"(?m)^ {12}(?:else )?if\s*\(")]
    private static partial Regex TopLevelBranchStartRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
