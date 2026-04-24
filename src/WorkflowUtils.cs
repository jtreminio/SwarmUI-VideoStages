using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VideoStages;

public readonly record struct WorkflowNode(string Id, JObject Node);

public readonly record struct WorkflowInputConnection(string NodeId, string InputName, JArray Connection);

public static class WorkflowUtils
{
    public static IReadOnlyList<WorkflowNode> NodesOfType(JObject workflow, string classType)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(classType);

        List<WorkflowNode> nodes = [];
        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is JObject node && $"{node["class_type"]}" == classType)
            {
                nodes.Add(new WorkflowNode(property.Name, node));
            }
        }
        return nodes;
    }

    public static IReadOnlyList<WorkflowInputConnection> FindInputConnections(JObject workflow, JArray outputRef)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        RequirePairOutputRef(outputRef, nameof(outputRef));

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
                if (input.Value is JArray array && array.Count == 2 && $"{array[0]}" == targetNode && $"{array[1]}" == targetIndex)
                {
                    matches.Add(new WorkflowInputConnection(property.Name, input.Name, array));
                }
            }
        }
        return matches;
    }

    public static int RetargetInputConnections(
        JObject workflow,
        JArray fromOutputRef,
        JArray toOutputRef,
        Func<WorkflowInputConnection, bool> shouldRetarget = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        RequirePairOutputRef(fromOutputRef, nameof(fromOutputRef));
        RequirePairOutputRef(toOutputRef, nameof(toOutputRef));

        int rewritten = 0;
        foreach (WorkflowInputConnection connection in FindInputConnections(workflow, fromOutputRef))
        {
            if (shouldRetarget is not null && !shouldRetarget(connection))
            {
                continue;
            }

            connection.Connection[0] = toOutputRef[0];
            connection.Connection[1] = toOutputRef[1];
            rewritten++;
        }
        return rewritten;
    }

    public static bool IsNodeTypeReachableFromOutput(JObject workflow, JArray outputRef, string classType)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        RequirePairOutputRef(outputRef, nameof(outputRef));
        ArgumentException.ThrowIfNullOrWhiteSpace(classType);

        string startNodeId = $"{outputRef[0]}";
        if (string.IsNullOrWhiteSpace(startNodeId))
        {
            return false;
        }

        Dictionary<string, List<string>> forwardEdges = BuildForwardAdjacency(workflow);
        Queue<string> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNodeId);
        visited.Add(startNodeId);

        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (!workflow.TryGetValue(nodeId, out JToken nodeToken) || nodeToken is not JObject node)
            {
                continue;
            }
            if ($"{node["class_type"]}" == classType)
            {
                return true;
            }

            if (!forwardEdges.TryGetValue(nodeId, out List<string> consumers))
            {
                continue;
            }

            foreach (string consumerId in consumers)
            {
                if (visited.Add(consumerId))
                {
                    pending.Enqueue(consumerId);
                }
            }
        }

        return false;
    }

    public static bool TryResolveNearestUpstreamDecode(
        JObject workflow,
        JArray outputRef,
        out WorkflowNode decodeNode)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        RequirePairOutputRef(outputRef, nameof(outputRef));

        decodeNode = default;
        Queue<JArray> pending = new();
        HashSet<string> visitedNodeIds = [];
        pending.Enqueue(new JArray(outputRef[0], outputRef[1]));

        while (pending.Count > 0)
        {
            JArray currentRef = pending.Dequeue();
            string nodeId = $"{currentRef[0]}";
            if (string.IsNullOrWhiteSpace(nodeId) || !visitedNodeIds.Add(nodeId))
            {
                continue;
            }

            if (!workflow.TryGetValue(nodeId, out JToken nodeToken) || nodeToken is not JObject node)
            {
                continue;
            }

            string classType = $"{node["class_type"]}";
            if (classType == NodeTypes.VAEDecode || classType == NodeTypes.VAEDecodeTiled)
            {
                decodeNode = new WorkflowNode(nodeId, node);
                return true;
            }

            if (node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty input in inputs.Properties())
            {
                foreach (JArray upstreamRef in ExtractNodeRefs(workflow, input.Value))
                {
                    pending.Enqueue(new JArray(upstreamRef[0], upstreamRef[1]));
                }
            }
        }

        return false;
    }

    public static bool TryResolveNearestDownstreamDecodeOutput(
        JObject workflow,
        JArray outputRef,
        out JArray decodeOutputRef)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        RequirePairOutputRef(outputRef, nameof(outputRef));

        decodeOutputRef = null;
        Queue<JArray> pending = new();
        HashSet<string> visitedRefs = [];
        pending.Enqueue(new JArray(outputRef[0], outputRef[1]));

        while (pending.Count > 0)
        {
            JArray currentRef = pending.Dequeue();
            string currentKey = $"{currentRef[0]}::{currentRef[1]}";
            if (!visitedRefs.Add(currentKey))
            {
                continue;
            }

            foreach (WorkflowInputConnection connection in FindInputConnections(workflow, currentRef))
            {
                if (!workflow.TryGetValue(connection.NodeId, out JToken nodeToken) || nodeToken is not JObject node)
                {
                    continue;
                }

                string classType = $"{node["class_type"]}";
                if (classType == NodeTypes.VAEDecode || classType == NodeTypes.VAEDecodeTiled)
                {
                    decodeOutputRef = new JArray(connection.NodeId, 0);
                    return true;
                }

                pending.Enqueue(new JArray(connection.NodeId, 0));
            }
        }

        return false;
    }

    private static void RequirePairOutputRef(JArray outputRef, string paramName)
    {
        if (outputRef is null || outputRef.Count != 2)
        {
            throw new ArgumentException("Expected [nodeId, outputIndex].", paramName);
        }
    }

    private static Dictionary<string, List<string>> BuildForwardAdjacency(JObject workflow)
    {
        Dictionary<string, List<string>> forwardEdges = [];

        foreach (JProperty nodeProperty in workflow.Properties())
        {
            if (nodeProperty.Value is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            string consumerId = nodeProperty.Name;
            foreach (JProperty input in inputs.Properties())
            {
                foreach (JArray upstreamRef in ExtractNodeRefs(workflow, input.Value))
                {
                    string producerId = $"{upstreamRef[0]}";
                    if (string.IsNullOrWhiteSpace(producerId))
                    {
                        continue;
                    }

                    if (!forwardEdges.TryGetValue(producerId, out List<string> consumers))
                    {
                        consumers = [];
                        forwardEdges[producerId] = consumers;
                    }

                    if (!consumers.Contains(consumerId))
                    {
                        consumers.Add(consumerId);
                    }
                }
            }
        }

        return forwardEdges;
    }

    private static IEnumerable<JArray> ExtractNodeRefs(JObject workflow, JToken token)
    {
        if (token is not JArray array)
        {
            yield break;
        }

        if (array.Count == 2
            && array[0] is not null
            && array[1] is JValue value
            && value.Type == JTokenType.Integer)
        {
            string nodeId = $"{array[0]}";
            if (!string.IsNullOrWhiteSpace(nodeId) && workflow.ContainsKey(nodeId))
            {
                yield return array;
                yield break;
            }
        }

        foreach (JToken child in array)
        {
            foreach (JArray nested in ExtractNodeRefs(workflow, child))
            {
                yield return nested;
            }
        }
    }
}
