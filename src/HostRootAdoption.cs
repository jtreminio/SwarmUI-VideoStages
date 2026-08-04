using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Lets the first generated stage of a text-to-video timeline build on SwarmUI's own root chain
/// instead of beside it, by taking over the node ids core reserves for its base sampler and decode.
/// <para>
/// The stage already loads its model through core's loader, and already samples core's empty latent
/// and conditioning pair — <see cref="WorkflowGenerator.CreateNode"/>'s dedup cache collapses those
/// onto core's nodes on its own. The sampler and decode cannot collapse that way, because a stage's
/// seed and step range differ from the request's, so they are claimed by id instead.
/// </para>
/// <para>
/// A claim is only ever offered on a node the root cleanup already owns — one it would delete as
/// displaced. Everything the cleanup spares, it spares because some other sink still holds it, and
/// overwriting such a node would hand that sink this stage's output instead. Nothing else has to
/// change for the claim to hold: <c>RemoveOwnedNodesNotLive</c> is liveness-based, so a core node
/// a stage samples through is simply kept.
/// </para>
/// </summary>
internal sealed class HostRootAdoption(
    WorkflowGenerator generator,
    RootExecutionPolicy rootPolicy,
    IReadOnlySet<string> ownedRootNodeIds)
{
    /// <summary>Core's reserved base sampler and decode, per its own id map.</summary>
    private const string SamplerNodeId = "10";

    private const string DecodeNodeId = "8";

    private static readonly string[] ClaimedNodeIds = [SamplerNodeId, DecodeNodeId];

    private bool _claimed;

    /// <summary>
    /// The ids a text stage should build its sampler and decode under, or nulls to let the host
    /// allocate fresh ones. There is one host root, so this is granted at most once.
    /// </summary>
    internal (string Sampler, string Decode) ClaimTextRoot(ClipPlan clip, StagePlan stage)
    {
        if (_claimed
            || !rootPolicy.ReplacesTextToVideoRootStage(stage, clip)
            || !ClaimedNodeIds.All(id => generator.HasNode(id) && ownedRootNodeIds.Contains(id))
            // A capture resolves to a node id rather than to a graph edge, so it survives the
            // ownership test above and has to be excluded on its own.
            || VideoGraphHelpers.IsCapturedByExtension(generator.NodeHelpers, ClaimedNodeIds))
        {
            return (null, null);
        }
        _claimed = true;
        // The dedup entries still describe core's sampler and decode, which these ids are about to
        // stop being.
        VideoGraphHelpers.InvalidateForRemovedNodes(generator.NodeHelpers, ClaimedNodeIds);
        return (SamplerNodeId, DecodeNodeId);
    }
}
