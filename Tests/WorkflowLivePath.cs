using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Node-count and wiring assertions stay green when a subgraph is built correctly and then
/// orphaned — still present, still wired to its own inputs, contributing nothing to the output.
/// Asserting the live path separates "the graph contains this" from "the graph runs this".
/// <para>
/// Downstream edges invert <see cref="ComfyGraph.FindUpstream"/> — see
/// <c>WorkflowGraphCleanup</c> for why reading node inputs directly is wrong.
/// </para>
/// </summary>
internal sealed class WorkflowLivePath : IDisposable
{
    private readonly WorkflowBridge _bridge;
    private readonly bool _ownsBridge;
    private readonly Dictionary<string, List<ComfyNode>> _downstream;

    private WorkflowLivePath(WorkflowBridge bridge, bool ownsBridge)
    {
        _bridge = bridge;
        _ownsBridge = ownsBridge;
        _downstream = [];
        foreach (ComfyNode node in bridge.Graph.Nodes.Values)
        {
            foreach (ComfyNode upstream in bridge.Graph.FindUpstream(node))
            {
                if (!_downstream.TryGetValue(upstream.Id, out List<ComfyNode> consumers))
                {
                    consumers = [];
                    _downstream[upstream.Id] = consumers;
                }
                consumers.Add(node);
            }
        }
    }

    public static WorkflowLivePath Open(JObject workflow) =>
        new(WorkflowBridge.Create(workflow), ownsBridge: true);

    /// <summary>A live path over a bridge the caller made and keeps; disposing this leaves that
    /// bridge open.</summary>
    public static WorkflowLivePath For(WorkflowBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return new WorkflowLivePath(bridge, ownsBridge: false);
    }

    public WorkflowBridge Bridge => _bridge;

    /// <summary>
    /// The four whole-graph checks every generated-workflow test closes with: nothing built is
    /// orphaned, no input references a pruned node, the graph is acyclic, and the video is
    /// published exactly once.
    /// </summary>
    public void AssertShippable(int publishedVideoSaves = 1)
    {
        AssertNoOrphanNodes();
        AssertPublishedSaveCount(publishedVideoSaves);
        TypedWorkflowAssertions.AssertNoDanglingNodeRefs(_bridge.Workflow);
        TypedWorkflowAssertions.AssertAcyclic(_bridge);
    }

    public void Dispose()
    {
        if (_ownsBridge)
        {
            _bridge.Dispose();
        }
    }

    /// <summary>
    /// The published video save. With per-stage intermediate saves present, the published one is
    /// the save every other save's inputs ultimately feed, so it has the largest ancestor set.
    /// </summary>
    public SwarmSaveAnimationWSNode FinalVideoSave()
    {
        List<SwarmSaveAnimationWSNode> saves =
            [.. _bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.True(
            saves.Count > 0,
            "Workflow publishes no SwarmSaveAnimationWS node, so nothing it builds is saved.");
        return saves
            .OrderByDescending(save => Ancestors(save).Count)
            .ThenBy(save => save.Id)
            .First();
    }

    /// <summary>Whatever feeds the published save's audio input, if anything does.</summary>
    public ComfyNode PublishedAudio() => FinalVideoSave().Audio.Connection?.Node;

    /// <summary>
    /// A duplicate publication is invisible to every other check here: <see cref="FinalVideoSave"/>
    /// picks the widest save and never counts, and <see cref="OrphanNodes"/> excludes output nodes,
    /// so a second <c>SwarmSaveAnimationWS</c> would ship the same video twice unnoticed. Only
    /// <c>outputintermediateimages</c> legitimately produces more than one.
    /// </summary>
    public void AssertPublishedSaveCount(int expected)
    {
        string[] saves = [.. _bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()
            .Select(save => $"{save.ClassTypeName}#{save.Id}")
            .Order()];
        Assert.True(
            saves.Length == expected,
            $"Workflow publishes {saves.Length} video save(s), expected {expected}:\n  "
                + string.Join("\n  ", saves));
    }

    /// <summary>A save node satisfies the downstream half itself.</summary>
    public void AssertLive(ComfyNode node, string because = null)
    {
        Assert.True(node is not null, $"Cannot check the live path of a null node. {because}".Trim());
        string label = $"{node.ClassTypeName} (node {node.Id})";
        string suffix = because is null ? "" : $" {because}";

        IReadOnlyList<ComfyNode> roots = RootsOf(node);
        Assert.True(
            roots.Count > 0,
            $"{label} descends from no parentless root node, so nothing feeds it.{suffix}");

        SwarmSaveAnimationWSNode save = FinalVideoSave();
        Assert.True(
            node.Id == save.Id || Descendants(node).Any(down => down.Id == save.Id),
            $"{label} does not reach the published video save (node {save.Id}); it is built but "
                + $"orphaned. Its roots were: "
                + $"{string.Join(", ", roots.Select(root => $"{root.ClassTypeName}#{root.Id}"))}."
                + suffix);
    }

    public void AssertAllLive(params ComfyNode[] nodes)
    {
        foreach (ComfyNode node in nodes)
        {
            AssertLive(node);
        }
    }

    /// <summary>
    /// Every node that reaches no output at all, as <c>ClassTypeName#Id</c>, sorted. A test pinning
    /// a known orphan asserts against this rather than dropping
    /// <see cref="AssertNoOrphanNodes"/>, which would drop coverage of every other orphan too.
    /// </summary>
    public string[] OrphanNodes()
    {
        // Every output counts, not just the published one: a node feeding an intermediate save
        // still gets built and still contributes something the user sees.
        ComfyNode[] outputs = [.. _bridge.Graph.Nodes.Values.Where(IsOutputNode)];
        Assert.True(
            outputs.Length > 0,
            "Workflow has no output node, so nothing it builds is saved.");
        HashSet<string> live = [.. outputs.SelectMany(
            output => Ancestors(output).Append(output).Select(node => node.Id))];
        return [.. _bridge.Graph.Nodes.Values
            .Where(node => !live.Contains(node.Id) && !IsOutputNode(node))
            .Select(node => $"{node.ClassTypeName}#{node.Id}")
            .Order()];
    }

    /// <summary>No node reaches no output at all. Weaker than <see cref="AssertLive"/>, which
    /// requires the published save.</summary>
    public void AssertNoOrphanNodes()
    {
        string[] orphans = OrphanNodes();
        Assert.True(
            orphans.Length == 0,
            "Workflow contains nodes that reach no output — Comfy would build them for nothing:\n  "
                + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// A node fed only by literals — an empty latent, a loader — is a root itself and so is its
    /// own answer here.
    /// </summary>
    private IReadOnlyList<ComfyNode> RootsOf(ComfyNode node) =>
        [.. Ancestors(node).Append(node)
            .Where(candidate => !_bridge.Graph.FindUpstream(candidate).Any())
            .OrderBy(candidate => candidate.Id)];

    private static bool IsOutputNode(ComfyNode node) =>
        node is SwarmSaveAnimationWSNode or SwarmSaveImageWSNode or SaveImageNode or PreviewImageNode;

    private HashSet<ComfyNode> Ancestors(ComfyNode node) =>
        Walk(node, current => _bridge.Graph.FindUpstream(current));

    private HashSet<ComfyNode> Descendants(ComfyNode node) =>
        Walk(node, current => _downstream.TryGetValue(current.Id, out List<ComfyNode> next)
            ? next
            : []);

    private static HashSet<ComfyNode> Walk(
        ComfyNode start,
        Func<ComfyNode, IEnumerable<ComfyNode>> next)
    {
        HashSet<string> seen = [start.Id];
        HashSet<ComfyNode> found = [];
        Queue<ComfyNode> queue = new([start]);
        while (queue.Count > 0)
        {
            foreach (ComfyNode neighbour in next(queue.Dequeue()))
            {
                if (seen.Add(neighbour.Id))
                {
                    found.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
        }
        return found;
    }
}
