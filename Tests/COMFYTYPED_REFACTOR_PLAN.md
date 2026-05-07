# VideoStages Tests → ComfyTyped Refactor Plan

Reference test (the canonical "after" shape):
`Tests/AceStepFunAudioSavePrunerTests.cs` — every other test file should look
similar in spirit (typed node construction, typed connections, typed reads).

---

## 1. Goal

Move VideoStages test code off of stringly-typed `JObject` / `JArray` workflow
construction and onto ComfyTyped's typed node graph. After this work, tests
should:

- **Build workflows** via `WorkflowBridge` + typed `*Node` classes from
  `ComfyTyped.Generated` and `VideoStages.Generated`.
- **Make connections** via `input.ConnectTo(output)` instead of
  `["x"] = new JArray("id", slotIndex)`.
- **Set literals** via `input.Set(value)` instead of `["x"] = value`.
- **Assert on graph state** via `bridge.Graph.GetNode<T>(id)`,
  `NodesOfType<T>()`, typed `.Connection`, `.LiteralAsX()`, etc.

The `JObject workflow` is still the source of truth held by SwarmUI's
`WorkflowGenerator`; we keep it, but build/inspect it through the bridge.

---

## 2. The pattern (cheat sheet)

### 2a. Building a workflow from scratch

**Before:**
```csharp
JObject workflow = new()
{
    ["64160"] = new JObject
    {
        ["class_type"] = "VAEDecodeAudio",
        ["inputs"] = new JObject()
    },
    ["64170"] = new JObject
    {
        ["class_type"] = "SaveAudioMP3",
        ["inputs"] = new JObject
        {
            ["audio"] = new JArray("64160", 0),
            ["filename_prefix"] = "SwarmUI_track_1_"
        }
    }
};
```

**After:**
```csharp
JObject workflow = [];
using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), "64160");

SaveAudioMP3Node save = new();
save.Audio.ConnectTo(decode.AUDIO);
save.FilenamePrefix.Set("SwarmUI_track_1_");
bridge.AddNode(save, "64170");
```

`WorkflowBridge` is `IDisposable` (subscriptions root the bridge to its
tracked nodes) — wrap construction in `using` to drop subscriptions when the
test scope ends.

Mutations to typed inputs auto-propagate to the JObject: `Set`, `ConnectTo`,
`ConnectToUntyped`, and `Clear` all rewrite `Workflow[id]["inputs"][name]`
in place. **Do not call `bridge.SyncNode(...)` after a typed mutation** — it
is no longer required, and the explicit sync rebuilds the outer node JObject,
detaching any held references.

So any code under test that walks the `JObject` (e.g.
`AceStepFunAudioSavePruner.Apply`) still works unchanged.

### 2b. Reading a workflow

**Before:** `JArray ref = workflow["7"]?["inputs"]?["images"] as JArray;`
**After:** `var save = bridge.Graph.GetNode<SwarmSaveAnimationWSNode>("7"); var conn = save.Images.Connection;`

### 2b.1. `UnknownNode` connections are always untyped

`UnknownNode.GetInput(name)` returns `NodeInput<AnyType>`. `ConnectTo(...)`
is invariant on the marker type, so wiring an `AnyType` input to a typed
output (or vice-versa) does not compile. **Whenever an `UnknownNode` is on
either side of a wire, use `ConnectToUntyped`**; reserve `ConnectTo` for
both-sides-typed cases.

### 2c. Resolving a path / output for `MediaRef` etc.

Still legitimate to use `bridge.ResolvePath(new JArray("5", 0))` when the test
is exercising the path↔typed-output boundary itself (see `TypedBoundaryTests`).

### 2d. Assertions

Use the helpers in `Tests/TypedWorkflowAssertions.cs`:

- `RequireTypedNode<T>(bridge, id)`
- `SamplerNodesOrdered(bridge)`
- `LoraLoaderNodesOf(bridge)`
- `AsWorkflowNode(node, workflow)` for cases that still want a `WorkflowNode`
  to feed legacy assertions

Add new shared helpers there if the same query repeats across two or more
files — don't inline-duplicate.

---

## 3. What stays as `JObject` / `JArray`

Subagents must **not** try to refactor these. They are not in scope.

1. **Stub class types like `UnitTest_*`.** They have no generated `*Node`, by
   design — they're sentinels in test fixtures. Leave them as `Node(classType,
   inputs)` calls or `g.CreateNode("UnitTest_*", ...)` calls. (You *can* load
   them through a bridge after the fact: `WorkflowBridge.Create` routes them
   to `UnknownNode` automatically.)

2. **Generator-state mutations inside `WorkflowGenStep` lambdas.**
   Assignments to `g.CurrentModel`, `g.CurrentVae`, `g.CurrentAudioVae`,
   `g.CurrentTextEnc`, `g.CurrentMedia`, `g.FinalLoadedModel`,
   `g.FinalLoadedModelList`, `g.NodeHelpers[...]`, `g.FinalPrompt`,
   `g.FinalNegativePrompt`, etc. — *and* `WGNodeData` construction that
   feeds them — are SwarmUI API surface and stay. `BridgeSync.SyncLastId(g)`
   stays.

   **`g.CreateNode("X", new JObject{...}, id: "Y", idMandatory: false)`
   calls inside seed steps are NOT in this category** — they are just
   JObject writes into `g.Workflow` and convert cleanly to
   `bridge.AddNode(new XNode { ... }, "Y")` (real types) or
   `bridge.AddNode(new UnknownNode("X"), "Y")` (stub types). Unit 7 of the
   refactor opens this up. The bridge created inside a seed step should be
   `using`-wrapped (auto-sync flushes to `g.Workflow` before the lambda
   exits, then the bridge disposes; the JObject persists for downstream
   steps).

3. **`WGNodeData` construction in seed steps.**
   `new WGNodeData(new JArray("104", 0), g, …)` is the SwarmUI public API.
   Don't replace it with `MediaRef` unless the test is specifically about the
   `MediaRef`/`WGNodeData` boundary (those tests already exist in
   `TypedBoundaryTests.cs`).

4. **VideoStages JSON config builders** (`MakeStage`, `MakeClip`,
   `MakeRef`, `MakeRootConfig`, `MakeUploadedAudio`). Those produce the JSON
   the *user* would author — they are not workflow nodes. Leave them.

5. **`JsonParserClipsTests.cs`** is entirely about parsing the user JSON —
   no workflow nodes. Skip the file.

6. **`AudioSourceParamTests.cs`, `ImageReferenceSyntaxTests.cs`,
   `VideoStagesMetadataSanitizerTests.cs`, `VideoStageModelCompatTests.cs`,
   `VideoClipPromptSectionsTests.cs`, `ClipAudioWorkflowHelperTests.cs`,
   `TestGlobalStateFixture.cs`, `ImageReferenceTests.cs`** — already low or
   zero JObject usage; verify with `grep -c "JObject\|JArray" <file>`. Skip
   unless the count justifies it.

7. **Production-code `bridge.SyncNode(...)` / `BridgeSync.SyncLastId(...)`
   calls** in `src/`. The SUT may still rely on explicit sync for
   `ExtraInputs` or `UnknownNode.RawInputs` paths that auto-sync does not
   cover. Tests must not delete those calls.

When in doubt, leave it alone and flag the call site in the PR description.

---

## 3.5. Gotchas (post auto-sync)

ComfyTyped commits `400b487` and `68a07fa` introduced auto-sync and
documented the removal contract. These behaviors are easy to trip over:

1. **Wrap bridges in `using`.** `WorkflowBridge` subscribes to every node it
   tracks; while subscriptions exist, a held node reference keeps the bridge
   (and the entire workflow) reachable. Use `using WorkflowBridge bridge =
   WorkflowBridge.Create(workflow);` in test methods. Helpers that build &
   return a workflow but don't hand back the bridge should `Dispose` before
   returning.

2. **`Clear()` then `Set()` reorders the property.** A clear-then-set on the
   same input appends the property at the end of `inputs` rather than
   restoring its original position. Semantically equivalent to ComfyUI but
   visible to anything diffing serialized JSON or asserting key order. If a
   test compares serialized workflows, prefer `Set(newValue)` directly over
   `Clear(); Set(newValue);`.

3. **`UnknownNode` first-mutation reset.** The first typed mutation on a
   node that came in as `UnknownNode` clears `RawInputs` and rebuilds the
   inner `inputs` JObject from typed slots. Anyone holding the inner
   `inputs` reference will see it replaced.

3a. **Re-hydrating an `UnknownNode` from a fresh bridge: outputs
    materialize on demand.** *(Fixed upstream in ComfyTyped commit
    `fc07bef`, May 2026.)* `UnknownNode` overrides `FindOutput(int)` to
    add the slot on miss, so `bridge.Graph.GetNode(id).FindOutput(slot)`
    resolves cleanly even for stub outputs that no in-JObject input
    references. `WorkflowBridge.ResolvePath` was simplified to rely on
    this behavior. No special-casing or `(UnknownNode)` cast required at
    call sites. If you're working against an older ComfyTyped DLL, the
    workaround was `((UnknownNode)bridge.Graph.GetNode(id)!).GetOutput(slot)`.

4. **`SyncNode` / `SyncAll` replace the outer node JObject.** Auto-sync
   updates `inputs` in place, but `SyncNode` and `SyncAll` rebuild
   `Workflow[id]` from scratch — held node-JObject refs detach. Avoid
   calling them in new test code; they are only needed for code that mutates
   `ExtraInputs` / `RawInputs` directly.

5. **Dangling refs to nodes you don't have typed bindings for.** When a
   workflow input must reference an ID that the test doesn't fully model
   (e.g. an upstream model loader the test doesn't care about), use stub
   `UnknownNode`s, not `ExtraInputs`:
   ```csharp
   UnknownNode stub = bridge.AddNode(new UnknownNode("StubVae"), "104");
   stub.GetOutput(0);
   decode.Vae.ConnectToUntyped(stub.GetOutput(0));
   ```
   Wiring direction matters: `ConnectTo(NodeOutput<T>)` is the typed-only
   path. **Whenever either side is an `UnknownNode` (stub) — input *or*
   output — use `ConnectToUntyped(...)`.** That covers both
   typed-input ← stub-output (above) and stub-input ← typed-output (e.g.
   `stub.GetInput("samples").ConnectToUntyped(typedNode.LATENT)`).
   `ExtraInputs` only carries through input keys the typed node does **not**
   declare — typed inputs win on collision (`ComfyNode.ToWorkflowNode`
   merges extras *after* typed slots and skips keys that already exist). So
   `ExtraInputs["latent_image"] = ...` on a `KSamplerNode` is silently
   dropped because `latent_image` is a declared typed input. Use
   `ExtraInputs` only for genuinely undeclared keys (e.g. dotted keys like
   `images.image0` on `BatchImagesNode`). For everything else, stub-and-wire.

6. **`bridge.RemoveNode(old)` is a dumb delete.** It does not clean up
   downstream inputs that point at the deleted node's outputs. The
   documented idiom (per the README §"Removing a node that has consumers")
   is rewire-then-remove:
   ```csharp
   foreach (INodeOutput output in old.Outputs)
   {
       INodeOutput? to = replacement.FindOutput(output.SlotIndex);
       if (to is not null) bridge.Graph.RetargetConnections(output, to);
   }
   bridge.RemoveNode(old);
   ```
   Or, to drop without a replacement, walk `FindInputsConnectedTo(output)`
   and `Clear()` each consumer first. Most test code doesn't remove
   mid-graph nodes, but `AceStepFunAudioSavePruner` (the SUT in the
   reference test) does — any new test exercising similar pruning logic
   must follow this contract.

---

## 4. Files in scope, ordered by impact

Run `grep -c "JObject\|JArray" Tests/*.cs` for current counts. The targets:

| File | Approx JObject/JArray refs | Notes |
|------|---|---|
| `StageFlowCoreVideoTests.cs` | 188 | Largest. Mostly seed `WorkflowGenStep` lambdas (rule #2 above — leave) plus a few hand-built workflows used as direct fixtures. Focus on the latter. |
| `StageFlowTests.cs` | 151 | Same shape: seed steps + assertion helpers. The `Make*` helpers at the top are user-JSON builders (rule #4 — leave). The `Assert*` helpers near the top already use `WorkflowBridge` — extend that pattern through the file body. |
| `AudioInjectionTests.cs` | 86 | Has hand-built workflow JObjects in test bodies (e.g. `Injector_sets_empty_video_length_…`). These are the prime targets. The `SeedRoot…Step` lambdas stay. |
| `JsonParserClipsTests.cs` | 51 | Skip — see rule #5. |
| `AudioStageDetectorTests.cs` | 24 | Hand-built workflows via `Node(classType, inputs)`. Strong refactor target — rewrite as typed builders. The `JToken.DeepEquals(detection.Audio.Path, new JArray(...))` assertions can stay (testing a `WGNodeData.Path`). |
| `WorkflowGraphTests.cs` | 22 | Has one `BuildWrapperWorkflow()` helper that hand-builds nodes. Convert to typed construction; the rest already uses `WorkflowBridge` correctly. |
| `NativeLtxStageFlowChainedTests.cs` | 17 | Mostly `MakeStage(...)` / `MakeClip(...)` (rule #4 — leave) plus a few `JArray` assertion checks. Likely small touch. |
| `NativeLtxStageFlowSingleStageTests.cs` | 16 | Same shape as above. |
| `TypedBoundaryTests.cs` | 170 | **Specifically tests the JObject↔typed boundary** — many `new JArray(...)` calls are intentional (they're the input *to* `bridge.ResolvePath`). Leave alone for content, but **audit for missing `using`**: these tests pre-date `IDisposable WorkflowBridge` and likely leak subscriptions across `[Fact]` runs. Wrap each `WorkflowBridge.Create(...)` site in a `using` block. |

**Heuristic for any file:** if a JObject/JArray sits inside a
`WorkflowGenStep` lambda or a `MakeStage`-style user-config helper, leave it.
If it sits in a test method building a fixture workflow that gets passed to
`WorkflowBridge.Create` or to code under test, rewrite it.

---

## 5. Per-subagent work units

Each unit is independent and should be one PR. Subagent prompt template at
the bottom.

1. **WorkflowGraphTests.cs** — small, self-contained, good warmup.
   Convert `BuildWrapperWorkflow()` to typed construction.
2. **AudioStageDetectorTests.cs** — replace the `Node(classType, inputs)`
   helper with typed builders. Keep `JToken.DeepEquals(detection.Audio.Path,
   new JArray(...))` assertions (testing serialized path output).
3. **AudioInjectionTests.cs** body — convert hand-built workflow fixtures
   (e.g. lines ~250-285) to typed construction. Do **not** touch the
   `Seed…Step` lambdas.
4. **StageFlowTests.cs** assertion helpers — push more
   `WorkflowBridge`-using helpers into `TypedWorkflowAssertions.cs` where any
   pattern repeats. Don't rewrite seed steps.
5. **StageFlowCoreVideoTests.cs** — same approach as #4, but larger. Likely
   split across two units if the diff balloons.
6. **NativeLtxStageFlow{Chained,SingleStage,GuideReuse}Tests.cs** — small
   cleanup only. Verify nothing's left over after the bigger files are done.

Skip: `JsonParserClipsTests.cs`, `AudioSourceParamTests.cs`,
`ImageReferenceSyntaxTests.cs`, `VideoStagesMetadataSanitizerTests.cs`,
`VideoStageModelCompatTests.cs`, `VideoClipPromptSectionsTests.cs`,
`ClipAudioWorkflowHelperTests.cs`, `TestGlobalStateFixture.cs`,
`ImageReferenceTests.cs`.

---

## 6. Validation

After each unit:

```bash
cd /Users/jtreminio/apps/swarmui
dotnet build src/Extensions/SwarmUI-VideoStages/SwarmUI-VideoStages.csproj
dotnet test  src/Extensions/SwarmUI-VideoStages/Tests/SwarmUI-VideoStages.Tests.csproj \
    --filter "FullyQualifiedName~<file-namespace>"
```

The whole suite must pass. No skips, no `[Trait("flaky", ...)]`, no `[Fact(Skip=...)]`.

---

## 7. Subagent prompt template

> **Task:** Refactor `<file>` to use ComfyTyped instead of raw JObject/JArray
> for workflow construction and assertions.
>
> **Reference:** `Tests/AceStepFunAudioSavePrunerTests.cs` is the canonical
> "after" shape. The full plan, including what to leave alone, is in
> `Tests/COMFYTYPED_REFACTOR_PLAN.md` — read sections 2 (pattern), 3
> (don'ts), and the row for your file in section 4 before touching code.
>
> **Scope (hard limits):**
> - Only `<file>` (and shared helpers in
>   `Tests/TypedWorkflowAssertions.cs` if a pattern needs to land there).
> - Do NOT touch `WorkflowGenStep` lambdas, `g.CreateNode` calls, `WGNodeData`
>   construction, `MakeStage`/`MakeClip` JSON-config helpers, or any
>   `UnitTest_*` stub nodes.
> - Do NOT change test names or assertion semantics. Each `[Fact]` should
>   still test exactly what it tested before.
> - Do NOT call `bridge.SyncNode(...)` or `bridge.SyncAll()` in new code —
>   auto-sync covers typed mutations (see §3.5).
> - DO wrap every `WorkflowBridge.Create(...)` site in `using` (§3.5).
>
> **Definition of done:**
> 1. `grep -c "JObject\|JArray" Tests/<file>` is materially lower (target:
>    only counts left should map to allowed cases from section 3).
> 2. `dotnet build` clean.
> 3. `dotnet test --filter` for that file: all green.
> 4. Diff is tight — no incidental rewrites of unrelated assertions, no
>    renames, no comment churn.
>
> Report back: file, before/after grep count, test result, and any call
> sites you flagged as ambiguous.

---

## 8. Open questions to confirm before kickoff

- **Should `ClipAudioWorkflowHelperTests.cs` get a typed-fixture pass even
  though it's already 0 JObject?** (currently planned: skip)
- **Are there any `g.CreateNode("ClassType", ...)` calls where `ClassType` is
  a *real* node** (not a `UnitTest_*` stub) that we'd want to convert to
  `bridge.AddNode(new ClassTypeNode(), id)` even though it lives inside a
  seed step? Currently rule #2 says no, but that's the biggest source of raw
  JObjects in the file count. Worth a one-file experiment to see if the
  WorkflowGenerator-driven generation flow still works post-conversion.
