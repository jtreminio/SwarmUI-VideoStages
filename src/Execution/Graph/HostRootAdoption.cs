using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Execution.Graph;

/// <summary>
/// Lets the first generated stage of a text-to-video timeline build on SwarmUI's own root chain
/// instead of beside it, by taking over the node ids core reserves for its base sampler and decode.
/// <para>
/// The stage already loads its model through core's loader — <see cref="WorkflowGenerator.CreateNode(string, Newtonsoft.Json.Linq.JObject, string, bool)"/>'s
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
    RootPlan root,
    IReadOnlySet<string> ownedRootNodeIds)
{
    /// <summary>Core's reserved base sampler and decode, per its own id map.</summary>
    private const string SamplerNodeId = "10";

    private const string DecodeNodeId = "8";

    private const string LatentNodeId = "5";

    private const string PositiveNodeId = "6";

    private const string NegativeNodeId = "7";

    private bool _claimed;

    /// <summary>
    /// The ids a text stage should build core's root roles under, or nulls to let the host allocate
    /// fresh ones. There is one host root, so this is granted at most once.
    /// <para>
    /// The sampler and decode are always claimed. The empty latent and the conditioning pair are
    /// asked for by a family that builds them through the typed bridge, which never consults
    /// <see cref="WorkflowGenerator.CreateNode(string, Newtonsoft.Json.Linq.JObject, string, bool)"/>'s dedup cache. A family
    /// that builds them through that cache lands on core's nodes unaided, so taking the ids from it
    /// would only cost it the collapse it already gets: claiming an id retires its dedup entry.
    /// </para>
    /// <para>
    /// All or nothing, so a request where core allocates no positive conditioning — an H3 positive
    /// with reference media returns before the reserved id is used — leaves this family without the
    /// claim entirely, and a later clip of another family takes it instead. The shipped graph is the
    /// same either way; only which clip owns core's ids moves.
    /// </para>
    /// </summary>
    internal HostRootClaim ClaimTextRoot(
        ClipPlan clip,
        StagePlan stage,
        bool includeLatent = false,
        bool includeConditioning = false)
    {
        List<string> ids = [SamplerNodeId, DecodeNodeId];
        if (includeLatent)
        {
            ids.Add(LatentNodeId);
        }
        if (includeConditioning)
        {
            ids.AddRange([PositiveNodeId, NegativeNodeId]);
        }
        return TryClaim(clip, stage, ids)
            ? new HostRootClaim
            {
                Sampler = SamplerNodeId,
                Decode = DecodeNodeId,
                Latent = includeLatent ? LatentNodeId : null,
                Positive = includeConditioning ? PositiveNodeId : null,
                Negative = includeConditioning ? NegativeNodeId : null,
            }
            : HostRootClaim.None;
    }

    private bool TryClaim(ClipPlan clip, StagePlan stage, IReadOnlyCollection<string> ids)
    {
        if (_claimed
            || !root.ReplacesTextToVideoRootStage(stage, clip)
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

/// <summary>Which of core's nodes a stage took over, by the role each plays. Null where it took
/// none — an unclaimed role simply leaves core's node to be swept as before.</summary>
internal sealed record HostRootClaim
{
    internal static readonly HostRootClaim None = new();

    internal string Sampler { get; init; }

    internal string Decode { get; init; }

    internal string Latent { get; init; }

    internal string Positive { get; init; }

    internal string Negative { get; init; }
}
