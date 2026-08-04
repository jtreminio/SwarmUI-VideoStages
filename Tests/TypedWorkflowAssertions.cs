using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

public readonly record struct WorkflowNode(string Id, JObject Node);

internal static class TypedWorkflowAssertions
{
    /// <summary>Fails when the graph has a dependency cycle (Comfy refuses to run such a workflow).</summary>
    public static void AssertAcyclic(WorkflowBridge bridge)
    {
        Dictionary<string, int> state = [];
        void Visit(ComfyNode node)
        {
            if (state.TryGetValue(node.Id, out int seen))
            {
                if (seen == 1)
                {
                    Assert.Fail($"Workflow contains a cycle through node {node.Id} ({node.ClassTypeName}).");
                }
                return;
            }
            state[node.Id] = 1;
            foreach (ComfyNode upstream in bridge.Graph.FindUpstream(node))
            {
                Visit(upstream);
            }
            state[node.Id] = 2;
        }
        foreach (ComfyNode node in bridge.Graph.Nodes.Values)
        {
            Visit(node);
        }
    }

    /// <summary>
    /// Fails when any input references a node id the workflow no longer contains. Comfy raises
    /// <c>NodeNotFoundError: Node N not found</c> at execution time for exactly this state.
    /// </summary>
    public static void AssertNoDanglingNodeRefs(JObject workflow)
    {
        foreach (JProperty node in workflow.Properties())
        {
            if (node.Value is not JObject { } nodeObj || nodeObj["inputs"] is not JObject inputs)
            {
                continue;
            }
            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is not JArray { Count: 2 } path
                    || path[1].Type != JTokenType.Integer)
                {
                    continue;
                }
                Assert.True(
                    workflow[$"{path[0]}"] is JObject,
                    $"Node {node.Name} ({nodeObj["class_type"]}) input '{input.Name}' references "
                        + $"missing node {path[0]}.");
            }
        }
    }

    /// <summary>
    /// The three whole-graph checks every generated-workflow test closes with: nothing built is
    /// orphaned, no input references a pruned node, and the graph is acyclic.
    /// </summary>
    public static void AssertShippable(
        WorkflowBridge bridge,
        JObject workflow,
        WorkflowLivePath live)
    {
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>The joint latent a stage sampler consumes.</summary>
    public static LTXVConcatAVLatentNode JointLatentOf(SwarmKSamplerNode sampler) =>
        Assert.IsType<LTXVConcatAVLatentNode>(sampler.LatentImage.Connection?.Node);

    /// <summary>The split of a stage's sampled joint latent — its video and audio handoff.</summary>
    public static LTXVSeparateAVLatentNode OutputOf(
        WorkflowBridge bridge,
        SwarmKSamplerNode sampler) =>
        Assert.Single(
            bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>(),
            separate => ReferenceEquals(separate.AvLatent.Connection?.Node, sampler));

    /// <summary>
    /// Core's base-image decode, the node an authored <c>Base</c> reference must resolve to. Only
    /// the image-to-video shape has one. This overload identifies it as the graph's only plain
    /// <c>VAEDecode</c>, which holds on LTX but not on architectures whose video half decodes
    /// through one too — those must name the base sampler.
    /// </summary>
    public static VAEDecodeNode BaseImage(WorkflowBridge bridge) =>
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeNode>());

    /// <summary>Core's base-image decode, selected by the base pass it decodes.</summary>
    public static VAEDecodeNode BaseImage(WorkflowBridge bridge, SwarmKSamplerNode baseSampler) =>
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => ReferenceEquals(decode.Samples.Connection?.Node, baseSampler));

    /// <summary>
    /// Selects a sampler by its seed. Under the production step list core can contribute samplers
    /// of its own, so counting or indexing samplers cannot identify a stage.
    /// </summary>
    public static SwarmKSamplerNode StageSampler(
        IEnumerable<SwarmKSamplerNode> samplers,
        long seed) =>
        Assert.Single(samplers, sampler => sampler.NoiseSeed.LiteralAsLong() == seed);

    /// <summary>
    /// The sampler for a stage, by its flat-across-clips index. Ambiguous where the host root
    /// survives — core's own image-to-video sampler shares stage 0's seed — so those tests must
    /// select by reachability instead.
    /// </summary>
    public static SwarmKSamplerNode StageSampler(WorkflowBridge bridge, int stageId) =>
        StageSampler(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            VideoStagesWorkflowFixture.StageSeed(stageId));

    /// <summary>
    /// For architectures whose compat class sets <c>LorasTargetTextEnc</c> false: the authored
    /// textEncoderWeight is dropped and there is no strength_clip.
    /// </summary>
    public static void AssertModelOnlyLora(
        IEnumerable<ComfyNode> loras,
        string fileName,
        double modelWeight)
    {
        ComfyNode lora = Assert.Single(
            loras,
            node => node.FindInput("lora_name").LiteralAsString() == fileName);
        Assert.IsType<LoraLoaderModelOnlyNode>(lora);
        Assert.Equal(modelWeight, lora.FindInput("strength_model").LiteralAsDouble());
        Assert.Null(lora.FindInput("strength_clip"));
    }

    /// <summary>
    /// An unconnected input is fine — the reference was dropped — so callers must assert
    /// connectedness themselves.
    /// </summary>
    public static void AssertImageSource(ComfyNode source, string inputName)
    {
        if (source is null)
        {
            return;
        }
        Assert.False(
            source is EmptyMiniMaxH3LatentAVNode or LTXVConcatAVLatentNode
                or SetLatentNoiseMaskNode or EmptyLatentImageNode or VAEEncodeNode
                or SwarmKSamplerNode or KSamplerAdvancedNode,
            $"{inputName} is fed by {source.ClassTypeName} (node {source.Id}), which produces a "
                + "latent, not an image.");
    }

    public static List<SwarmKSamplerNode> SamplerNodesOrdered(WorkflowBridge bridge)
    {
        return bridge.Graph.NodesOfType<SwarmKSamplerNode>()
            .OrderBy(n => int.Parse(n.Id))
            .ToList();
    }

    public static List<ComfyNode> LoraLoaderNodesOf(WorkflowBridge bridge)
    {
        return bridge.Graph.Nodes.Values
            .Where(n => n is LoraLoaderNode or LoraLoaderModelOnlyNode)
            .ToList();
    }

    public static T RequireTypedNode<T>(WorkflowBridge bridge, string id) where T : ComfyNode
    {
        T node = bridge.Graph.GetNode<T>(id);
        Assert.NotNull(node);
        return node;
    }

    public static WorkflowNode AsWorkflowNode(ComfyNode node, JObject workflow)
    {
        Assert.True(workflow[node.Id] is JObject, $"Expected workflow to contain node id '{node.Id}'.");
        return new WorkflowNode(node.Id, (JObject)workflow[node.Id]);
    }

    /// <summary>
    /// The browser-visible warnings <c>PlanDiagnosticReporter.TrackRequestWarning</c> accumulates.
    /// The API route's generator carries the same input, so these are readable off a generated
    /// workflow as well as off a hand-built generator.
    /// </summary>
    public static List<string> RequestWarnings(T2IParamInput input) =>
        Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]);

    /// <summary>
    /// Inclusive variant of <see cref="ComfyGraph.IsReachableUpstream"/>: returns true if
    /// <paramref name="start"/> IS the target node, or if <paramref name="targetNodeId"/>
    /// is reachable by walking upstream from it.
    /// </summary>
    public static bool ReachesUpstream(WorkflowBridge bridge, ComfyNode start, string targetNodeId) =>
        start is not null
        && (start.Id == targetNodeId || bridge.Graph.IsReachableUpstream(start, targetNodeId));
}
