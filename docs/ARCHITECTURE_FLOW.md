# VideoStages architecture flow

This is the start-here map for two end-to-end paths:

1. selected model → architecture identity → visible frontend features;
2. generation request → planned architecture → model-specific graph → neutral
   timeline output.

For the detailed execution and frontend designs, continue to
[`ARCHITECTURE.md`](../ARCHITECTURE.md) and
[`FRONTEND_ARCHITECTURE.md`](../FRONTEND_ARCHITECTURE.md).

VideoStages is a closed-world modular monolith. Production registers the
source-only `none` architecture, LTX Video 2.3 (`ltx2`), and the cut-only Wan
2.2 Image2Video 14B and Text/Image2Video 5B profiles
(`wan22`).

## Ownership

| Concern | Owner | Concrete entry points |
|---|---|---|
| Production registration | SwarmUI adapter | `VideoStagesExtension.OnInit`, `VideoArchitectureManifest` |
| Exact model recognition | Backend architecture module | `VideoArchitectureRegistry.TryResolveModel`, `Ltx2ArchitectureModule.TryResolveModel`, `WanArchitectureModule.TryResolveModel` |
| Capabilities and rules | Backend architecture module | `Ltx2ArchitectureModule.Descriptor`, `WanArchitectureModule.Descriptor`, `NoneArchitecture.Descriptor` |
| Catalog transport | Common backend + SwarmUI authorization | `VideoStagesApi.VideoStagesGetArchitectureCatalog`, `AuthorizedArchitectureRegistry`, `ArchitectureCatalogSerializer.Serialize` |
| Catalog loading and feature policy | Common frontend | `getArchitectureCatalogSnapshot`, `loadAuthoritativeArchitectureCatalog`, `refreshAuthoritativeArchitectureCatalog`, `parseVideoArchitectureCatalog`, `createCapabilityViewResolver` |
| Architecture-specific authoring behavior | Frontend local behavior maps | `architectureBehavior`, `ltx2Behavior`, `authoringPanels.ts`, architecture ID identity modules |
| Curated IC-LoRA download route | LTX backend adapter + SwarmUI core | `Ltx2ApiRoutes`, `ModelsAPI.DoModelDownloadWS` |
| Document parsing and product planning | Common backend | `VideoStagesSpecParser`, `ArchitecturePlanResolver`, `VideoExecutionPlanCompiler` |
| Model-family planning and execution | Selected backend module | `IVideoArchitectureModule.ValidateAndCompileClip`, `IVideoGenerationSession` |
| Runtime dispatch and timeline assembly | Common backend | `StageSequenceRunner`, `ArchitectureRuntimeDispatcher`, `TimelineAssemblySession` |
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
results, ambiguous model matches, and invalid default profiles. API projection
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
`WanArchitectureModule.TryResolveModel` accepts ordinary WAN 2.1/2.2
image-entry models when SwarmUI reports a WAN image-to-video compatibility
class and authoritative entry abilities. It explicitly rejects text-only,
VACE, LoRA, and VAE component classes. Two exact legacy pairs retain special
profile aliases:

- `wan-2_2-image2video-14b` / `wan-21-14b` resolves to `wan22` /
  `wan-2.2-i2v-14b`; and
- `wan-2_2-ti2v-5b` / `wan-22-5b` resolves to `wan22` /
  `wan-2.2-ti2v-5b`.

The exact identifiers are compatibility aliases rather than the recognition
allowlist. Other ordinary WAN image-entry models resolve to the generic
`wan-i2v` profile; first/last-frame and native 5B behavior are not inferred for
that alias. The 14B profile remains the descriptor default.
`NoneArchitectureModule.TryResolveModel` always returns false; common planning
assigns `none` only to source-video clips with no active stages.

Backend recognition is the execution authority. A persisted architecture ID
or frontend classification cannot authorize an unsupported model.

### A2. Capability declaration and transport

`Ltx2ArchitectureModule.Descriptor`, `WanArchitectureModule.Descriptor`, and
`NoneArchitecture.Descriptor` are typed `VideoArchitectureDescriptor` values.
Entry modes are owned by each model profile. The descriptor's entry-mode list
is only the distinct catalog projection used for architecture overviews; model
selection, diagnostics, conversion, planning, and runtime authorization all
resolve the exact selected profile. The WAN 14B profile publishes
`ImageToVideo` and `SourceVideo`; the 5B profile additionally publishes
`TextToVideo`.

Wan publishes same-profile multi-stage chaining, video-only output, a
four-frame profile grid, and cut-only boundaries. Both profiles publish
ordinary persisted clip/stage and prompt-section LoRAs. Image-generated stage
0 uses the host root at full control. Native 5B text stage 0 uses an empty
latent and does not decode or reinterpret the host's donor image.
Sourced stage 0 uses its conformed source at finite control in `[0, 1]`; each
later stage uses `PreviousStage` with the same bound. Exact control `0` is a
samplerless decoded-video passthrough for those two decoded inputs, while
positive partial control still must quantize to a nonzero start step.
Refine-video and audio capabilities remain absent. A request-global refine
source cannot coexist with a clip-local sourced Wan timeline. The same typed
boundary/rule objects feed backend validation and frontend publication.

`ArchitectureCatalogSerializer.Serialize` projects the descriptor catalog and
the currently resolved, session-authorized host models to:

```text
architectures[] = descriptor + capabilities + profiles + rules
models[]        = modelName + architectureId + modelProfileId
```

`VideoStagesApi.VideoStagesGetArchitectureCatalog` exposes that projection as
the `VideoStagesGetArchitectureCatalog` API call.

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
`setArchitectureCatalogRequestListener` reports the moment a request starts, so
the timeline paints the `loading`/`refreshing` transition it is actually in —
including the pending refresh's later start — instead of painting ahead of it.

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

The host param-refresh hook uses forced refresh, so newly installed models can
appear without a page reload and without a temporary loss of authority.
`buildArchitectureModelCatalog` uses backend DTO identity only: it may decorate
backend-known models with current host dropdown labels and keeps backend-only
models, but a host model absent from the backend catalog has null
architecture/profile identity. Frontend identity modules contain only stable
IDs used to select local LTX behavior and DOM panels; they declare no
capabilities and perform no model recognition.

### A4. Selection, identity, and feature visibility

`getRootDefaults` builds `RootDefaults.modelCatalog` from the current SwarmUI
model dropdown and the authoritative backend catalog. Without that catalog the
model catalog contains no capability or model-identity authority.
`appendStageModelSection` uses it to build model options:

- stage 0 may select a model whose exact profile supports the clip's entry mode;
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
through the local `ArchitectureBehavior` map. Only the `ltx2` ID selects
`ltx2Behavior` today, and the interface is mostly IC-LoRA-shaped. LTX DOM
rendering is keyed directly by the same ID in `authoringPanels.ts`. These maps
own implementation behavior only; labels, profiles, capabilities, and rules
remain backend DTO data. Wan needs no custom frontend behavior, so reassess this
abstraction only when another architecture presents a concrete bespoke-UI need.

### A5. Opaque architecture-owned authoring payloads

Each persisted clip has an `architecturePayload` envelope containing either
`null` or an opaque JSON object. Common normalization and persistence preserve
that object structurally without projecting, defaulting, or interpreting its
nested fields. Unknown architecture IDs therefore retain future architecture
data through decode, normalization, save, and reload even when common code does
not understand that data.

In Stage 0 the envelope is preservation-only: it is frontend state, no adapter
parses it, and the backend never reads it (`documentCodec.ts` emits it,
`AuthoringDocumentContractTests` classifies it as backend-unread). Typed parsing
would belong to the adapter named by the clip's `architecture` ID, and giving it
one means surfacing raw JSON on `ClipSpec` first. Do not confuse this envelope
with the backend's `IArchitectureClipPayload`, which is a runtime compilation
produced from the already-parsed common `ClipSpec` and shares only the name.

Because the envelope's owner is the clip's architecture, converting a clip to a
different architecture clears it (`planArchitectureConversion`), and the
whole-document save path refuses a state that changes owner while keeping one.

`documentCodec` writes the key for every clip, including `null`, so resaving an
older document adds it without a schema-version change: an added optional key
that decodes to the same document is deliberately not a version bump.

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
    → VideoExecutionPlanCompiler
    → VideoExecutionPlanContext
```

`VideoStagesJsonReader` requires schema version 5.
`VideoStagesSpecParser` applies prompt overrides and parses clips, authored
stages, source media, dimensions, FPS, and timeline audio into
`VideoStagesSpec`.

### B2. Select `ArchitectureId` and compile opaque payloads

`ArchitecturePlanResolver.ResolveAuthoredStages` resolves every authored stage
model, including skipped stages, through the same session-authorized backend
registry used for catalog projection. A forbidden model is unresolved and
blocks planning before graph mutation. For a generated
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
`NormalLoraPlanCompiler` is common graph-free planning shared by LTX and Wan:
it resolves each stage's effective clip rows, keeps clip-before-stage ordering,
and leaves the resulting immutable array inside the selected architecture's
stage payload rather than common `StagePlan`. Its default model-and-text target
policy preserves LTX/generic text-encoder-only rows. WAN explicitly selects the
model-only policy at this seam.
For Wan, `WanClipPlanCompiler.Compile` produces the smaller `WanClipPayload`
and `WanStagePayload`; both preserve canonical profile identity. It accepts
only the two supported `wan22` profiles and requires one profile throughout a
clip. A hard cut starts a new clip and may select the other profile. The
compiler also enforces the generated-root / source-video / previous-stage
chain, refuses an effective LoRA plan on a samplerless passthrough, and refuses
unsupported or empty integer schedules that the common capability validator
cannot yet see. A clip-LoRA weight of zero is the supported per-stage disable
path. Direct/default clip and stage rows whose model and text-encoder weights
are both zero are omitted by the default policy. Under WAN's model-only policy,
every model-zero row is omitted even when its stored text-encoder weight is
nonzero; the samplerless-stage rule therefore sees the same effective plan the
WAN runtime can apply.

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
`WanExecutionAdapter.PreflightRequest` similarly refuses host-only options the
slice cannot honor. Legacy request-global video-swap values are not preflight
errors: effective-request projection emits one warning and
`WanLegacySwapIsolation` clears them only from host generation info, without
editing `T2IParamInput`. High- and low-noise work is expressed as ordinary
authored stages. Global end-frame is limited to exactly one pure generated 14B
ImageToVideo clip. Its immutable stage payload assigns sole ownership to the
last non-passthrough stage, so earlier generators receive no final-frame input
and trailing passthrough does not consume it. Multi-clip, mixed-family,
sourced, refine, text, active or forged 5B/cross-profile, and missing or forged
ownership contracts refuse the option before mutation. Global creativity
remains refused in favor of the authored clip-local controls.

“Before mutation” here means before **VideoStages** mutation. SwarmUI may
already have built host graph state that VideoStages captures or replaces.

### B4. Host phases prepare selected architecture state

Later `Runner` phases dispatch through
`VideoArchitectureExecutionHost.DispatchHostPhase`.
`ArchitectureHostPhasePolicy` chooses all-active versus root-owner-only scope;
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
When Wan owns the image-to-video root, `WanRootMediaHandoff` captures the
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
same-architecture boundary, while separately exposing the previous timeline
output as contextual media across cuts and architecture changes.

`ArchitectureRuntimeDispatcher.ResolveSession` selects a session solely from
`clip.Architecture.Id`, passes the narrow per-clip context directly to that
session, and validates the returned artifact's architecture identity, clip
identity, and decoded-media shape. It does not repeat model-name checks.

Timeline state such as the plan, prepared audio, assembly session, and root
policy is captured when each architecture session is created. LTX composes the
per-clip context with its private root and host state in
`StageClipExecutionContext`; the sourced-only session captures only frame rate
and audio sources.

### B6a. LTX graph execution

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

### B6b. Wan direct runtime execution

`WanGenerationSessionFactory` snapshots the host root media and VAE.
`WanGenerationSession` prepares each hard-cut clip independently, then loops
its compiled stages. Generated stage 0 resets to the captured root and
delegates that image to SwarmUI's
`WorkflowGenerator.CreateImageToVideo`. Native 5B text stage 0 prepares the
authored model and prompt conditioning through the host loader, constructs
`Wan22ImageToVideoLatent` without `start_image`, samples it with the authored
steps, CFG, sampler, scheduler, seed, dimensions, and frame count, and decodes
the result with the prepared VAE. An authored clip duration wins; otherwise
text stage 0 uses the host text-to-video frame setting (default 81), and later
stages inherit the preceding decoded frame count. All counts are snapped to
the selected profile's frame grid. A sourced clip instead uses
`SourcedClipInstaller` to resample, window, and resize its exact clip-local
footage to WAN's snapped dimensions and requests video-only installation, so
the source-audio trim branch is never built. Exact control `0` preserves that
decoded source, or the immediately preceding decoded stage, without opening a
host model section or constructing conditioning, latent, or sampler nodes.
Eligible passthrough intermediates are still published. Full control
conditions from source frame 0 without VAE-encoding the source batch; positive
partial control conditions from frame 0 and VAE-encodes a distinct full
conformed-batch selector. Each later stage uses the same passthrough/full/
partial rules over the preceding decoded batch. The session validates the
immutable clip, entry, source, stage input, payload, and canonical per-clip
profile contract before graph mutation. For the one permitted request-global
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

For every generating pass, `PromptParser.ApplyLoraScope` first projects the
matching bare, clip, and stage prompt-section rows into host
`SectionID_Video`; nested inside it, `LoraParams.ApplyNormalLoras` appends the
compiled persisted rows. WAN's model-only projection omits prompt rows whose
model weight is zero while retaining the stored text-encoder weight on every
nonzero-model row. That prompt-before-persisted order is deterministic.
Both scopes are absent for passthrough stages and restore the original four
host LoRA parameter lists in reverse nesting order on success or failure.
Before the host builder runs, Wan evicts the stage
`modelloader_{model}_image2video` cache marker even when the compiled list is
empty, because that marker does not encode scoped LoRA state; existing live
graph nodes are not pruned. A loader tuple built under nonempty planned LoRAs
is transient. A tuple built under a nonempty prompt scope is transient too:
Wan removes its marker in a `finally` before either parameter snapshot is
restored, including when construction or normalization fails. An unscoped
stage may keep its durable tuple. Marker eviction never removes live graph
nodes.

For ordinary supported WAN image-entry families, SwarmUI's generic LoRA loader
targets the model only (`LorasTargetTextEnc=false`). VideoStages uses that
existing generic path for both persisted and prompt-section rows; text-encoder
weights remain round-trippable host parameter data but do not make a model-zero
WAN row effectful. VideoStages does not claim to solve core's automatic
5B-LoRA classifier TODO.

VACE, text-only 14B entry, transition expansion, advanced references, audio,
refine-source, and HDR remain outside the WAN contract. Ordinary WAN 2.1/2.2
image-entry variants are accepted from host facts. Legacy swap controls are
warned and ignored; two noise models are two authored stages.

The session publishes authored intermediates and
removes every host per-pass trim. For a terminal single-clip session it applies
the global trim after the final stage; for a multi-clip timeline, common
assembly applies that trim once over the joined timeline. The session returns
the final decoded video-only artifact. A new generated hard-cut clip resets to
the captured root rather than consuming the previous clip. LTX/Wan boundaries
are neutral hard cuts; no family assembler crosses the architecture boundary.

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
| Invalid Wan option or unsupported host video parameter | Wan compiler / `WanExecutionAdapter.PreflightRequest` |
| Invalid common geometry, boundary, or audio plan | Common compiler diagnostics |
| Missing IC-LoRA dependencies | `Ltx2RequestPreflight` before later VideoStages mutation |
| Missing or corrupt Wan root handoff | `WanRootMediaHandoff` with complete key cleanup |
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

The main current transition seam is the IC-LoRA-shaped local behavior
interface. The frontend ID maps are behavior dispatch only, not a second
catalog authority. Do not copy that remaining seam into a new architecture.
