using System.Reflection;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// SwarmUI's statics — <c>Program.T2IModelSets</c>, <c>WorkflowGenerator.Steps</c>/
/// <c>ModelGenSteps</c>, the <c>T2IParamTypes</c> registry, <c>FeaturesSupported</c> — are shared,
/// and the collection also owns <c>GlobalStateFixture</c>'s save/restore. xUnit parallelises across
/// collections, so any class outside it races every class inside.
/// </summary>
[Collection("VideoStagesTests")]
public class TestIsolationContractTests
{
    [Fact]
    public void Every_test_class_joins_the_shared_collection()
    {
        string[] offenders = [.. typeof(TestIsolationContractTests).Assembly
            .GetTypes()
            .Where(IsTestClass)
            .Where(type => CollectionNameOf(type) != "VideoStagesTests")
            .Select(type => type.FullName)
            .Order()];

        Assert.True(
            offenders.Length == 0,
            "Test classes outside the \"VideoStagesTests\" collection run in parallel with it "
                + "and race on SwarmUI's static state. Add [Collection(\"VideoStagesTests\")] to:\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>xUnit v2's CollectionAttribute keeps its name in the constructor argument only.</summary>
    private static string CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments[0].Value as string;

    private static bool IsTestClass(Type type) =>
        type.IsClass
        && !type.IsAbstract
        && type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(method => method.GetCustomAttribute<FactAttribute>() is not null);
}
