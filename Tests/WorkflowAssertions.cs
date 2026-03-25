using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

internal static class WorkflowAssertions
{
    public static WorkflowNode RequireNodeById(JObject workflow, string id)
    {
        Assert.NotNull(workflow);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(workflow.TryGetValue(id, out JToken token), $"Expected workflow to contain node id '{id}'.");
        Assert.True(token is JObject, $"Expected workflow node '{id}' to be an object.");
        return new WorkflowNode(id, (JObject)token);
    }

    public static WorkflowNode RequireNodeOfType(JObject workflow, string classType)
    {
        IReadOnlyList<WorkflowNode> nodes = WorkflowUtils.NodesOfType(workflow, classType);
        Assert.NotEmpty(nodes);
        return nodes[0];
    }

    public static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .SelectMany(type => WorkflowUtils.NodesOfType(workflow, type))
            .ToList();

    public static JArray RequireConnectionInput(JObject node, params string[] preferredKeys)
    {
        Assert.NotNull(node);
        Assert.True(node["inputs"] is JObject, "Expected node to have an 'inputs' object.");
        JObject inputs = (JObject)node["inputs"];

        foreach (string key in preferredKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && inputs.TryGetValue(key, out JToken token) && token is JArray array && array.Count == 2)
            {
                return array;
            }
        }

        foreach (JProperty property in inputs.Properties())
        {
            if (property.Value is JArray array && array.Count == 2)
            {
                return array;
            }
        }

        Assert.Fail("Expected at least one [nodeId, outputIndex] connection input.");
        return null;
    }
}
