# VideoStages stage runtime

This document is the operational map for backend generation. It explains where
one request becomes an immutable plan, when graph mutation is allowed, which
objects live for a request versus a timeline, and which stage mechanics are
common or architecture-owned.

For model/catalog/frontend flow, start with
[`ARCHITECTURE_FLOW.md`](ARCHITECTURE_FLOW.md). For ownership rules and module
contracts, see [`ARCHITECTURE.md`](../ARCHITECTURE.md).

## The short path

```text
hidden authoring document + host request
    → RequestReader
    → ArchitecturePlanResolver
    → resolved temporal-grid projection
    → VideoExecutionPlanCompiler
    → cached VideoExecutionPlanContext
    → graph-free request preparation
    → ordered host phases
    → VideoArchitectureExecutionHost
    → one session per active architecture, selected for each clip
    → DecodedClipArtifact per clip
    → Timeline.Boundaries
    → RootRuntimeSession.PublishTimeline
```

There is one common orchestration path. LTX, MiniMax, WAN, generic host video,
and the source-only `none` path do not receive separate top-level runners.

## 1. Parse, resolve, project, compile

`RequestCaches.GetVideoExecutionPlanContext` is the entry to graph-free
planning. Two `ConditionalWeakTable` caches are keyed by the current
`WorkflowGenerator`: one for `TimelineSpec`, one for the compiled plan.
Repeated SwarmUI workflow callbacks therefore observe the same immutable plan.

The stages before runtime are:

1. `DocumentJson` version-checks the authoring document and applies the one
   bounded migration, both pinned in [`ARCHITECTURE.md`](../ARCHITECTURE.md).
2. `RequestReader` reads common document data and prompt-section
   overrides.
3. `ArchitecturePlanResolver` resolves every authored stage model—including
   skipped stages—through the session-authorized backend registry. Resolved
   stage models own architecture, profile, and feature support. Persisted architecture/profile values are diagnostic hints.
4. `EffectiveVideoRequestProjection`, called inside
   `VideoExecutionPlanCompiler`, keeps authored data intact while projecting
   resolved temporal grids and reporting stale persisted model hints. Clip and
   stage IDs, raw stage indexes, model names, source identity, and topology stay
   unchanged, so the resolved assignments remain authoritative.
5. `VideoExecutionPlanCompiler` compiles common root, geometry, timing,
   boundary, and audio plans and asks the selected module to compile typed
   clip/stage payloads. Every stage payload exposes the common execution core;
   its graph-specific additions remain architecture-owned.
6. nonblocking diagnostics are reported once. Blocking diagnostics remain on
   the plan and stop preparation.

Planning is graph-free. A module compiler may inspect the resolved request and
host metadata captured as planning facts; it must not add/remove Comfy nodes or
depend on a graph mutation performed by an earlier phase.

## 2. Prepared request state

`VideoExecutionPlanContext` is the request state machine:

```text
Compiled → Preparing → Prepared → Completed
                └────────→ Failed
Prepared ─────────────────→ Failed
```

- `Compiled`: the immutable plan exists; runtime providers are not bound.
- `Preparing`: the single preflight owner is resolving providers and
  dependencies.
- `Prepared`: all blocking plan and preflight diagnostics passed.
- `Completed`: the final configured-stage run and publication returned.
- `Failed`: the first failure is captured with `ExceptionDispatchInfo`; later
  callbacks rethrow the same failure.

The registered preflight phase is the only caller of `PrepareRequest`. Later
phases enter their own lifecycle methods on the context, while alternate host
callbacks call `ExecutePrepared`. Neither path prepares lazily after core has
begun graph construction.

Preparation constructs one `VideoArchitectureExecutionHost`, resolves only
providers used by active clip architectures, and runs:

- common frame-interpolation preflight; then
- `IArchitectureGenerationSessionProvider.PreflightRequest` once for
  each active architecture.

The provider object is request-scoped and reused sequentially by preflight,
host phases, and session creation. It may retain lifecycle captures; mutable
timeline and clip state belongs to the session it creates.

## 3. Ordered SwarmUI workflow phases

`VideoStagesExtension.OnInit` registers these VideoStages steps:

Each is a `VideoExecutionPlanContext` method wrapped by `WorkflowPhase.Guarded`,
which keeps the step inert on a request VideoStages is not driving. Numeric
priorities live in `Constants.WorkflowStepPriority`.

| Priority | Context method | Scope/purpose |
| ---: | --- | --- |
| -6 | `PrepareRequest` | reads the compiled plan and backend features; must stay non-mutating |
| -5.9 | `CaptureControlNetPreprocessors` | reads core's ControlNet graph; captures raw image/audio/apply facts, then fans out to every active architecture provider |
| -4.2 | `CaptureBaseReference` | all active providers may snapshot base reference facts |
| 5.89 | `CaptureRefinerReference` | all active providers may snapshot refiner facts |
| 10.95 | `CapturePreCoreMedia` | snapshots eligible generated-root media/VAE in memory when a generated stage owns an interceptable root |
| 11.05 | `DropCoreOutput` | restores that root from the snapshot and prunes core's video pass |
| 11.4 | `ApplyRootAudioMaskDimensions` | root-owner architecture only; resizes audio SolidMask nodes to root dims |
| 11.5 | `RunConfiguredStages` | reads architecture references and the phase-2 captures; executes the planned sessions and publishes |

SwarmUI core image-to-video is expected around priority 11. The extension
discovers that step at startup and disables unsafe handoff when it is missing
or ambiguous.

`ArchitectureRootOwnerResolver` selects at most one architecture: the first
clip whose planned input consumes host root media or an empty latent. A init-video
clip owns its own media and does not claim the host root.

`VideoExecutionPlanContext` exposes one guarded method for each fixed lifecycle
step. `VideoArchitectureExecutionHost` implements the graph work: ControlNet and
reference capture call every active provider in plan order. Root capture and
restoration are common host work; audio-mask sizing calls only the root owner.

## 4. Provider and session lifetimes

The runtime has two lifetimes:

| Object | Lifetime | May own mutable generation state? |
| --- | --- | --- |
| `IArchitectureGenerationSessionProvider` | request | Host-phase captures only; reused across preflight, host phases, and session creation |
| `IVideoGenerationSession` | one active architecture in one timeline | Yes; executes all clips owned by that architecture |

`VideoArchitectureExecutionHost` creates sessions only after request preparation. Its
lifecycle is:

1. call `CreateSession` once per active provider, with `OwnsGeneratedRoot`;
2. execute clips through the execution host's architecture-keyed session map;
3. dispose every session, including constructor rollback on partial failure.

## 5. Common clip loop

`VideoArchitectureExecutionHost` owns the timeline-level sequence:

1. capture the host root publication/save contract;
2. resolve request audio sources;
3. reserve the staged node-ID range;
4. create active architecture sessions;
5. execute and assemble the clip sequence;
6. apply configured timeline interpolation;
7. clear model compatibility from neutral final media;
8. publish the final artifact and restore the root save contract.

The execution host loops planned clips in order. It exposes
`PreviousClipOutput` only across a same-architecture non-cut boundary.
`PreviousTimelineClipOutput` remains available as contextual decoded media
across cuts. The execution host selects a session solely by the planned
`ArchitectureId`, verifies that the returned architecture matches both the
selected session and planned clip, then verifies clip identity and decoded
shape.

Every architecture returns `DecodedClipArtifact`: decoded video, optional
decoded audio, literal dimensions/FPS/frame count, architecture ID, and clip
ID. Latents, VAEs, models, compatibility IDs, and opaque payloads cannot cross
this common boundary.

## 6. Stage loops

### Host stage loop

`VideoStageRunner.Execute` is the outer loop used by MiniMax, WAN, and generic
host video. It owns:

- stage iteration;
- adjacent sampling-continuation selection;
- decoded upscale and passthrough handling;
- stage-input publication;
- stage-output capture and validation;
- intermediate publication;
- terminal trim; and
- advancing past a consumed continuation.

Each architecture keeps one cohesive stage procedure for its model-specific
references, audio, conditioning, latent construction, sampling, and decode.
Generating procedures use `StageModelLoadScope` for the shared prompt/planned
LoRA order and host model-loader cache lifetime.

### WAN and generic host video

`StockHostVideoGenerationSession` owns the common stock-host execution path and
uses `VideoStageRunner` for stage iteration, decoded pixel/model upscale,
passthrough input, host parameter sections, and terminal trim. Generic host video
uses that session directly. WAN supplies one optional concrete
`WanStockHostVideoBehavior` collaborator for first/final-frame materialization,
temporal snapping, native final-frame conditioning, and 5B cleanup. This is not
a generic callback or policy interface: it is the bounded WAN addition to the
otherwise shared stock path.

The WAN and generic paths delegate supported graph construction to SwarmUI's
stock video primitives. VideoStages validates its own document topology and
added features; it does not try to reproduce every core model-path validity
rule.

### MiniMax

`MiniMaxGenerationSession` uses `VideoStageRunner.Execute`, but does not use
`StockHostVideoGenerationSession`. The shared wrapper owns decoded upscale and
passthrough handling, host parameter sections, intermediate publication, output
validation, and terminal trim. `MiniMaxGenerationSession` keeps H3 model/prompt
setup, audio selection, timeline-segment combination and encoding, joint
audio-video latent construction, first/last-frame keyframes, sampling, and
joint decode together. Without a selected base track, only authored segment
windows are preserved; H3 remains free to generate native audio in the gaps.
When external audio owns duration, the selected track drives the initial joint
latent's `17k+5` frame count; refinement reuses that latent without re-deriving
its length. That mode currently requires a single clip, refuses global frame
trim, and skips request-global frame interpolation.

### LTX

LTX owns its outer lifecycle and its latent, conditioning, audio, IC-LoRA, guide,
retake, and post-video-chain semantics:

```text
LTX private generation session
    → Ltx2GenerationSession
    → StageRunner
    → LtxStageExecutor
    → LtxStageOutputFinalizer
    → DecodedClipArtifact
```

Common orchestration reads only the required architecture-neutral stage core
and otherwise carries `Ltx2ClipPayload`/`Ltx2StagePayload` without interpreting
their graph instructions. LTX code interprets those additions under
`src/Architectures/Ltx2`.

### Source-only

The `none` architecture uses `SourceOnlyGenerationSession` and
`InitVideoClipInstaller`. It creates no model, latent, sampler, or VAE generation
path, but returns the same decoded artifact contract.

## 7. Merge and publication

`Timeline.Boundaries` routes descriptor-supported crossfades through
`Timeline.DecodedVideoJoiner`, joins architecture runs with neutral hard cuts,
and assembles decoded audio.

`RootRuntimeSession` restores the captured host save set and publishes the final
artifact itself. No architecture session may publish the
whole timeline directly.

## 8. Failure boundary

The first exception in a prepared host phase, alternate callback, stage run,
assembly, or publication transitions the request to `Failed`.
Scopes and sessions still dispose through normal `using`/rollback paths. Later
callbacks cannot reuse partially mutated request state.

Diagnostics divide responsibility:

- malformed document identity/timing and invalid VideoStages topology block;
- absent optional architecture features warn and remain dormant in authored
  data;
- architecture compilers validate their opaque additions;
- graph-free dependency checks run during preparation; and
- ordinary supported core path validity remains with SwarmUI.

| Validation concern | Owner and phase |
|---|---|
| JSON version, types, IDs, common structural shape | `DocumentJson` / `RequestReader` |
| Model resolution, clip lock, entry mode, common timing/boundary/audio topology | `ArchitecturePlanResolver` / `VideoExecutionPlanCompiler` |
| Unsupported optional authored features | Common capability validation; warn and omit from the compiled plan |
| Architecture-specific options and semantic conflicts | Selected module's graph-free compiler |
| Architecture dependencies | Active runtime provider during request preparation |
| Ordinary model-path validity already owned by a supported SwarmUI primitive | SwarmUI core during graph construction |
| Returned identity and decoded media shape | `VideoArchitectureExecutionHost` |
| Cross-clip run validity and final publication contract | `Timeline.Boundaries` / `RootRuntimeSession` |

## 9. Generated binding retention audit

The ComfyTyped surface has two different meanings of “retention”:

1. code-generation pruning; and
2. .NET linker trimming.

For code-generation pruning, `comfytyped.keep.json` retains the
`custom_nodes.ComfyUI-LTXVideo` module and the `SwarmFrameImage` class type.
Generation expands those facts into `src/Generated/PruneManifest.g.cs`.
Every node wrapper in the generated directory is retained one of two ways: it
is in `PruneManifest.AlwaysKeep`, or it is one of the five extension-owned
Swarm nodes directly referenced by production C# and therefore discovered by
the prune source scan:

- `SwarmAudioLengthToFramesNode`;
- `SwarmFrameWindowNode`;
- `SwarmPromptRelayEncodeNode`;
- `SwarmRampMaskBatchNode`; and
- `SwarmSetAudioMaskWindowsNode`.

`GeneratedBindingRetentionTests` owns that split: it makes the two-way
classification exhaustive and verifies that every manifest entry names an
existing unique generated node. It is the count, so this doc does not restate
one.

At runtime, `VideoStagesExtension.OnInit` calls both
`ComfyTyped.Generated.NodeRegistrations.EnsureRegistered()` and
`VideoStages.Generated.NodeRegistrations.EnsureRegistered()` before
architecture dependency registration. The latter registers the executing
assembly with `NodeRegistry`.

The extension project does not enable `PublishTrimmed`, `TrimMode`,
`PublishAot`, or self-contained publication. No linker-retention annotation is
currently required. If deployment enables trimming later, the reflective
`RegisterAssembly` call becomes a new boundary and must receive linker
characterization before release; the code-generation keep manifest is not a
substitute for linker annotations.

After changing object-info inputs or `comfytyped.keep.json`, regenerate and run
the documented prune command in [`README.md`](../README.md), then run
`./run-tests`.

## 10. Change checklist

When adding an architecture using the existing vocabulary:

1. register its module and runtime provider in `VideoArchitectureManifest`;
2. publish typed capabilities, frame grid, and reference positions from
   resolved model facts;
3. implement graph-free projection/compilation and typed architecture payloads
   whose stage payloads expose the required common core;
4. implement provider → session lifetimes;
5. add a fail-closed decoded-overlap branch before declaring non-cut support;
6. return a valid neutral decoded artifact;
7. add frontend-local behavior only for concrete bespoke UI; and
8. test mixed clips, failure cleanup, and prepared-state enforcement.

Do not add a second top-level runner, recognize models in the frontend, or add
generic “before/after stage” hooks for behavior that belongs in one module.
