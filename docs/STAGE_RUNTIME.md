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
    → VideoStagesSpecParser
    → ArchitecturePlanResolver
    → resolved-fact effective-request projection
    → VideoExecutionPlanCompiler
    → cached VideoExecutionPlanContext
    → graph-free request preparation
    → ordered host phases
    → VideoStagesCoordinator
    → StageSequenceRunner
    → one session per active architecture, selected for each clip
    → DecodedClipArtifact per clip
    → TimelineAssemblySession
    → OutputPublisher
    → optional exclusive architecture finalization
```

There is one common orchestration path. LTX, WAN, generic host video, and the
source-only `none` path do not receive separate top-level runners.

## 1. Parse, resolve, project, compile

`VideoStagesContext.GetVideoExecutionPlanContext` is the entry to graph-free
planning. Two `ConditionalWeakTable` caches are keyed by the current
`WorkflowGenerator`: one for `VideoStagesSpec`, one for the compiled plan.
Repeated SwarmUI workflow callbacks therefore observe the same immutable plan.

The stages before runtime are:

1. `VideoStagesJsonReader` accepts authoring schema v6. It has one bounded v5
   migration that renames the old `architecture` key to `architectureHint`.
2. `VideoStagesSpecParser` parses common document data and prompt-section
   overrides.
3. `ArchitecturePlanResolver` resolves every authored stage model—including
   skipped stages—through the session-authorized backend registry. Resolved
   stage models own architecture, profile, and feature support. Persisted architecture/profile values are diagnostic hints.
4. `EffectiveVideoRequestProjector`, called inside
   `VideoExecutionPlanCompiler`, keeps authored data intact while producing the
   values that this generation can execute from those resolved facts. Common
   capability omission handles optional unsupported values; an architecture
   hook handles only its unique graph-free policy. Projection preserves clip
   and stage IDs, raw stage indexes, model names, source identity, and topology,
   so the original resolved assignments remain authoritative.
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

`Runner.PreflightRequest` is the only method allowed to call
`PrepareRequest`. Mutation callbacks call `RequirePrepared`; they never lazily
prepare. This matters for alternate host callbacks that can occur after core
has begun graph construction.

Preparation constructs one `VideoArchitectureExecutionHost`, resolves only
providers used by active clip architectures, and runs:

- common frame-interpolation preflight; then
- `IArchitectureGenerationSessionFactoryProvider.PreflightRequest` once for
  each active architecture.

The provider object is request-scoped and reused sequentially by preflight,
host phases, root-resizer lookup, and factory creation. It must remain
stateless or hold only request-safe collaborators.

## 3. Ordered SwarmUI workflow phases

`VideoStagesExtension.OnInit` registers these VideoStages steps:

| Priority | Entry point | Scope/purpose |
| ---: | --- | --- |
| -6 | `Runner.PreflightRequest` | compile/prepare once; no VideoStages graph mutation |
| -5.9 | `CaptureCoreVideoControlNetPreprocessors` | common capture once, then all active architecture participants |
| -4.2 | `CaptureBase` | all active participants may snapshot base reference facts |
| 5.89 | `CaptureRefiner` | all active participants may snapshot refiner facts |
| 10.95 | `CapturePreCoreVideoMedia` | root-owner architecture only |
| 11.05 | `DropCoreImageToVideoOutput` | root-owner architecture only |
| 11.4 | `ApplyRootAudioMaskDimensions` | root-owner architecture only |
| 11.5 | `RunConfiguredStages` | common timeline execution and publication |

SwarmUI core image-to-video is expected around priority 11. The extension
discovers that step at startup and disables unsafe handoff when it is missing
or ambiguous.

`ArchitectureRootOwnerResolver` selects at most one architecture: the first
clip whose planned input consumes host root media or an empty latent. A init-video
clip owns its own media and does not claim the host root.

`ArchitectureHostPhasePolicy` is the exhaustive scope table. Adding a phase
requires adding it to the enum and this switch; phases are not ad hoc string
hooks.

## 4. Provider, factory, and session lifetimes

The three runtime layers are intentionally different:

| Object | Lifetime | May own mutable generation state? |
| --- | --- | --- |
| `IArchitectureGenerationSessionFactoryProvider` | request | No; reused across preflight/host phases/factory creation |
| `IArchitectureGenerationSessionFactory` | one timeline execution | Yes; timeline preparation and optional finalization |
| `IVideoGenerationSession` | one active architecture in one timeline | Yes; executes all clips owned by that architecture |

`ArchitectureRuntimeSessionFactoryRegistry` creates active factories only after
request preparation. Its lifecycle is:

1. `PrepareTimeline` for each active factory, with `OwnsGeneratedRoot`;
2. `CreateSession` once per active architecture;
3. execute clips through `ArchitectureRuntimeDispatcher`;
4. dispose every session, including constructor rollback on partial failure.

## 5. Common clip loop

`VideoStagesCoordinator` owns the timeline-level sequence:

1. capture the host root publication/save contract;
2. install global refine source when the compiled root requires it;
3. resolve request audio sources;
4. reserve the staged node-ID range;
5. prepare active architecture factories;
6. execute the clip sequence;
7. apply configured timeline interpolation;
8. clear model compatibility from neutral final media;
9. capture and publish the final artifact;
10. invoke architecture finalization.

`StageSequenceRunner` loops planned clips in order. It exposes
`PreviousClipOutput` only across a same-architecture non-cut boundary.
`PreviousTimelineClipOutput` remains available as contextual decoded media
across cuts. The dispatcher selects a session solely by the planned
`ArchitectureId`, verifies that the returned architecture matches both the
selected session and planned clip, then verifies clip identity and decoded
shape.

Every architecture returns `DecodedClipArtifact`: decoded video, optional
decoded audio, literal dimensions/FPS/frame count, architecture ID, and clip
ID. Latents, VAEs, models, compatibility IDs, and opaque payloads cannot cross
this common boundary.

## 6. Stage loops

### WAN and generic host video

`HostVideoStageEngine` is the proven common intersection. It owns:

- stage iteration;
- pixel-upscale ordering;
- passthrough decoded-input configuration;
- reversible per-stage host parameter sections;
- intermediate publication;
- terminal global trim; and
- capture of the final neutral artifact.

`StockHostVideoGenerationSession` owns the common stock-host execution path and
uses `HostVideoStageEngine` for stage iteration. Generic host video uses that
session directly. WAN supplies one optional concrete
`WanStockHostVideoBehavior` collaborator for first/final-frame materialization,
temporal snapping, native final-frame conditioning, and 5B cleanup. This is not
a generic callback or policy interface: it is the bounded WAN addition to the
otherwise shared stock path.

Both paths delegate supported graph construction to SwarmUI's stock video
primitives. VideoStages validates its own document topology and added
features; it does not try to reproduce every core model-path validity rule.

### LTX

LTX does not use `HostVideoStageEngine`. Its runtime has different latent,
conditioning, audio, IC-LoRA, guide, retake, and post-video-chain semantics:

```text
LTX private generation session
    → StageClipExecutor
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

## 7. Assembly and publication

`TimelineAssemblySession` delegates a same-architecture non-cut run to that
architecture's `IArchitectureBoundaryAssembler`. It joins the resulting runs
with neutral hard cuts and assembles decoded audio.

`RootRuntimeSession` and `OutputPublisher` are the only normal writers of the
captured host save set. An exclusive finalizer runs only after publication and
may replace that publication; no stage session may publish the whole timeline
directly.

## 8. Failure boundary

The first exception in a prepared host phase, alternate callback, stage run,
assembly, publication, or finalization transitions the request to `Failed`.
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
| JSON version, types, IDs, common structural shape | `VideoStagesJsonReader` / `VideoStagesSpecParser` |
| Model resolution, clip lock, entry mode, common timing/boundary/audio topology | `ArchitecturePlanResolver` / `VideoExecutionPlanCompiler` |
| Unsupported optional authored features | Common effective-request projection, using resolved capabilities; warn-and-omit on the effective copy |
| Architecture-specific options and semantic conflicts | Selected module's graph-free projector/compiler |
| Architecture dependencies | Active runtime provider during request preparation |
| Ordinary model-path validity already owned by a supported SwarmUI primitive | SwarmUI core during graph construction |
| Returned identity and decoded media shape | `ArchitectureRuntimeDispatcher` |
| Cross-clip run validity and final publication contract | `TimelineAssemblySession` / `OutputPublisher` |

## 9. Generated binding retention audit

The ComfyTyped surface has two different meanings of “retention”:

1. code-generation pruning; and
2. .NET linker trimming.

For code-generation pruning, `comfytyped.keep.json` retains the
`custom_nodes.ComfyUI-LTXVideo` module and the `SwarmFrameImage` class type.
Generation expands those facts into `src/Generated/PruneManifest.g.cs`.
The current generated directory contains 83 node wrappers: 78 are in
`PruneManifest.AlwaysKeep`; the remaining five are extension-owned Swarm nodes
directly referenced by production C# and are therefore discovered by the prune
source scan:

- `SwarmAudioLengthToFramesNode`;
- `SwarmFrameWindowNode`;
- `SwarmPromptRelayEncodeNode`;
- `SwarmRampMaskBatchNode`; and
- `SwarmSetAudioMaskWindowsNode`.

`GeneratedBindingRetentionTests` makes that 78-plus-5 classification exhaustive
and verifies that every manifest entry names an existing unique generated node.

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
4. implement provider → factory → session lifetimes;
5. add a boundary assembler before declaring non-cut support;
6. return a valid neutral decoded artifact;
7. add frontend-local behavior only for concrete bespoke UI; and
8. test mixed clips, failure cleanup, and prepared-state enforcement.

Do not add a second top-level runner, recognize models in the frontend, or add
generic “before/after stage” hooks for behavior that belongs in one module.
