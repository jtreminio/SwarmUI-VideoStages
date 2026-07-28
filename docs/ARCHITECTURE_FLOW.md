# VideoStages architecture flow

This is the start-here map for two end-to-end paths:

1. selected model → architecture identity → visible frontend features;
2. generation request → planned architecture → model-specific graph → neutral
   timeline output.

For the detailed execution and frontend designs, continue to
[`ARCHITECTURE.md`](../ARCHITECTURE.md) and
[`FRONTEND_ARCHITECTURE.md`](../FRONTEND_ARCHITECTURE.md).

VideoStages is a closed-world modular monolith. Production currently registers
the source-only `none` architecture and LTX Video 2.3 (`ltx2`). WAN is not
registered, so the architecture layer has not yet been tested by a second
generating architecture.

## Ownership

| Concern | Owner | Concrete entry points |
|---|---|---|
| Production registration | SwarmUI adapter | `VideoStagesExtension.OnInit`, `VideoArchitectureManifest` |
| Exact model recognition | Backend architecture module | `VideoArchitectureRegistry.TryResolveModel`, `Ltx2ArchitectureModule.TryResolveModel` |
| Capabilities and rules | Backend architecture module | `Ltx2ArchitectureModule.Descriptor`, `NoneArchitecture.Descriptor` |
| Catalog transport | Common backend | `VideoStagesApi.VideoStagesGetArchitectureCatalog`, `ArchitectureCatalogSerializer.Serialize` |
| Catalog loading and feature policy | Common frontend | `loadAuthoritativeArchitectureCatalog`, `parseVideoArchitectureCatalog`, `createCapabilityViewResolver` |
| Architecture-specific authoring behavior | Frontend architecture module | `VIDEO_ARCHITECTURE_MODULES`, `ArchitectureBehavior`, `authoringPanels.ts` |
| Document parsing and product planning | Common backend | `VideoStagesSpecParser`, `ArchitecturePlanResolver`, `VideoExecutionPlanCompiler` |
| Model-family planning and execution | Selected backend module | `IVideoArchitectureModule.ValidateAndCompileClip`, `IVideoGenerationSession` |
| Runtime dispatch and timeline assembly | Common backend | `StageSequenceRunner`, `ArchitectureRuntimeDispatcher`, `TimelineAssemblySession` |
| Final host publication | SwarmUI adapter | `RootRuntimeSession`, `OutputPublisher` |

The boundary in one sentence:

> Common code owns VideoStages product semantics; architecture code owns
> model-family semantics; SwarmUI owns host policy and lifecycle.

## Flow A: model selection to frontend features

### A1. Backend registration and exact recognition

`VideoStagesExtension.OnInit` registers dependencies, API routes, host handlers,
and the ordered `Runner` workflow steps. The backend composition root is
`VideoArchitectureManifest.Production`; a `VideoArchitectureRegistration`
keeps a module, runtime provider, host handlers, API routes, and dependencies
together.

`VideoArchitectureRegistry.ResolvedModels` enumerates
`Program.MainSDModels.Models` and asks each registered
`IVideoArchitectureModule.TryResolveModel` to recognize each installed model.
The registry rejects duplicate architecture/profile IDs, invalid module
results, ambiguous model matches, and invalid default profiles.

`Ltx2ArchitectureModule.TryResolveModel` accepts a model only when:

- `model.ModelClass.CompatClass.ID` is
  `T2IModelClassSorter.CompatLtxv2.ID`; and
- `model.ModelClass.ID` is `lightricks-ltx-video-2-3`
  (case-insensitive).

It returns `ArchitectureId("ltx2")` and `ModelProfileId("ltx-2.3")`.
`NoneArchitectureModule.TryResolveModel` always returns false; common planning
assigns `none` only to source-video clips with no active stages.

Backend recognition is the execution authority. A persisted architecture ID
or frontend classification cannot authorize an unsupported model.

### A2. Capability declaration and transport

`Ltx2ArchitectureModule.Descriptor` and `NoneArchitecture.Descriptor` are
typed `VideoArchitectureDescriptor` values. They declare architecture, clip,
stage, profile, boundary, audio-source, and output support. The same typed
boundary/rule objects feed backend validation and frontend publication.

`ArchitectureCatalogSerializer.Serialize` projects the descriptor catalog and
the currently resolved host models to:

```text
architectures[] = descriptor + capabilities + profiles + rules
models[]        = modelName + architectureId + modelProfileId + compatId
```

`VideoStagesApi.VideoStagesGetArchitectureCatalog` exposes that projection as
the `VideoStagesGetArchitectureCatalog` API call.

### A3. Frontend boot and catalog adoption

`frontend/main.ts` creates `videoStagesTimeline()` and schedules
`initTimeline` after SwarmUI builds parameters. Initialization retries until
the hidden `input_videostages` carrier exists.

`videoStagesTimeline.init` binds timeline collaborators, renders immediately,
then starts `adoptArchitectureCatalog()` asynchronously.
`loadAuthoritativeArchitectureCatalog`:

1. coalesces concurrent requests;
2. calls the API through `VideoStagesHostBridge.requestJson`;
3. validates the response all-or-nothing with
   `parseVideoArchitectureCatalog`;
4. caches a valid response until `invalidateArchitectureCatalog`.

The host param-refresh hook in `createTimelineHostLifecycle` invalidates and
reloads the catalog so newly installed models can appear without a page reload.
Adding a new clip waits for the coalesced request before selecting its initial
model, but the initial timeline render does not.

#### Current compromise: two frontend catalog authorities

Until a backend response is cached, `buildArchitectureModelCatalog` calls
`bootstrapArchitectures(videoArchitectureRegistry)`.
`VIDEO_ARCHITECTURE_MODULES` registers bundled LTX and `none` descriptors, and
`ltx2Architecture.resolveModelProfile` includes a model-name regex fallback.
On API failure the UI logs a warning and continues with this bootstrap.

That is current behavior, not the intended boundary. The target is:

- backend data is the only capability/model authority;
- the UI has explicit loading, ready, and unavailable/retry states;
- a refresh failure may keep the last valid in-memory catalog;
- frontend modules own rendering behavior, not duplicate capabilities or
  model recognition.

### A4. Selection, identity, and feature visibility

`getRootDefaults` builds `RootDefaults.modelCatalog` from the current SwarmUI
model dropdown and the active backend/bootstrap catalog.
`appendStageModelSection` uses it to build model options:

- stage 0 may select a model whose architecture supports the clip's entry mode;
- later stages use `architectureCatalogView` and stay inside the clip's
  architecture;
- unsupported persisted values remain visible and disabled.

A stage-0 architecture change is an explicit, confirmed
`clip.convert-architecture` command planned by
`planArchitectureConversion`. Later stages use `stage.retarget-model` and
cannot change the clip architecture.

`deriveClipArchitectureIdentity` verifies catalog identity and same-architecture
authored stages. A sourced clip with no active stage executes as `none` while
retaining dormant authored identity for restoration.

`currentCapabilityViewResolver` creates catalog-backed `ClipCapabilityView`,
`StageCapabilityView`, and boundary views. Timeline tracks and detail panels ask
`decision(feature)` / `authoringState(feature, hasPersistedValue)` to decide
visibility, enablement, reason text, and repair behavior. Unsupported persisted
data stays visible for removal rather than disappearing during normalization.

Architecture-specific *how* behavior dispatches separately by architecture ID
through `ArchitectureBehavior`. Only `ltx2Behavior` implements it today, and
the interface is mostly IC-LoRA-shaped. LTX DOM rendering is registered
separately in `authoringPanels.ts`. This abstraction should be reassessed after
a second generating architecture supplies another concrete use case.

### Flow A failures

| Failure | Current result |
|---|---|
| Data input never appears | `main.ts` retries and emits a watchdog warning. |
| Backend registration is invalid | `VideoArchitectureRegistry` construction throws. |
| Model matches no module | It is absent from the backend model catalog; planning later reports an unresolved model. |
| Model matches multiple modules | `VideoArchitectureRegistry.TryResolveModel` throws. |
| Catalog wire data is malformed | `parseVideoArchitectureCatalog` rejects the whole response. |
| Initial API request fails | Current UI continues with frontend bootstrap; this is a known authority leak. |
| Persisted identity is inconsistent | Frontend retains it for diagnosis/repair; backend planning blocks execution. |
| Feature is unsupported | Frontend disables or offers repair; backend capability validation rechecks it. |

## Flow B: request to architecture-specific execution

### B1. Parse and cache a graph-free plan

The frontend writes the versioned document to
`VideoStagesExtension.Data` (`input_videostages`) and clip prompt sections to
the prompt carrier. `VideoStagesPromptSection.IsActive` requires an enabled
group and non-empty Data JSON.

Every `Runner` phase calls `RequireVideoExecutionPlanContext`. The first lookup
builds and caches one plan per `WorkflowGenerator` through
`VideoStagesContext`:

```text
VideoStagesJsonReader / VideoStagesSpecParser
    → ArchitecturePlanResolver
    → VideoExecutionPlanCompiler
    → VideoExecutionPlanContext
```

`VideoStagesJsonReader` requires schema version 5.
`VideoStagesSpecParser` applies prompt overrides and parses clips, authored
stages, source media, dimensions, FPS, and timeline audio into
`VideoStagesSpec`.

### B2. Select `ArchitectureId` and compile opaque payloads

`ArchitecturePlanResolver.ResolveAuthoredStages` resolves every authored stage
model, including skipped stages, through the backend registry. For a generated
clip, the first authored stage selects the module and `ArchitectureId`;
`ValidateClipIdentity` checks persisted identity and
`ValidateSameArchitecture` requires all authored stages to use that
architecture. A source-only executable clip receives `NoneArchitectureModule`.

`VideoExecutionPlanCompiler.Compile` is pure and graph-independent. For each
clip it:

1. resolves `ArchitectureEntryMode`;
2. runs `ArchitectureCapabilityValidator.Validate`;
3. calls the selected
   `IVideoArchitectureModule.ValidateAndCompileClip`;
4. attaches the returned opaque `IArchitectureClipPayload` and stage payloads
   to common `ClipPlan` / `StagePlan`;
5. compiles common root, geometry, boundary, and audio plans;
6. runs architecture whole-plan validators.

For LTX, `Ltx2ClipPlanCompiler.Compile` produces `Ltx2ClipPayload` and
`Ltx2StagePayload` instructions for LTX audio, prompt relay, guides, upscale,
LoRA, IC-LoRA, retake, frame references, and stage audio actions. Common
orchestration carries these values; it must not interpret their graph meaning.

Blocking `PlanDiagnostic` values are thrown by
`RequireVideoExecutionPlanContext` before a VideoStages mutation phase.

### B3. Preflight before VideoStages graph mutation

`Runner.PreflightRequest` is the first registered VideoStages workflow phase.
It calls `VideoArchitectureExecutionHost.PreflightRequest`, which invokes each
active runtime provider with graph-free
`ArchitectureRequestPreflightContext`.

`Ltx2RequestPreflight.Resolve` checks planned IC-LoRA dependencies: required
ComfyUI-LTXVideo nodes/features and resolvable IC-LoRA weights. Blocking
diagnostics stop the request before later VideoStages host phases mutate the
graph.

“Before mutation” here means before **VideoStages** mutation. SwarmUI may
already have built host graph state that VideoStages captures or replaces.

### B4. Host phases prepare selected architecture state

Later `Runner` phases dispatch through
`VideoArchitectureExecutionHost.DispatchHostPhase`.
`ArchitectureHostPhasePolicy` chooses all-active versus root-owner-only scope;
`ArchitectureRootOwnerResolver` selects the one architecture allowed to
transform host-root media.

`Ltx2ExecutionAdapter.ExecuteHostPhase` currently handles ControlNet
preprocessor capture, base/refiner references, pre-core handoff, core-output
drop, and root audio-mask sizing.

Two current pre-WAN risks are important:

- `StageRefStore` uses keys such as `base`, `refiner`, `generated`, and
  `preroot` without `ArchitectureId` scope. Only LTX writes them today.
- `ControlNetCoreMediaCapture` is called by both LTX and
  `SourceOnlyExecutionAdapter`, but it also enforces a multiple-of-64 resize and
  one-frame wrapping. Those are LTX rules in common capture code, so even a
  source-only timeline can receive LTX normalization. The intended split is
  common raw capture followed by LTX-owned normalization.

### B5. Dispatch a clip by architecture

`Runner.RunConfiguredStages` enters:

```text
VideoArchitectureExecutionHost
    → VideoStagesCoordinator
    → StageSequenceRunner
    → ArchitectureRuntimeDispatcher
```

The coordinator captures the host root, resolves audio, prepares active
architecture factories, and begins the clip sequence.
`StageSequenceRunner` creates `ArchitectureClipRuntimeContext` for each planned
clip. It exposes previous output as continuity input only for a non-cut,
same-architecture boundary.

`ArchitectureRuntimeDispatcher.ResolveSession` selects a session solely from
`clip.Architecture.Id`. It does not repeat model-name checks.

Current sessions copy most of that context into
`ArchitectureClipExecutionRequest.RuntimePayload` (`StageClipExecutionContext`
for LTX, `SourceOnlyClipExecutionContext` for `none`) and execute immediately.
That field-for-field repack is current ceremony, not a required boundary.

### B6. LTX graph execution

`Ltx2ExecutionAdapter.CreateFactory` builds the LTX runtime collaborators.
`Ltx2GenerationSessionFactory` prepares LTX root state when LTX owns it and
creates `Ltx2GenerationSession`.

The LTX path is:

```text
Ltx2GenerationSession.Execute
    → StageClipExecutor.Execute
    → StageRunner.RunStage
    → LtxStageExecutor.RunStage
    → LtxStageOutputFinalizer.Complete
    → StageRuntimeArtifactCapture
    → DecodedClipArtifact.FromRuntime
```

`StageClipExecutor` installs source media if planned, prepares LTX boundary and
audio state, and loops stages. `StageRunner` owns passthrough versus generated
execution and prepares guides, references, upscale, and IC-LoRA input.
`LtxStageExecutor` builds LTX model/prompt state, latent, conditioning, sampler,
and decoded video/audio output. LTX node choices, latent/VAE handling, audio
splitting, IC-LoRA, and post-video-chain behavior remain under
`src/Architectures/Ltx2`.

The `none` path uses `SourceOnlyGenerationSession` and
`SourcedClipInstaller`; it builds no generation latent, VAE, or stage runtime.

### B7. Return neutral artifacts and publish

`DecodedClipArtifact` is the architecture-neutral handoff. It contains decoded
video, optional decoded audio, literal dimensions/FPS/frames, and
architecture/clip provenance. It cannot carry a latent, VAE, model
compatibility, or architecture payload.

`ArchitectureRuntimeDispatcher` verifies returned identity and calls
`ValidateDecoded`. `TimelineAssemblySession` then:

- delegates same-architecture non-cut runs to the registered
  `IArchitectureBoundaryAssembler` (currently `Ltx2BoundaryAssembler`);
- joins architecture runs with neutral hard cuts;
- assembles decoded audio;
- installs the final decoded media.

`VideoStagesCoordinator` clears model compatibility from final media and
publishes through `RootRuntimeSession.PublishTimeline` / `OutputPublisher`.
Architecture finalization runs only after publication; LTX currently claims
exclusive finalization only for an all-LTX HDR timeline.

### Flow B failures

| Failure | Stopped by |
|---|---|
| Malformed JSON or wrong schema | `VideoStagesJsonReader` |
| Unknown model/profile, mixed clip architecture, forged identity | `ArchitecturePlanResolver` diagnostics |
| Unsupported entry mode or feature | `ArchitectureCapabilityValidator` diagnostics |
| Invalid LTX option | LTX clip/plan compiler diagnostics |
| Invalid common geometry, boundary, or audio plan | Common compiler diagnostics |
| Missing IC-LoRA dependencies | `Ltx2RequestPreflight` before later VideoStages mutation |
| Missing provider/session | `VideoArchitectureExecutionHost` / `ArchitectureRuntimeDispatcher` |
| Wrong returned identity or decoded media | Dispatcher identity checks / `DecodedClipArtifact.ValidateDecoded` |
| Invalid cross-architecture non-cut run | `MultiClipParallelMerger` |
| Unpublishable final media | `RootRuntimeSession` / `OutputPublisher` |

## Invariants

1. Exact backend model recognition remains authoritative.
2. The frontend receives capabilities; it does not infer execution
   architecture from model names.
3. The persisted schema remains typed and versioned.
4. Unknown/future architecture data follows an explicit round-trip policy and
   is never silently discarded.
5. Planning, validation, and request preflight happen before VideoStages graph
   mutation.
6. Common orchestration never interprets architecture graph instructions.
7. Runtime dispatch uses `ArchitectureId`, not scattered model-name tests.
8. Common cross-stage/clip handoffs use neutral artifacts, not architecture
   payloads.
9. Mixed-architecture boundaries are explicit; hard cut is the safe initial
   policy.
10. Dispatch identity assertions and decoded-output validation remain enabled
    in production.
11. Timeline edits remain commands/diffs with undo semantics.
12. Source-only and generated execution follow the same ownership rules.

Known current exceptions or transition seams are the frontend bootstrap
authority, LTX normalization inside common ControlNet capture, unscoped runtime
reference keys, and the redundant runtime-request repack. Do not copy those
patterns into a new architecture.
