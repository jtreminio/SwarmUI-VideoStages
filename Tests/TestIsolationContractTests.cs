using System.Reflection;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
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

    /// <summary>
    /// <see cref="UnitTestStubs.EnsureComfyWorkflowParamsRegistered"/> hand-mirrors core's own
    /// registrations, so a parameter core adds stays null here. Core reads those statics through
    /// <c>T2IParamInput.TryGet</c>, which dereferences <c>param.Type</c> before any lookup, so the
    /// miss surfaces as a NullReferenceException inside core from every workflow test at once
    /// rather than as a missing registration.
    /// </summary>
    [Fact]
    public void Every_core_comfy_parameter_has_a_test_registration()
    {
        UnitTestStubs.EnsureComfyWorkflowParamsRegistered();

        string[] unregistered = [.. typeof(ComfyUIBackendExtension)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(UnregisteredNames)
            .Order()];

        Assert.True(
            unregistered.Length == 0,
            "Core registers these ComfyUI parameters that the test harness does not, so any core "
                + "step reading one throws inside SwarmUI. Add an Ensure(...) line for each to "
                + "UnitTestStubs.EnsureComfyWorkflowParamsRegistered:\n  "
                + string.Join("\n  ", unregistered));
    }

    private static IEnumerable<string> UnregisteredNames(FieldInfo field)
    {
        if (IsRegisteredParam(field.FieldType))
        {
            return field.GetValue(null) is null ? [field.Name] : [];
        }
        if (!IsRegisteredParam(field.FieldType.GetElementType()))
        {
            return [];
        }
        Array slots = (Array)field.GetValue(null) ?? Array.CreateInstance(field.FieldType, 0);
        return Enumerable.Range(0, slots.Length)
            .Where(index => slots.GetValue(index) is null)
            .Select(index => $"{field.Name}[{index}]");
    }

    private static bool IsRegisteredParam(Type type) =>
        type is { IsGenericType: true }
        && type.GetGenericTypeDefinition() == typeof(T2IRegisteredParam<>);

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
