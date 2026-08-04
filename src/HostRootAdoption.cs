using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Lets the first generated stage of a text-to-video timeline build on SwarmUI's own root chain
/// instead of beside it, by taking over the node ids core reserves for its base sampler and decode.
/// <para>
/// The stage already loads its model through core's loader — <see cref="WorkflowGenerator.CreateNode"/>'s
/// dedup cache collapses that onto core's node on its own, as it does for the empty latent and the
/// conditioning pair of every family that builds them through it. The sampler and decode cannot
/// collapse that way, because a stage's seed and step range differ from the request's, so they are
/// claimed by id instead — as is anything a family builds outside the dedup cache's reach.
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

    private const string LatentNodeId = "5";

    private bool _claimed;

    /// <summary>
    /// The ids a text stage should build its sampler and decode under, or nulls to let the host
    /// allocate fresh ones. There is one host root, so this is granted at most once.
    /// </summary>
    internal (string Sampler, string Decode) ClaimTextRoot(ClipPlan clip, StagePlan stage)
    {
        string[] ids = [SamplerNodeId, DecodeNodeId];
        return TryClaim(clip, stage, ids)
            ? (SamplerNodeId, DecodeNodeId)
            : (null, null);
    }

    /// <summary>
    /// The same claim, plus core's empty latent — for a stage that builds its latent through the
    /// typed bridge, which never consults <see cref="WorkflowGenerator.CreateNode"/>'s dedup cache.
    /// Every other architecture builds its latent through that cache and lands on core's node
    /// unaided, so taking the id from them would only cost them the collapse they already get.
    /// </summary>
    internal (string Sampler, string Decode, string Latent) ClaimTextRootWithLatent(
        ClipPlan clip,
        StagePlan stage)
    {
        string[] ids = [SamplerNodeId, DecodeNodeId, LatentNodeId];
        return TryClaim(clip, stage, ids)
            ? (SamplerNodeId, DecodeNodeId, LatentNodeId)
            : (null, null, null);
    }

    private bool TryClaim(ClipPlan clip, StagePlan stage, IReadOnlyCollection<string> ids)
    {
        if (_claimed
            || !rootPolicy.ReplacesTextToVideoRootStage(stage, clip)
            || !ids.All(id => generator.HasNode(id) && ownedRootNodeIds.Contains(id))
            // A capture resolves to a node id rather than to a graph edge, so it survives the
            // ownership test above and has to be excluded on its own.
            || VideoGraphHelpers.IsCapturedByExtension(generator.NodeHelpers, ids))
        {
            return false;
        }
        _claimed = true;
        // The dedup entries still describe core's nodes, which these ids are about to stop being.
        VideoGraphHelpers.InvalidateForRemovedNodes(generator.NodeHelpers, ids);
        return true;
    }
}
