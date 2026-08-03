# VideoStages architecture flow

This is the start-here map for two end-to-end paths:

1. selected model → architecture identity → visible frontend features;
2. generation request → planned architecture → model-specific graph → neutral
   timeline output.

For the detailed execution and frontend designs, continue to
[`ARCHITECTURE.md`](../ARCHITECTURE.md) and
[`FRONTEND_ARCHITECTURE.md`](../FRONTEND_ARCHITECTURE.md). For exact workflow
priorities, runtime lifetimes, and stage-loop ownership, use
[`STAGE_RUNTIME.md`](STAGE_RUNTIME.md).

VideoStages is a modular monolith with specialized overlays and a conservative
host-video fallback. Production registers the source-only `none` architecture,
specialized LTX Video 2.3 (`ltx2`), MiniMax H3 (`minimax`), the WAN family
(`wan22`), and a cut-only generic profile (`host-video`) for exact SwarmUI model
classes whose stock video graph branches have been verified.

## Ownership

| Concern | Owner | Concrete entry points |
|---|---|---|
| Production registration | SwarmUI adapter | `VideoStagesExtension.OnInit`, `VideoArchitectureManifest` |
| Exact model recognition | Backend architecture module | `VideoArchitectureRegistry.TryResolveModel`, `Ltx2ArchitectureModule.TryResolveModel`, `MiniMaxArchitectureModule.TryResolveModel`, `WanArchitectureModule.TryResolveModel`, `HostVideoArchitectureModule.TryResolveModel` |
| Capabilities and rules | Backend architecture module | `Ltx2ArchitectureModule.Descriptor`, `MiniMaxArchitectureModule.Descriptor`, `WanArchitectureModule.Descriptor`, `HostVideoArchitectureModule.Descriptor`, `NoneArchitecture.Descriptor` |
| Catalog transport | Common backend + SwarmUI authorization | `VideoStagesApi.VideoStagesGetArchitectureCatalog`, `AuthorizedArchitectureRegistry`, `ArchitectureCatalogSerializer.Serialize` |
| Catalog loading and feature policy | Common frontend | `getArchitectureCatalogSnapshot`, `loadAuthoritativeArchitectureCatalog`, `refreshAuthoritativeArchitectureCatalog`, `parseVideoArchitectureCatalog`, `createCapabilityViewResolver` |
| Architecture-specific authoring behavior | Frontend architecture-gated helpers | `behaviorRegistry.ts`, `authoringPanels.ts`, architecture ID identity modules |
| Curated IC-LoRA download route | LTX backend adapter + SwarmUI core | `Ltx2ApiRoutes`, `ModelsAPI.DoModelDownloadWS` |
| Document parsing and product planning | Common backend | `VideoStagesSpecParser`, `ArchitecturePlanResolver`, `VideoExecutionPlanCompiler` |
| Model-family planning and execution | Selected backend module | `IVideoArchitectureModule.ValidateAndCompileClip`, `IVideoGenerationSession` |
| Runtime dispatch and timeline assembly | Common backend | `VideoArchitectureExecutionHost`, `TimelineAssemblySession` |
| Final host publication | SwarmUI adapter | `RootRuntimeSession`, `OutputPublisher` |

The boundary in one sentence:

> Common code owns VideoStages product semantics; architecture code owns
> model-family semantics; SwarmUI owns host policy and lifecycle.

For curated IC-LoRA downloads, `Ltx2ApiRoutes` owns the preset ID-to-URL/name
mapping and route permission, refuses unknown preset IDs and already-in-flight
preset IDs locally, and delegates everything else — transfer, model-refusal
policy, cancellation, temporary-file lifecycle, refresh, and resave — to
SwarmUI's `ModelsAPI.DoModelDownloadWS`. Extension tests cover both local
refusals, the delegated URL/model name, and core's terminal error shape; they do
not duplicate core's transfer implementation.

Open host dependency: core keys an unlocked `<name>.download.tmp` by model name
and leaves it behind after a failed or canceled transfer, until the next attempt
for that name deletes it. The route's in-flight refusal covers only its own
traffic; a curated download and a model-browser download of the same name can
still collide, and the extension does not clean core's temporary file. Closing
that belongs to core or to a shared safe-download service, not to this route.

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
results, ambiguous model matches within the winning resolution tier, and
invalid default profiles. Specialized matches win over fallback matches, so a
generic registration cannot steal an LTX, MiniMax, or WAN model. API projection
and planning wrap that registry in `AuthorizedArchitectureRegistry`, which
removes models the requesting SwarmUI session is not allowed to see. It
authorizes the canonical resolved model name rather than the authored spelling,
because core resolves both `name` and `name.safetensors` while blacklist and
whitelist prefixes match the exact string they are given. A session with no user
carries no authorization context and is left unfiltered — the same convention
session-owned media uses; both production entry points, the catalog route and
plan compilation, always carry a request session.

`Ltx2ArchitectureModule.TryResolveModel` accepts a model only when:

- `model.ModelClass.CompatClass.ID` is
  `T2IModelClassSorter.CompatLtxv2.ID`; and
- `model.ModelClass.ID` is `lightricks-ltx-video-2-3`
  (case-insensitive).

It returns `ArchitectureId("ltx2")` and `ModelProfileId("ltx-2.3")`.
`MiniMaxArchitectureModule.TryResolveModel` accepts only the `minimax-h3` model
class under core's MiniMax H3 compatibility class. It returns
`ArchitectureId("minimax")` and `ModelProfileId("minimax-h3")`.
`WanArchitectureModule.TryResolveModel` accepts ordinary WAN 2.1/2.2 video
models and gives the family text and image entry. It explicitly rejects VACE,
LoRA, and VAE component classes. Two exact legacy pairs retain special profile
aliases:

- `wan-2_2-image2video-14b` / `wan-21-14b` resolves to `wan22` /
  `wan-2.2-i2v-14b`; and
- `wan-2_2-ti2v-5b` / `wan-22-5b` resolves to `wan22` /
  `wan-2.2-ti2v-5b`.

The exact identifiers are compatibility aliases rather than the recognition
allowlist. Other ordinary WAN models resolve to the generic
`wan-i2v` profile; first/last-frame and native 5B behavior are not inferred for
that alias. The 14B profile remains the descriptor default for compatible
runtime routing, but these aliases are not separate user-facing
text-versus-image families.
`HostVideoArchitectureModule.TryResolveModel` is the last-priority baseline. It
does not trust `IsText2Video` / `IsImage2Video` by themselves. Its proof table
admits exact stock branches for Hunyuan Video, Hunyuan Video 1.5, Mochi,
Cosmos 1, Kandinsky 5 Video, LTX Video 1, and non-2.3 LTX Video 2. Cosmos
Predict2, SVD, component LoRA/VAE checkpoints, Hunyuan 1.5 SR, and unknown
synthetic video classes remain unresolved.
`NoneArchitectureModule.TryResolveModel` always returns false; common planning
assigns `none` only to init-video clips with no active stages.

Backend recognition is the execution authority. A persisted architecture ID
or frontend classification cannot authorize an unsupported model.

### A2. Capability declaration and transport

`Ltx2ArchitectureModule.Descriptor`, `MiniMaxArchitectureModule.Descriptor`,
`WanArchitectureModule.Descriptor`, `HostVideoArchitectureModule.Descriptor`,
and `NoneArchitecture.Descriptor` are typed `VideoArchitectureDescriptor` values.
Each resolved model owns its complete effective capabilities. The descriptor
publishes architecture defaults; model selection,
diagnostics, conversion, planning, and runtime authorization use the exact
selected model facts, which may narrow those defaults. Every accepted WAN model
publishes text, image, and source entry. Those entry modes describe the current
request's input, not different user-facing WAN model categories.

WAN publishes same-compatibility-family stage chaining, video-only output, a
four-frame profile grid, and cut-only boundaries. Every WAN compatibility alias
publishes ordinary persisted clip/stage and prompt-section LoRAs.
Image-generated stage 0 uses the host root at full control. WAN text entry uses
an empty latent and does not decode or reinterpret the host's donor image.
InitVideo stage 0 uses its conformed source at finite control in `[0, 1]`; each
later stage uses `PreviousStage` with the same bound. Exact control `0` is a
samplerless decoded-video passthrough for those two decoded inputs, while
positive partial control still must quantize to a nonzero start step.
Audio capabilities remain absent. The same typed boundary/rule objects feed
backend validation and frontend publication.

MiniMax publishes text/image entry, native/uploaded/AceStepFun audio, timeline
audio segments, audio-derived duration for external tracks, first/last frame
references, decoded previous-stage chaining, its 17-frame grid with a five-frame
origin, and cut-only clip boundaries. Its timeline segments do not authorize
boundary audio carry.

The generic descriptor supports source entry through the same neutral
conformance path used by WAN. Model-level
facts still say whether a checkpoint can enter from text, image, or both, so a
text-only model such as Mochi cannot occupy a decoded later-stage role. The
baseline advertises prompts, ordinary LoRAs, source video, pixel resize, decoded output, and
hard-cut stage chaining where selected models have image entry. It does
not advertise arbitrary authored references, audio, IC-LoRA,
advanced upscalers, end-frame conditioning, or swap.

`ArchitectureCatalogSerializer.Serialize` projects the descriptor catalog and
the currently resolved, session-authorized host models to:

```text
schemaVersion   = 2
architectures[] = id + label + complete capabilities + boundary rules + rules
models[]        = identity + core facts + frame grid
                  + complete effective capabilities + reference positions
```

`VideoStagesApi.VideoStagesGetArchitectureCatalog` exposes that projection as
the `VideoStagesGetArchitectureCatalog` API call. The v2 wire deliberately has
no profile table, extras overlay, duplicate entry-mode alias, or separate
output-capability alias.

### A3. Frontend boot and catalog adoption

`frontend/main.ts` creates `videoStagesTimeline()` and schedules
`initTimeline` after SwarmUI builds parameters. Initialization retries until
the hidden `input_videostages` carrier exists.

`videoStagesTimeline.init` binds its event collaborators, but the initial
timeline render is a catalog status view. It does not read, normalize, hydrate,
or render the authoring document until an authoritative catalog exists.
History rebasing and the host carrier-sync poll use the same readiness gate.

`catalogRepository.ts` exposes an explicit snapshot state machine:

```text
loading    = no catalog, request in flight
unavailable= no catalog, last request failed
ready      = authoritative catalog
refreshing = retained catalog, replacement request in flight
stale      = retained catalog, replacement request failed
```

`loadAuthoritativeArchitectureCatalog` coalesces initial/cached loads.
`refreshAuthoritativeArchitectureCatalog` forces a request without clearing the
last-known DTO. There is at most one request in flight and at most one pending
refresh behind it: forced refreshes raised during a request share that single
pending one, which starts when the request settles, so a model-install signal
cannot be consumed by an older response. A monotonic request generation lets
only the current request publish state.
`subscribeArchitectureCatalog` reports every request-start and settled
snapshot, so the timeline paints the `loading`/`refreshing` transition it is
actually in — including the pending refresh's later start — instead of painting
ahead of the repository.

Every response is validated all-or-nothing by
`parseVideoArchitectureCatalog`. Initial failure or malformed data enters
`unavailable`; the status view offers Retry and no authoring controls. Every
successful catalog adoption invalidates the parsed timeline-store cache and
rebases history against the newly normalized document before rendering. The
history implementation retains its stacks when normalization is unchanged and
clears them when catalog interpretation changed. During refresh the
last-known catalog remains active. Refresh failure enters `stale`, retains
that exact DTO and its rendered capability-backed UI, and shows a nonblocking
warning with Retry.

The v2 decoder requires exact root, architecture, model, enhancement, and rule
keys; it rejects unknown/missing fields, duplicate architecture/model IDs,
dangling architecture references, unknown capability values, and malformed
constraints rather than constructing partial authority.

The host param-refresh hook uses forced refresh, so newly installed models can
appear without a page reload and without a temporary loss of authority.
`buildArchitectureModelCatalog` uses backend DTO identity only: it may decorate
backend-known models with current host dropdown labels and keeps backend-only
models, but a host model absent from the backend catalog has null
architecture/profile identity. Frontend identity modules contain only stable
IDs used to select local LTX behavior and DOM panels; they declare no
capabilities and perform no model recognition.

### A4. Selection, identity, and feature visibility

`captureAuthoringTransactionSnapshot` reads catalog state once, then builds
`RootDefaults.modelCatalog`, the capability resolver, and generated entry mode
from that exact DTO plus current SwarmUI inputs. One synchronous render, save,
or command dispatch passes that snapshot through its collaborators instead of
rereading live state. Without a ready or retained catalog the model catalog
contains no capability or model-identity authority.
`appendStageModelSection` uses it to build model options:

- stage 0 may select a model whose resolved facts support the clip's entry mode;
- later stages use `architectureCatalogView` and stay inside the clip's
  architecture;
- unsupported persisted values remain visible and disabled.

A stage-0 architecture change is an explicit, confirmed
`clip.convert-architecture` command planned by
`planArchitectureConversion`. Later stages use `stage.retarget-model` and
cannot change the clip architecture.

`deriveClipArchitectureIdentity` verifies catalog identity and same-architecture
authored stages. A init-video clip with no active stage executes as `none` while
retaining dormant authored identity for restoration. Persisted
`architectureHint` and profile hints are repair/display data only: a resolved
stage-0 model outranks them, and an unresolved hint never enables authoring
controls.

| Fact | Authority | What it controls |
|---|---|---|
| Executable architecture/profile identity | Backend-resolved stage-0 model | Planning, runtime dispatch, clip lock |
| Architecture descriptor capabilities | Backend architecture catalog record | Family overview and default policy |
| Effective model capabilities, grid, reference positions | Backend resolved-model catalog record | Model picker, sidebar/timeline feature gates, conversion |
| Persisted `architectureHint` / profile hints | Authoring document | Unresolved-model display and repair only |
| Local architecture behavior/panel gates | Frontend implementation | How an already-authorized bespoke control renders/edits |

The transaction's `CapabilityViewResolver` creates catalog-backed
`ClipCapabilityView`, `StageCapabilityView`, and boundary views. Timeline
tracks and detail panels ask
`decision(feature)` / `authoringState(feature, hasPersistedValue)` to decide
visibility, enablement, reason text, and repair behavior. Unsupported persisted
data stays visible for removal rather than disappearing during normalization.

Architecture-specific *how* behavior is gated separately by architecture ID.
Only `ltx2` has bespoke frontend behavior today, so the common helpers use an
explicit LTX branch instead of imposing an IC-LoRA-shaped polymorphic contract
on hypothetical future architectures. LTX DOM rendering is keyed directly by
the same ID in `authoringPanels.ts`. These gates own implementation behavior
only; labels, resolved model identities, capabilities, and rules remain backend
DTO data. A real second bespoke frontend can add a branch; extract a common
contract only after two implementations reveal one.

### Flow A failures

| Failure | Current result |
|---|---|
| Data input never appears | `main.ts` retries and emits a watchdog warning. |
| Backend registration is invalid | `VideoArchitectureRegistry` construction throws. |
| Model matches no module | It is absent from the backend model catalog; planning later reports an unresolved model. |
| Model matches multiple modules | `VideoArchitectureRegistry.TryResolveModel` throws. |
| Initial catalog request fails or wire data is malformed | The UI enters `unavailable`, renders no architecture-derived authoring controls, and offers Retry. |
| Catalog refresh fails or is malformed | The UI enters `stale`, retains the exact last-known DTO and rendered controls, and offers nonblocking Retry. |
| Host model is absent from the backend model catalog | It has null frontend architecture/profile identity and is unavailable for architecture authoring. |
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
    → EffectiveVideoRequestProjector (first step inside VideoExecutionPlanCompiler)
    → common + architecture plan compilation
    → VideoExecutionPlanContext
```

`VideoStagesJsonReader` requires schema version 6 after applying its only
bounded migration: a version-5 clip's `architecture` field becomes
`architectureHint`. `VideoStagesSpecParser` applies prompt overrides and parses
clips, authored stages, source media, dimensions, FPS, and timeline audio into
`VideoStagesSpec`.

### B2. Select `ArchitectureId`, project, and compile typed payloads

`ArchitecturePlanResolver.ResolveAuthoredStages` resolves every authored stage
model, including skipped stages, through the same session-authorized backend
registry used for catalog projection. A forbidden model is unresolved and
blocks planning before graph mutation. For a generated
clip, the first authored stage selects the module and `ArchitectureId`;
`ValidateClipIdentity` compares persisted hints for diagnostics only, and
`ValidateSameArchitecture` requires all authored stages to use the resolved
architecture. A source-only executable clip receives
`NoneArchitectureModule`.

`VideoExecutionPlanCompiler.Compile` is pure and graph-independent. It first
projects a non-mutating effective request from the resolved assignments.
Projection preserves clip/stage IDs, raw stage indexes, model names, source
identity, and topology, so the original assignments remain authoritative. For
each clip it then:

1. resolves `ArchitectureEntryMode`;
2. runs `ArchitectureCapabilityValidator.Validate`;
3. calls the selected
   `IVideoArchitectureModule.ValidateAndCompileClip`;
4. attaches the returned `IArchitectureClipPayload` and typed stage payloads to
   common `ClipPlan` / `StagePlan`; every stage payload exposes its required
   common core while its graph instructions remain architecture-owned;
5. compiles common root, geometry, boundary, and audio plans;
6. runs architecture whole-plan validators.

For LTX, `Ltx2ClipPlanCompiler.Compile` produces `Ltx2ClipPayload` and
`Ltx2StagePayload` instructions for LTX audio, prompt relay, guides, upscale,
LoRA, IC-LoRA, retake, frame references, and stage audio actions. Common
orchestration reads the required stage core and otherwise carries these values;
it must not interpret their graph meaning.
`NormalLoraPlanCompiler` is common graph-free planning shared by LTX, MiniMax,
WAN, and generic host video:
it resolves each stage's effective clip rows, keeps clip-before-stage ordering,
and leaves the resulting immutable array inside the selected architecture's
stage payload rather than common `StagePlan`. Its default model-and-text target
policy preserves LTX/generic text-encoder-only rows. WAN explicitly selects the
model-only policy at this seam.
Generic planning preserves same-clip compatibility-class uniformity and
selects the proven host family's model-only or model-and-text-encoder target
policy. Unsupported optional data is removed only from the effective request,
with a browser-visible warning; authored data is unchanged. This includes
architecture-specific references, prompt relay, retakes, audio options,
ControlNet strength, IC-LoRA, and advanced upscalers. Ordinary root-image
entry, source video, decoded stages, pixel resize, and normal LoRA remain.
For WAN, `WanClipPlanCompiler.Compile` produces the smaller `WanClipPayload`
and the shared `StockHostVideoStagePayload`; both preserve resolved host
identity. It requires one host compatibility class throughout a clip. A hard
cut starts a new clip and may select another family. The
compiler also enforces the generated-root / init-video / previous-stage
chain, refuses an effective LoRA plan on a samplerless passthrough, and refuses
unsupported or empty integer schedules that the common capability validator
cannot yet see. A clip-LoRA weight of zero is the supported per-stage disable
path. Direct/default clip and stage rows whose model and text-encoder weights
are both zero are omitted by the default policy. Under WAN's model-only policy,
every model-zero row is omitted even when its stored text-encoder weight is
nonzero; the samplerless-stage rule therefore sees the same effective plan the
WAN runtime can apply.

For MiniMax, `MiniMaxArchitectureModule.ValidateAndCompileClip` produces
`MiniMaxClipPayload` plus the shared `StockHostVideoStagePayload`. It compiles
first/last references, normal LoRAs, stage controls, and the shared stage core;
the H3 runtime owns the joint audio-video graph meaning.

Blocking `PlanDiagnostic` values are thrown by
`RequireVideoExecutionPlanContext` before a VideoStages mutation phase.

### B3. Preflight before VideoStages graph mutation

`Runner.PreflightRequest` is the first registered VideoStages workflow phase.
It is the only caller of `VideoExecutionPlanContext.PrepareRequest`, which
constructs one `VideoArchitectureExecutionHost` and invokes each active runtime
provider with graph-free `ArchitectureRequestPreflightContext`. Every later
phase calls `RequirePrepared`; preparation is never lazy after graph mutation
has begun.

`Ltx2RequestPreflight.Resolve` checks planned IC-LoRA dependencies: required
ComfyUI-LTXVideo nodes/features and resolvable IC-LoRA weights. Blocking
diagnostics stop the request before later VideoStages host phases mutate the
graph.
`WanExecutionAdapter.PreflightRequest` checks the few request-global host
options that need special WAN handling. Legacy request-global video-swap
values are not preflight errors: effective-request projection emits one warning and
`WanLegacySwapIsolation` clears them only from host generation info, without
editing `T2IParamInput`. High- and low-noise work is expressed as ordinary
authored stages. WAN first/last-frame conditioning is a bounded family
enhancement, not a different user-facing model category. When the selected
host path can use a final image, the last sampling stage owns it. When the
request shape cannot use it safely, VideoStages warns and continues without
it. Global creativity is expressed through authored stage Control values.

“Before mutation” here means before **VideoStages** mutation. SwarmUI may
already have built host graph state that VideoStages captures or replaces.
The full prepared-state machine and exact eight-step priority table are in
[`STAGE_RUNTIME.md`](STAGE_RUNTIME.md).

### B4. Host phases prepare selected architecture state

Later `Runner` phases dispatch through
`VideoArchitectureExecutionHost.DispatchHostPhase`.
`ArchitectureHostPhases.IsRootOwnerOnly` chooses root-owner-only versus all-active;
`ArchitectureRootOwnerResolver` selects the one architecture allowed to
transform host-root media.

For the ControlNet preprocessor phase,
`VideoArchitectureExecutionHost` invokes common
`ControlNetCoreMediaCapture` once to capture raw host media. It then fans out
to active architecture participants. `Ltx2ExecutionAdapter` derives its private
multiple-of-64 branch from that capture through `LtxControlNetMediaNormalizer`,
never from the current shared apply input, so the result does not depend on which
architectures ran before it. Wrapping the shared apply input down to one frame is
LTX root policy, so it happens only when LTX owns the host root; the source-only
path retains the raw capture.
The remaining LTX host phases handle base/refiner references, pre-core handoff,
core-output drop, and root audio-mask sizing.
`MiniMaxExecutionAdapter` captures the same base/refiner host reference points
and uses `RootMediaHandoff` for pre-core capture and discarded-core cleanup.
When WAN owns the generated host root, `HostVideoRootMediaHandoff` captures the
resolvable root image, its VAE state (which may be explicitly absent), and a
node snapshot, then restores them and prunes the host core video pass. Missing
or corrupt handoff state fails closed and clears all
`videostages.arch.wan22.*` handoff keys.
`PreviousStage` is not a host reference capture: it is the decoded,
Wan-local handoff between adjacent stages inside one generation session.

All LTX-owned `NodeHelpers` keys are architecture-scoped by
`LtxRuntimeKeyScope` under `videostages.arch.ltx2.*`. This LTX-private formatter
derives its fixed architecture ID from `Ltx2ArchitectureModule`; callers cannot
inject an unrelated architecture identity. It formats stage references, the
pre-core snapshot, normalized ControlNet media, and IC-LoRA audio-reference,
control-signal, and uploaded-drive caches.

### B5. Dispatch a clip by architecture

`Runner.RunConfiguredStages` enters `VideoArchitectureExecutionHost`.

The execution host captures the host root, resolves audio, creates one session for
each active architecture provider, and creates `ArchitectureClipRuntimeContext`
for each planned clip. It exposes previous output as continuity input only for
a non-cut, same-architecture boundary, while separately exposing the previous
timeline output as contextual media across cuts and architecture changes.

`VideoArchitectureExecutionHost` selects a session solely from `clip.Architecture.Id`,
passes the narrow per-clip context directly to that session, and validates that
the returned architecture matches both the selected session and planned clip
before validating clip identity and decoded-media shape. It does not repeat
model-name checks.

Timeline state such as the plan, prepared audio, assembly session, and root
policy is captured when each architecture session is created. LTX composes the
per-clip context with its private root and host state in
`StageClipExecutionContext`; MiniMax captures its root, audio sources, and base/
refiner references in `MiniMaxGenerationSession`; the init-video-only session
captures only frame rate and audio sources.

### B6a. LTX graph execution

`Ltx2ExecutionAdapter.CreateSession` prepares private LTX root state when LTX
owns it and returns the LTX timeline session. Common orchestration depends only
on the provider/session contracts.

The LTX path is:

```text
LTX private generation session
    → StageClipExecutor.Execute
    → VideoStageRunner.ExecuteStages
    → StageRunner.RunStage
    → LtxStageExecutor.RunStage
    → LtxStageOutputFinalizer.Complete
    → RuntimeArtifact.Capture
    → DecodedClipArtifact.FromRuntime
```

`StageClipExecutor` installs source media if planned, prepares LTX boundary and
audio state, then hands stage advancement to `VideoStageRunner`.
`VideoStageRunner` publishes each stage input, captures and validates each stage
output, and publishes intermediates. `StageRunner` owns passthrough versus
generated execution and prepares guides, references, upscale, and IC-LoRA input.
`LtxStageExecutor` builds LTX model/prompt state, latent, conditioning, sampler,
and decoded video/audio output. LTX node choices, latent/VAE handling, audio
splitting, IC-LoRA, and post-video-chain behavior remain under
`src/Architectures/Ltx2`.

The `none` path uses `SourceOnlyGenerationSession` and
`InitVideoClipInstaller`; it builds no generation latent, VAE, or stage runtime.

### B6b. MiniMax H3 graph execution

`MiniMaxExecutionAdapter` creates `MiniMaxGenerationSession`, which delegates
the common host-style stage lifecycle to the shared runner:

```text
MiniMaxGenerationSession.Execute
    → VideoStageRunner.Execute
    → MiniMaxGenerationSession.ExecuteGeneratingStage
```

The runner owns stage iteration, decoded upscale/passthrough handling, stage
scope, capture, validation, intermediate publication, and terminal trim. The
MiniMax procedure owns H3 prompt/model preparation, selected/timeline audio
combination and preserve-window encoding or native audio creation outside those
windows, audio-derived `17k+5` joint-latent length, joint audio-video latent
construction, first/last-frame keyframes, sampling, and joint decode.

### B6c. WAN on the shared stock-host runtime

`WanExecutionAdapter` creates `StockHostVideoGenerationSession` directly. The
session snapshots the host root media and VAE when timeline execution begins.
`StockHostVideoGenerationSession` prepares each hard-cut clip independently and
uses `VideoStageRunner` for common iteration, scope restoration,
pixel-upscale ordering, passthrough handling, intermediate publication,
terminal trim, and artifact capture. Its optional concrete
`WanStockHostVideoBehavior` collaborator owns only WAN first/final-frame
materialization, temporal snapping, native final-frame conditioning, and 5B
cleanup. Generated stage
0 resets to the captured root and delegates that first-image input to SwarmUI's
`WorkflowGenerator.CreateImageToVideo`. A text-input stage 0 prepares the
authored model and prompt conditioning through the host loader and creates an
unconditioned WAN latent without a start image. The exact internal node path
still follows the selected checkpoint's SwarmUI support, but it is not exposed
as a separate WAN model category. The stage samples with the authored steps,
CFG, sampler, scheduler, seed, dimensions, and frame count, then decodes the
result with the prepared VAE. An authored clip duration wins; otherwise text
entry uses the host text-to-video frame setting (default 81), and later stages
inherit the preceding decoded frame count. All counts are snapped to the
selected model's frame grid. A init-video clip instead uses
`InitVideoClipInstaller` to resample, window, and resize its exact clip-local
footage to WAN's snapped dimensions and requests video-only installation, so
the source-audio trim branch is never built. Exact control `0` preserves that
decoded source, or the immediately preceding decoded stage, without opening a
host model section or constructing conditioning, latent, or sampler nodes.
Eligible passthrough intermediates are still published. Full control
conditions from source frame 0 without VAE-encoding the source batch; positive
partial control conditions from frame 0 and VAE-encodes a distinct full
conformed-batch selector. Each later stage uses the same passthrough/full/
partial rules over the preceding decoded batch. Graph-free planning validates
the immutable entry, source, stage-input, payload, and per-clip compatibility
contract before the session mutates the graph; runtime still requires the
concrete WAN-owned payload at use. For the one permitted request-global
end-frame path, `BuildGenInfo` exposes the frame only while executing the stage
whose immutable WAN payload owns it; every earlier generating stage receives
`null`. A 14B pass uses the host's `WanImageToVideo`; a 5B pass uses
`Wan22ImageToVideoLatent`. Full 5B generation feeds that latent directly to the
sampler. Partial 5B refinement samples from the VAE encoding of the conformed
decoded input, so after the host switches to that path the session removes only
the newly created, consumerless 5B latent preparation node and its
otherwise-unused upstream nodes. This pruning also runs when the host builder
throws. If pruning then fails, the original host failure remains authoritative;
without a host failure, a pruning failure is surfaced.

The removed host swap path reused the high-noise sampler's unfinished latent
directly for the low-noise sampler. It avoided a VAE decode/re-encode boundary,
so it could be cheaper and avoid possible VAE reconstruction loss. Ordinary
authored stages deliberately use the product's normal decoded contract: one
stage decodes its result, and a later partial-control stage re-encodes that
decoded video before sampling. This adds work and may add small VAE loss, but
it gives each visible stage correct ownership of its model, prompts, LoRAs,
steps, CFG, sampler, scheduler, and intermediate output. Any future direct
latent reuse must be a benchmarked, architecture-neutral optimization based on
compatible adjacent-stage facts, with decoded handoff as the safe fallback.

For every LTX, MiniMax, WAN, and generic-host generating pass,
`StageModelLoadScope` projects the matching prompt-section rows, then appends
the compiled persisted rows. WAN's model-only projection omits prompt rows whose
model weight is zero while retaining the stored text-encoder weight on every
nonzero-model row. That prompt-before-persisted order is deterministic.
The model-load scope is absent for passthrough stages and restores the original
four host LoRA parameter lists in reverse nesting order on success or failure.
Before model construction, the scope evicts the stage
`modelloader_{model}_image2video` cache marker even when the compiled list is
empty, because that marker does not encode scoped LoRA state; existing live
graph nodes are not pruned. A loader tuple built under nonempty planned LoRAs
is transient. A tuple built under a nonempty prompt scope is transient too:
the scope removes its marker before either parameter snapshot is restored,
including when construction or normalization fails. An unscoped
stage may keep its durable tuple. Marker eviction never removes live graph
nodes.

For ordinary supported WAN models, SwarmUI's generic LoRA loader
targets the model only (`LorasTargetTextEnc=false`). VideoStages uses that
existing generic path for both persisted and prompt-section rows; text-encoder
weights remain round-trippable host parameter data but do not make a model-zero
WAN row effectful. VideoStages does not claim to solve core's automatic
5B-LoRA classifier TODO.

VACE, transition expansion, arbitrary middle-frame references, and audio remain
outside the WAN contract. Ordinary WAN 2.1/2.2 video
models are accepted from host facts and can use text, first-image, or source
entry as request inputs. Legacy swap controls are warned and ignored; two noise
models are two authored stages.

The shared runner publishes authored intermediates and removes every host
per-pass trim. For a terminal single-clip session it applies the global trim
after the final stage; for a multi-clip timeline, common assembly applies that
trim once over the joined timeline. It returns the final decoded video-only
artifact. A new generated hard-cut clip resets to the captured root rather than
consuming the previous clip. LTX, MiniMax, and WAN boundaries are neutral hard
cuts; no family assembler crosses the architecture boundary.

### B6d. Generic host-video runtime execution

The same `StockHostVideoGenerationSession`, without the WAN collaborator, calls
the proven stock `WorkflowGenerator.CreateImageToVideo` branch that SwarmUI uses
for an image-entry model. A later stage receives the immediately previous
decoded video, optionally pixel-resizes it, and lets the host encode and refine
it with the stage's model, prompt, ordinary LoRAs, steps, CFG, sampler,
scheduler, and Control start step. This root image is the ordinary host
image-to-video entry; it is not a claim that the generic profile supports
clip-authored frame references.

Text entry prepares the selected host model and conditioning, then calls the
host's family-specific `EmptyImage` video-latent primitive before sampling and
decoding. `HostVideoRootMediaHandoff`, `HostVideoDecodedStageInput`, and
`VideoStageRunner` contain the root restoration, decoded-media boundary,
and stage-loop mechanics shared with WAN. Generic host video retains direct
stock-path scheduling and request isolation. Generic passes clear ambient
audio, native audio-reference input, swap, and end-frame values inside
reversible scopes. A core-pass pre-handler also neutralizes request-global swap,
end-frame, and creativity-derived start-step state on the discarded host pass
only; authored stage sections are not intercepted.

### B7. Return neutral artifacts and publish

`DecodedClipArtifact` is the architecture-neutral handoff. It contains decoded
video, optional decoded audio, literal dimensions/FPS/frames, and
architecture/clip provenance. It cannot carry a latent, VAE, model
compatibility, or architecture payload.

`VideoArchitectureExecutionHost` verifies returned identity and calls `ValidateDecoded`.
`TimelineAssemblySession` then:

- routes supported crossfades through `DecodedCrossfadeAssembler`;
- joins architecture runs with neutral hard cuts;
- assembles decoded audio;
- installs the final decoded media.

`VideoArchitectureExecutionHost` clears model compatibility from final media and
publishes through `RootRuntimeSession.PublishTimeline` / `OutputPublisher`.
Publication ends the timeline; no architecture finalization step follows it.

### Flow B failures

| Failure | Stopped by |
|---|---|
| Malformed JSON or wrong schema | `VideoStagesJsonReader` |
| Unknown model/profile, mixed clip architecture, forged identity | `ArchitecturePlanResolver` diagnostics |
| Unsupported entry mode or feature | `ArchitectureCapabilityValidator` diagnostics |
| Invalid LTX option | LTX clip/plan compiler diagnostics |
| Invalid Wan option or unsupported host video parameter | Wan compiler / `WanExecutionAdapter.PreflightRequest` |
| Invalid common geometry, boundary, or audio plan | Common compiler diagnostics |
| Missing IC-LoRA dependencies | `Ltx2RequestPreflight` before later VideoStages mutation |
| Missing or corrupt Wan root handoff | `HostVideoRootMediaHandoff` with complete key cleanup |
| Missing provider/session | `VideoArchitectureExecutionHost` |
| Wrong returned identity or decoded media | Execution-host identity checks / `DecodedClipArtifact.ValidateDecoded` |
| Invalid cross-architecture non-cut run | `MultiClipParallelMerger` |
| Unpublishable final media | `RootRuntimeSession` / `OutputPublisher` |

## Invariants

1. Exact backend model recognition remains authoritative.
2. The frontend receives capabilities; it does not infer execution
   architecture from model names.
3. The persisted schema remains typed and versioned.
4. Planning, validation, and request preflight happen before VideoStages graph
   mutation.
5. Common orchestration never interprets architecture graph instructions.
6. Runtime dispatch uses `ArchitectureId`, not scattered model-name tests.
7. Common cross-stage/clip handoffs use neutral artifacts, not architecture
   payloads.
8. Mixed-architecture boundaries are explicit; hard cut is the safe initial
   policy.
9. Dispatch identity assertions and decoded-output validation remain enabled
    in production.
10. Timeline edits remain commands/diffs with undo semantics.
11. Source-only and generated execution follow the same ownership rules.

The frontend has no generic architecture behavior registry. Explicit
architecture-ID guards select LTX-local behavior and its editor only after
catalog-backed capability views authorize the feature. Add another explicit
branch for a second concrete bespoke UI; extract a common contract only when
two implementations demonstrate one.
