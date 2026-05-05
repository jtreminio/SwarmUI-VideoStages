# ComfyTyped upstream migration TODOs

Things this extension worked around locally that should ideally be solved in
the `ComfyTyped` library itself (`src/Extensions/ComfyTyped/`). Move them
upstream and delete the local workarounds when they're addressed.

---

## 1. Literal-value type coercion on `INodeInput`

**Where the workaround lives:** `src/Typed/NodeSlotExtensions.cs`
(`LiteralAsInt`, `LiteralAsLong`, `LiteralAsString`, `LiteralAsDouble`,
`LiteralAsBool`).

**The problem:** `INodeInput.LiteralValue` returns `object?`, and the boxed
type depends on how the input was set:

- `input.Set(42)` in C# → boxed `int`.
- `input.Set(42L)` in C# → boxed `long`.
- Loaded from a `JObject` via `ComfyGraph.FromWorkflow` → boxed `long`
  (Newtonsoft normalizes integer JSON to `long`).

A naive `(long?)input.LiteralValue` cast throws `InvalidCastException` when
the underlying type is a boxed `int`, even though the conversion would be
lossless. Consumers that mix `Set(int)` with bridge round-trips will hit
this.

**Suggested upstream fix:** add extension methods on `INodeInput` (probably in
`ComfyTyped.Core`) that read literal values with cross-numeric-type tolerance.
The five methods in `NodeSlotExtensions.cs` are a starting point; they are
self-contained and can be lifted as-is.

---

## 2. Dynamic list-style ComfyUI inputs

**Where the workaround lives:**
- `src/MultiClipParallelMerger.MergeClipVideosWithBatchImagesNode` — falls back
  to `g.CreateNode("BatchImagesNode", jObject)` with hand-built
  `images.image0`, `images.image1`, … keys.
- `src/ControlNetApplicator.ControlImageForLtxIcloraGuide` and
  `ControlNetApplicator.ReplaceVideoControlNetUpscale` — keep
  `ResizeImageMaskNode` as JObject because the node has variant inputs like
  `resize_type.multiple` and `resize_type.shorter_size` that depend on the
  selected `resize_type`. The codegen exposes `ResizeType` and `ScaleMethod`
  as typed inputs but not the dotted-key variants.

**The problem:** Some ComfyUI nodes declare list-typed or variant-shaped
inputs that are wired in the workflow JSON as multiple sibling keys. Examples:

- `BatchImagesNode`: `images.image0`, `images.image1`, …, `images.imageN`.
- `ResizeImageMaskNode`: `resize_type.multiple`, `resize_type.shorter_size`,
  etc., depending on which `resize_type` mode is active.

The ComfyTyped codegen currently flattens these to a single typed input,
and `node.ToWorkflowNode()` only serializes the named typed inputs — losing
every dynamic/dotted key. Round-tripping such a node through the bridge
would silently drop those inputs.

**Suggested upstream fix:** Either

- Detect list-typed inputs in `object_info.json` and emit a typed
  collection input on the generated node (e.g.
  `IList<NodeInput<ImageType>> Images { get; }` with serialization that
  writes the `images.imageN` keys), **or**
- At minimum, add a documented escape hatch on `ComfyNode` for
  preserving/round-tripping unknown extra input keys, similar to how
  `UnknownNode.RawInputs` works for unknown node classes.

Until then, consumers must build these nodes via untyped `g.CreateNode`
and skip the bridge for those specific class types.

---

## 3. `WorkflowBridge.ResolvePath` doesn't synthesize `UnknownNode` outputs

**Where the workaround lives:** `src/Typed/NodeSlotExtensions.ResolveOrSynthesizePath`,
plus a hand-rolled equivalent already in `RootVideoStageResizer.TryResolveOrSynthesizeOutput`.

**The problem:** `ComfyGraph.FromWorkflow` populates an `UnknownNode`'s outputs only
for slots that *appear* somewhere as another node's input source. A node that nobody
references yet — common for freshly seeded test stubs and for any new untyped node
that's about to get its first consumer — has zero outputs registered. As a result,
`bridge.ResolvePath([unknownNodeId, 0])` returns `null` even though the slot logically
exists, and any `ConnectToUntyped(...)` consuming that path silently no-ops (the
input becomes a literal-less, connection-less hole, which round-trips as a missing
key in the JObject).

**Suggested upstream fix:** in `WorkflowBridge.ResolvePath`, when the looked-up node
is an `UnknownNode` and `FindOutput(slotIndex)` returns null, call
`UnknownNode.GetOutput(slotIndex)` to materialize the slot on demand and return that.
The slot is typed as `AnyType` and connects to anything, so this is a strict
improvement over returning null. The existing `TryResolveOrSynthesizeOutput` /
`ResolveOrSynthesizePath` helpers can be deleted in consumers once this lands.
