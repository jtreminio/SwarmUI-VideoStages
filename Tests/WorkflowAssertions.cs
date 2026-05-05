using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

public readonly record struct WorkflowNode(string Id, JObject Node);

public readonly record struct WorkflowInputConnection(string NodeId, string InputName, JArray Connection);

internal static class WorkflowAssertions
{
    public static IReadOnlyList<WorkflowNode> NodesOfType(JObject workflow, string classType)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(classType);

        List<WorkflowNode> nodes = [];
        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is JObject node && StringUtils.NodeTypeMatches(node, classType))
            {
                nodes.Add(new WorkflowNode(property.Name, node));
            }
        }
        return nodes;
    }

    public static IReadOnlyList<WorkflowInputConnection> FindInputConnections(JObject workflow, JArray outputRef)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (outputRef is null || outputRef.Count != 2)
        {
            throw new ArgumentException("Expected [nodeId, outputIndex].", nameof(outputRef));
        }

        string targetNode = $"{outputRef[0]}";
        string targetIndex = $"{outputRef[1]}";
        List<WorkflowInputConnection> matches = [];
        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is not JArray array || array.Count != 2)
                {
                    continue;
                }
                if ($"{array[0]}" == targetNode && $"{array[1]}" == targetIndex)
                {
                    matches.Add(new WorkflowInputConnection(property.Name, input.Name, array));
                }
            }
        }
        return matches;
    }

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
        IReadOnlyList<WorkflowNode> nodes = NodesOfType(workflow, classType);
        Assert.NotEmpty(nodes);
        return nodes[0];
    }

    public static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .SelectMany(type => NodesOfType(workflow, type))
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
