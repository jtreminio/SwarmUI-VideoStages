# ComfyTyped upstream migration — status

A record of footguns and ergonomics gaps that were fixed upstream in
`ComfyTyped` (`src/Extensions/ComfyTyped/`) and how this extension was
migrated off the local workarounds.

---

## 1. Literal-value type coercion on `INodeInput` — DONE

**Upstream:** `ComfyTyped.Core.NodeInputExtensions` exposes `LiteralAsInt`,
`LiteralAsLong`, `LiteralAsString`, `LiteralAsDouble`, `LiteralAsBool` as
public extension methods on `INodeInput`. They tolerate boxed-int vs boxed-long
mismatches and Newtonsoft's integer-to-`long` normalization.

**VideoStages migration — DONE.**
`src/Typed/NodeSlotExtensions.cs` deleted. All call sites already wrote
`input.LiteralAsInt()` / etc., so dropping the local helpers and letting
`ComfyTyped.Core` win was a no-op rebuild.

---

## 2. Dynamic list-style ComfyUI inputs — DONE (escape hatch)

**Upstream:** `ComfyNode.ExtraInputs` (`JObject?`) is a public, settable
property on every node. `ComfyGraph.FromWorkflow` automatically captures any
input key on a typed node that doesn't match a declared `NodeInput<T>` into
`ExtraInputs`. `ComfyNode.ToWorkflowNode()` re-emits typed inputs first, then
merges in `ExtraInputs` keys not already present (typed inputs win on
collision). Round-trip is now lossless for nodes like `BatchImagesNode` and
`ResizeImageMaskNode` even when the codegen does not model their dynamic keys.

**Caveat:** connection refs (`[nodeId, slotIndex]`) stored under `ExtraInputs`
are passed through verbatim — they are NOT graph-aware, so
`RetargetConnections` and node removal do NOT update them. Acceptable for the
two VideoStages call sites because both build terminal nodes that nothing
later remaps.

**VideoStages migration — DONE.**
- `MultiClipParallelMerger.MergeClipVideosWithBatchImagesNode` uses typed
  `BatchImagesNodeNode` with `ExtraInputs` for the dynamic `images.imageN`
  keys.
- `ControlNetApplicator.ControlImageForLtxIcloraGuide` uses typed
  `ResizeImageMaskNodeNode` with `ExtraInputs` for the variant
  `resize_type.multiple` key. The `Input` connection (typed as
  `ComfyMatchTypeV3`) is wired via `ConnectToUntyped` from any source —
  enabled by the wildcard fix in the same upstream pass.

**Still open (typed list-collection codegen):** the codegen could detect
list-typed inputs in `object_info.json` and emit a real
`IList<NodeInput<ImageType>> Images { get; }` collection on
`BatchImagesNodeNode`, with serialization that writes the `images.imageN`
keys and full graph-awareness. Defer until there's a second consumer that
needs graph-aware list inputs — the escape hatch is sufficient for everything
VideoStages does today.

---

## 3. `WorkflowBridge.ResolvePath` synthesizes `UnknownNode` outputs — DONE

**Upstream:** `WorkflowBridge.ResolvePath` calls `UnknownNode.GetOutput(slotIndex)`
when the path resolves to an `UnknownNode` and the requested slot is not yet
registered. Synthesized slots are typed `AnyType` and connect to anything via
`ConnectToUntyped`.

**VideoStages migration — DONE.**
- Local `ResolveOrSynthesizePath` extension deleted (along with the rest of
  `src/Typed/NodeSlotExtensions.cs`).
- `RootVideoStageResizer.TryResolveOrSynthesizeOutput` deleted; its single
  caller now uses `bridge.ResolvePath(...)` directly.
- All `bridge.ResolveOrSynthesizePath(path)` call sites swept to
  `bridge.ResolvePath(path)` (same signature, same return type, same null
  semantics — synthesis happens transparently).

---

## Bonus — `WorkflowBridge.RemoveAllNodes` available upstream

`WorkflowBridge.RemoveAllNodes()` wipes every node from both the typed graph
and the JObject workflow while preserving non-node properties like `_meta`.
Returns the removed count. No current VideoStages call site needs it.

---

## Remaining VideoStages-side oddities (not upstream concerns)

- **`ControlNetApplicator.ReplaceVideoControlNetUpscale`** still mutates a
  node's `class_type` in place (`ImageScale` → `ResizeImageMaskNode`,
  preserving the same node id). The wildcard fix would make a typed
  remove-and-re-add approach feasible, but the in-place class swap is
  semantically clearer for the use case (replace one preprocessor with
  another at the same slot, no consumer retargeting needed). Keep as-is.
