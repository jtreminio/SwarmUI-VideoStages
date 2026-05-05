using ComfyTyped.Core;
using Newtonsoft.Json.Linq;

namespace VideoStages.Typed;

/// <summary>
/// Convenience accessors for reading literal values off typed node inputs.
///
/// <para>Why this exists:</para>
/// <para>
/// <see cref="INodeInput.LiteralValue"/> is typed as <c>object?</c>, and the underlying boxed
/// type depends on how the input was set:
/// </para>
/// <list type="bullet">
///   <item>Set in C# via <c>input.Set(42)</c> → boxed <c>int</c>.</item>
///   <item>Set in C# via <c>input.Set(42L)</c> → boxed <c>long</c>.</item>
///   <item>Loaded from a <c>JObject</c> via <see cref="ComfyGraph.FromWorkflow"/> → boxed
///         <c>long</c> (Newtonsoft normalizes integer JSON to <c>long</c>).</item>
/// </list>
/// <para>
/// A naive <c>(long?)input.LiteralValue</c> cast throws <see cref="System.InvalidCastException"/>
/// when the underlying type is a boxed <c>int</c>, even though the value would convert losslessly.
/// These helpers paper over that by accepting either underlying type.
/// </para>
///
/// <para>TODO: migrate this into ComfyTyped (likely <c>ComfyTyped.Core</c> as extensions on
/// <see cref="INodeInput"/>). The footgun isn't VideoStages-specific; any consumer mixing
/// <c>Set(int)</c> with bridge round-trips will hit it.</para>
/// </summary>
internal static class NodeSlotExtensions
{
    /// <summary>
    /// Read the input's literal value as an <see cref="int"/>, accepting boxed <c>int</c> or
    /// <c>long</c>. Returns null when the input is unset, connected, or holds a non-integer
    /// type. <c>long</c> values are narrowed via unchecked cast.
    /// </summary>
    public static int? LiteralAsInt(this INodeInput input) => input?.LiteralValue switch
    {
        int i => i,
        long l => (int)l,
        _ => null,
    };

    /// <summary>
    /// Read the input's literal value as a <see cref="long"/>, accepting boxed <c>int</c> or
    /// <c>long</c>. Returns null when the input is unset, connected, or holds a non-integer
    /// type.
    /// </summary>
    public static long? LiteralAsLong(this INodeInput input) => input?.LiteralValue switch
    {
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>
    /// Read the input's literal value as a <see cref="string"/>. Returns null when the input is
    /// unset, connected, or holds a non-string type.
    /// </summary>
    public static string LiteralAsString(this INodeInput input) =>
        input?.LiteralValue as string;

    /// <summary>
    /// Read the input's literal value as a <see cref="double"/>, accepting any boxed numeric
    /// type that converts losslessly. Returns null when the input is unset, connected, or holds
    /// a non-numeric type.
    /// </summary>
    public static double? LiteralAsDouble(this INodeInput input) => input?.LiteralValue switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>
    /// Read the input's literal value as a <see cref="bool"/>. Returns null when the input is
    /// unset, connected, or holds a non-boolean type.
    /// </summary>
    public static bool? LiteralAsBool(this INodeInput input) => input?.LiteralValue switch
    {
        bool b => b,
        _ => null,
    };

    /// <summary>
    /// Resolve a <c>[nodeId, slotIndex]</c> path to a typed <see cref="INodeOutput"/>, with a
    /// fallback that materializes the slot on an <see cref="UnknownNode"/> when no other
    /// node has referenced it yet.
    ///
    /// <para>Why the fallback exists:</para>
    /// <para>
    /// <see cref="ComfyGraph.FromWorkflow"/> populates <c>UnknownNode</c> outputs only for
    /// slots that *appear* in some other node's inputs. A node that nobody references yet
    /// — e.g. a freshly seeded test stub like <c>UnitTest_Base2EditPublishedImage</c> — will
    /// have zero outputs registered, so <see cref="WorkflowBridge.ResolvePath"/> returns
    /// <c>null</c>. Calling <see cref="UnknownNode.GetOutput"/> on demand creates the slot
    /// (typed as <c>AnyType</c>, which connects to anything via <c>ConnectToUntyped</c>).
    /// </para>
    ///
    /// <para>TODO: migrate this into ComfyTyped. The right home is probably
    /// <c>WorkflowBridge.ResolvePath</c> itself — it already knows the node is an
    /// <c>UnknownNode</c> and could synthesize the output instead of returning null. See
    /// <c>COMFYTYPED_UPSTREAM_TODO.md</c>.</para>
    /// </summary>
    public static INodeOutput ResolveOrSynthesizePath(this WorkflowBridge bridge, JArray path)
    {
        if (path is not { Count: 2 } || path[1] is not JValue slotVal || slotVal.Type != JTokenType.Integer)
        {
            return null;
        }
        ComfyNode node = bridge.Graph.GetNode($"{path[0]}");
        if (node is null)
        {
            return null;
        }
        int slotIndex = System.Convert.ToInt32(slotVal.Value!);
        INodeOutput output = node.FindOutput(slotIndex);
        if (output is null && node is UnknownNode unknown)
        {
            output = unknown.GetOutput(slotIndex);
        }
        return output;
    }
}
