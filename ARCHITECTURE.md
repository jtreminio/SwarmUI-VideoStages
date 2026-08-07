# VideoStages backend architecture

VideoStages owns one architecture-neutral timeline. A generated clip owns one
video architecture; all authored stages in that clip, including skipped
stages, must resolve to the same architecture. Different executable clips may
use different architectures.

Production registers source-only `none`, specialized LTX Video 2.3, MiniMax H3,
the WAN family, and a cut-only generic fallback for any other model SwarmUI's
compatibility classes report as video. The registry, planning contracts,
runtime dispatch, and timeline assembly host all five architectures through one
top-level execution path.

## The execution model

Every run follows the same simple path:

1. Parse one versioned timeline document.
2. Resolve every authored stage model to an architecture and model profile.
3. Compile each executable clip through its owning architecture module.
4. Execute clips through per-architecture runtime sessions.
5. Return one decoded, neutral video/audio artifact per clip.
6. Assemble same-architecture non-cut runs, hard-cut those runs together, and
   apply the host's global frame trim.
7. Publish one final timeline through the captured host save contract.

The apparent entry points are inputs to this path, not separate executors.
`ArchitectureEntryMode` names them, and each architecture publishes the ones
it supports:

- text-to-video;
- host image or user init image to video;
- source video followed by zero, one, or several generation stages;
- init-video-only clips; and
- the Refine Video media button, which authors an existing video onto clip 0 as
  its source and passes through the stages that already produced it.

Single-clip/multi-clip and single-stage/multi-stage combinations use the same
coordinator. Options decorate a clip or stage; they do not create parallel
execution engines. A stage with Control 0, no retake window, and no latent
scaling is a passthrough: it carries media forward without sampling,
encoding, or decoding, and is still an ordinary stage in the plan.

### Host phases

VideoStages registers eight priority-ordered host workflow steps. The first
compiles and prepares the request without VideoStages graph mutation. Six named
context lifecycle methods then capture ControlNet preprocessors and references, preserve
and restore the host root, and size the root audio mask. The last step runs the
configured stages. Mutation requires prepared state, so nothing runs against an
invalid or partially preflighted document.

Architecture-specific capture is scoped either to the single root-owning
architecture or to every active architecture. `ArchitectureRootOwnerResolver` picks the root owner: the
first staged clip whose `ClipPlan.EntryMode` is not init-video. `EntryMode` is
the sole compiled clip-entry decision; init-video clips also carry their source
plan. Init-video clips consume their own media and never claim the host root. The
ControlNet preprocessor capture is architecture-neutral and therefore runs for
every active architecture, including the source-only provider, guarded so a
mixed timeline captures once.

See [`docs/STAGE_RUNTIME.md`](docs/STAGE_RUNTIME.md) for exact priorities,
state transitions, object lifetimes, stage loops, and failure behavior.

### Document carrier

The authoring document rides one hidden `Video Stages` string param, gated by a
hidden `Video Stages Enabled` toggle. Per-clip and
per-stage prompt overrides ride in the prompt itself as
`<videoclip[clip,stage]>` sections through a registered `PromptRegion` custom
prefix. Saved metadata
strips embedded upload blobs and restores the `videoclip` tags as the user typed
them.

## The authoring document contract

The timeline rides one hidden `Video Stages` param holding a single JSON
object: a `schemaVersion` (currently 7), optional root `width`/`height`,
`clips`, and `audioTracks`. Version 7 is exact. One bounded version-6 migration
renames the clip `refs` and stage `refStrengths` fields to `frameRefs` and
`frameRefStrengths`; no other version is accepted. Keys are camelCase end to end — the backend readers name
exactly the keys
`frontend/persistence/documentCodec.ts` emits, and nothing relies on lenient key
matching. `Tests/fixtures/authoring-document.json` is the shared pin: the jest
half asserts the codec emits exactly that payload, and the xUnit half parses it
while recording every key lookup through the reader's single `Read()` funnel, so
a reader whose key the frontend never emits (or a frontend key nothing reads)
fails the suite instead of silently dropping data. The two deliberate exception
lists are path-qualified and each entry carries its reason.

## Identity and lock

`ArchitectureId` identifies a video family such as `ltx2` or `none`.
`ModelProfileId` identifies a more specific model generation such as
`ltx-2.3`.

The backend-resolved first authored stage establishes the executable clip
architecture and clip model profile. Every later authored stage is validated
against that architecture, even when a stage is skipped. Later stages may
resolve to different profiles inside the same architecture; each stage keeps
its own resolved profile. Persisted `architectureHint` and model-profile hints
exist only to explain or repair a document whose model can no longer resolve.
They never enable features or authorize execution. A init-video clip with no
active generation stages executes through the neutral `none` runtime, while
its dormant authored stage chain is still checked for architecture and
per-stage profile consistency.

Unknown models, unknown profiles, mixed stage architectures, unsupported
options, and invalid joins produce blocking diagnostics before the extension
mutates the workflow graph.

`PlanDiagnostic` is the one diagnostic record every planner emits: a severity,
a stable machine code, a user-facing message, and whichever of clip, stage, raw
stage index, audio track, or span it knows about. `PlanDiagnosticReporter` is
the only place that decides what a diagnostic does — errors block,
warnings reach the host warning channel, info goes to the debug log, and
duplicate lines collapse. The prepared execution context is the single mutation
gate and retains the first lifecycle failure.

## Architecture module boundary

Common code knows:

- architecture and profile identifiers;
- the authored document schema, including the shape of options an architecture
  interprets — retake windows, prompt-relay windows, IC-LoRA entries, upscale
  method names, audio sources, and timeline audio tracks;
- generic clip ordering, timing, source, and boundary plans;
- capability and rule decisions;
- architecture-owned clip and stage payloads; common code reads the required
  stage execution core while graph-specific additions remain opaque;
- runtime-session, host-phase, and timeline-lifecycle contracts;
- resolved audio runtime sources and root execution policy; and
- decoded clip artifacts.

Common code knows the *shape* of those authored options and nothing about their
meaning: interpretation, validation beyond capability gating, and graph
construction all belong to the module.

An architecture module owns:

- model recognition and profile resolution;
- its catalog descriptor and boundary rules;
- validation and compilation of architecture-specific stage options;
- request preflight of its own dependencies, and creation and preparation of its
  runtime session;
- latent/VAE/conditioning/stage transitions;
- non-cut joins it supports;
- decoding its final clip.

Family implementations live under `VideoStages.Architectures.Ltx2` and
`VideoStages.Architectures.Wan`. Common planning, coordination, and assembly
must not instantiate their managers, inspect their compatibility IDs, or create
their nodes.

`VideoArchitectureManifest` is the production composition root. Each
registration supplies its module, request-scoped runtime provider, host
integration, API routes, and dependency registration together. The backend
maintains no parallel architecture list, and the frontend consumes the
serialized backend catalog without a capability-definition mirror. Explicit
local architecture-ID guards may select concrete behavior or DOM panels, but
they do not recognize models or declare profiles, labels, capabilities, or
rules.

WAN recognizes ordinary WAN 2.1 and 2.2 video models from SwarmUI's
authoritative class, compatibility, and entry facts. VACE, LoRA, and VAE
components are excluded. The exact 14B and 5B identifiers remain
compatibility aliases for their existing special behavior; they are not a
model allowlist. One clip may chain full, partial, and decoded passthrough
stages when every authored stage has the same compatibility class, even when
their legacy profile aliases differ. Hard-cut clips may select different WAN
compatibility classes. The session preserves decoded provenance and returns the
same neutral artifact contract as LTX. Persisted and prompt-section normal
LoRAs use the host's generic model-only loader, so model-zero rows are omitted
before planning or prompt-scope projection.

SwarmUI's request-global Video Swap Model, percent, and swap-section overrides
are legacy metadata in a WAN VideoStages request. Planning emits one warning,
the authored values remain untouched, and an idempotent host pre-handler
prevents SwarmUI from appending an unauthored second sampling pass. High- and
low-noise models are represented as ordinary user-authored stages. A
request-global end-frame remains accepted only for exactly one pure generated
clip whose selected SwarmUI path supports a final image. The compiled plan
assigns it structurally to the last non-passthrough stage; earlier generating
stages receive no end-frame and a trailing passthrough does not take ownership.
Other request shapes warn and continue without the last image. VACE, transition
expansion, arbitrary middle-frame references, and audio remain outside the WAN
contract.

MiniMax H3 samples video and audio together in one joint AV latent, so its
session keeps the audio VAE live and carries decoded audio into the clip
artifact. Text, image, init-video, and later refinement stages build the H3
joint latent directly after reusing SwarmUI's model loader, conditioning, and
sampler builders. Refinement must rebuild both halves together:
`WGNodeData.AsSamplingLatent` encodes raw video without audio, so routing a
decoded clip through the stock host builder would hand the transformer a latent
missing its audio half. Init-video installation preserves its source audio for
that joint re-encoding. H3's generated counts use the `17k + 5` frame grid.
An H3 Continue boundary uses the previous decoded video and optional audio tail
as reference conditioning rather than an overlapping latent handle. The prompt
remains unchanged, and the decoded clips are concatenated without overlap removal; the
graph details live in [ARCHITECTURE_FLOW.md](docs/ARCHITECTURE_FLOW.md#b6b-minimax-h3-graph-execution).

## Capability catalog

The backend catalog is authoritative. Catalog schema v2 has exactly an
architecture table and a resolved-model table:

- each architecture publishes exactly `id`, `label`, `capabilities`, and
  `boundaryRules`;
- `capabilities` is exactly the three lists `features`, `entryModes`, and
  `audioSourceKinds`; `features` is the flag vocabulary
  `ArchitectureFeatureVocabulary` owns;
- `boundaryRules` is exactly the cut, continue, and crossfade rules;
- each resolved model publishes architecture/profile identity, core model and
  compatibility identities, frame grid and grid origin, its architecture's
  capabilities, and supported frame-reference positions.

There is no clip/stage scoping on the wire, and no flat upscale-mode list. A
resolved model is handed its architecture's descriptor verbatim, so a model
cannot narrow its architecture's capabilities. The wire has no profile table,
architecture/model extras, duplicate entry-mode alias, or separate
output-capability alias. `modelProfileId` remains an opaque resolved runtime
identity, not a frontend authorization table.

A rule decision is exactly support state, a stable code, a user-facing reason,
and typed constraints. The frontend mirrors these decisions for authoring, but
the backend revalidates them before graph mutation.

The typed boundary policy is also the publication source: the evaluator that
accepts or rejects a plan consumes the same policy object serialized into the
catalog, and reads its thresholds back out of the published rule rather than
keeping a second copy. Shared C#/TypeScript contract fixtures
guard the exact wire keys, constraints, resolved-model gates, frame alignment,
crossfade budgeting, IC-LoRA presets, and the IC-LoRA drive contract.

### Clip and stage options

Capabilities say what is allowed; these are what they mean.

- Retake regenerates only a frame window of the base video. It requires a
  init-video clip and is mutually exclusive with frame references.
- Prompt relay tiles a window list across the clip and requires a fixed frame
  count, so it cannot combine with audio-owned or ControlNet-owned length.
- Upscale has four modes selected by the authored method-name prefix: `pixel-`,
  `model-`, `latent-`, and `latentmodel-`.
- Source video is conformed before use: load, resample to the timeline fps
  using the file's own runtime rate, slice the used range to the clip's exact
  aligned frame count, and resize to the timeline dimensions. The file's own
  audio track is trimmed to the same range and attached.

## Neutral artifact and joins

An architecture runtime returns a `DecodedClipArtifact` containing only:

- decoded video output identity;
- optional decoded audio output identity;
- literal width, height, FPS, and frame count;
- architecture provenance; and
- clip provenance.

It cannot carry a latent, VAE, model compatibility object, or a nested media
wrapper that carries those values transitively. The single-clip path honours
the same boundary — publication reads the artifact, not ambient host media.

Cross-architecture boundaries are cut-only. A persisted invalid continue or
crossfade keeps its requested value for repair and compiles to an effective
cut. A cross-architecture join, or one the owning architecture marks
unsupported, blocks generation; a join that merely does not apply to its target
— init-video target, target without a stage, target with an explicit first-frame
reference, an unknown mode, or an insufficient frame budget — degrades to a cut
with a warning that now actually reaches the user. Within one architecture, its
boundary rules determine whether continue or crossfade is valid and how the
join window snaps to that architecture's grid. An overlap-mode Continue or
Crossfade may also carry the outgoing audio tail into the next clip, which
requires the target to have a generation stage that can consume it.

An overlap-mode Continue's frozen tail is an explicit boundary artifact, not
an implicit incoming guide, and the two follow different stage rules. The
implicit host image is the opening stage's guide only; every later defaulted
stage refines its incoming latent directly. The continuity tail instead applies
at *every* stage of the target clip that regenerates the clip's opening frames,
because a later stage's denoise would otherwise wash the seam out. It is sliced
and frame-rate-conformed once, at clip level, and kept at the previous clip's
own resolution; each consuming stage conforms it spatially to that stage's
resolution, so the final stage anchors on the previous clip's native frames
rather than on whatever downscale the opening stage needed. The opening stage
takes the tail as its primary guide; a later stage layers it over its own input,
leaving every frame past the overlap window untouched. Stages that do not
regenerate the head are skipped: passthrough stages, retake stages (their
per-frame noise mask owns what regenerates), and any stage authoring its own
first-frame reference.

Assembly is entirely common. `Timeline.Boundaries` re-resolves the planned
joins against what the architectures actually produced, and `Timeline.Merger`
builds every join through the two shared joiners — an architecture contributes
a decoded clip, never a join. Final output metadata never inherits the first
clip's model compatibility.

## Audio ownership

`AudioSourceKind` is the one vocabulary for "where does this audio come from",
and `AudioSource.Parse` is the one reader for authored source strings. Clip
base audio, projected segments, timeline tracks, and architecture capability
declarations all name sources with the same enum, and an unparsable source
blocks rather than falling through to native audio.

Clip-local base-source selection, clip-length ownership, segments, and
stage-latent reuse are separate decisions. `ClipAudioBedDuration` is the single
rule for how long a clip's audio bed is, so a init-video clip at a resampled fps
places segments the same way whether or not it has stages. Architecture-specific
conditioning media, such as IC-LoRA drive audio, stays outside the base track
and timeline mix. Architecture modules own any model-latent audio behavior.

For LTX multi-stage clips, separated audio latents remain architecture-owned
and flow directly between stages. They are not decoded to audio and encoded
again merely to construct the next stage.

LTX IC-LoRAs carry explicit, preset-independent drive intent. `DriveSource`
chooses an authored upload or Incoming media already available at that
generation point; `DriveData` chooses Visual, Audio, or None (model-only);
`DriveMediaKinds` narrows which containers (image, video, or audio) may supply
that stream. Curated presets seed these fields in the frontend, but backend
planning and runtime dispatch use the persisted typed contract rather than
matching preset or model names.

### Timeline audio tracks

Timeline audio is authored at the document root, not on a clip: a track owns a
source and one or more spans. The parser flattens those spans into independent
`TimelineAudioSpanSpec` values.

`TimelineAudioSpanCompiler` partitions each segment across the final
trimmed clip windows and appends the resulting items to each clip's
`AudioPlan.Spans`. Source time advances with the final timeline, so continued
and crossfaded seams do not cause drift. Overlapping segments stay independent
and mix additively. If any clip timing is unknown, a segment produces no clip
items, so it is never partially mixed. Clip audio-segment capability is validated
after projection, because before it there is nothing clip-scoped to validate.

## Runtime invariants

- A plan is fully architecture-resolved before any extension graph mutation, and
  every architecture's dependencies are preflighted in the same window. The
  first registered workflow phase is request preflight; a request that cannot
  execute is rejected while the host graph, current media, and node helpers are
  still untouched.
- Clip ids are unique within a plan.
- One runtime session handles only its declared architecture.
- A session result must match the requested clip and architecture.
- Every clip returns a decoded video artifact with positive literal metadata.
- Multi-clip assembly receives exactly one valid artifact per planned clip.
- Source installation, execution, assembly, and publication fail closed. A
  trim that cannot be applied and an upload blob the sanitizer cannot strip are
  errors, not warnings.
- At most one architecture owns the host root.
- `RootRuntimeSession` is the only writer of the captured host save set. Nothing
  else may re-target it.
- `VideoGraphHelpers` provides common `NodeHelpers` accessors/codecs and is the
  sole owner of invalidation caused by graph-node removal. It recognizes the
  extension's bare-id, JSON `[nodeId, slot]`, and pipe-marker encodings plus
  SwarmUI's six-part model/CLIP/VAE loader tuple. Architecture-scoped snapshots
  and richer marker codecs stay beside their consumers, but every graph-removal
  path routes removed ids through `InvalidateForRemovedNodes` (normally through
  `RemoveNode` or `WorkflowGraphCleanup`), so no removed node leaves a
  live-looking cache entry behind.
- `StableNodeIds` is the one allocation map for stable dynamic node ids; each
  allocator owns a declared block and a slot outside it throws.
- Embedded upload blobs are stripped from saved metadata by walking
  `UploadContainers.AllPaths` from the document root, so a new upload field
  cannot be missed.
- Intermediate artifacts and architecture compatibility never leak into the
  final mixed timeline.

## ComfyUI node surface

The extension ships its own ComfyUI package and registers its folder as a
custom node path at init. It provides Swarm Audio Length To Frames, Swarm Frame
Window, Swarm Prompt Relay Encode, Swarm Ramp Mask Batch, and Swarm Set Audio
Mask Windows. Typed C# bindings are
generated into `src/Generated/`. The package root imports the ComfyUI-facing
module lazily so the pure helpers stay importable and testable without ComfyUI.
The generation-prune and runtime-registration retention audit is recorded in
[`docs/STAGE_RUNTIME.md`](docs/STAGE_RUNTIME.md#9-generated-binding-retention-audit).

## Adding another architecture

Adding another family should require:

1. a manifest registration supplying module, runtime provider, host handlers,
   API routes, and dependency registration together;
2. a model resolver that returns stable architecture/profile identity, core
   model facts, frame grid and grid origin, and complete effective capabilities
   for every claimed model;
3. descriptor capabilities and rules, including a rule for every boundary
   mode — the registry rejects an incomplete catalog at construction;
4. an architecture-owned clip compiler and typed payload whose stage payload
   exposes the required common execution core;
5. a runtime provider that answers request preflight for its dependencies and
   creates one timeline session;
6. a boundary rule declaring which joins it supports; the common merger builds
   them;
7. an explicit frontend owner guard only when the family has concrete custom UI
   behavior; architectures with no custom frontend behavior need no frontend
   registration; and
8. contract tests using the common dispatcher, strict catalog parser, and
   `Timeline.Merger`.

It must not require changes to generic document parsing, clip ordering,
history, cut assembly, output publication, or the frontend's generic panel
routing. Two shared extension points do exist by design: a genuinely new typed
rule constraint must be added to catalog serialization, and a genuinely new
audio source kind must be added to `AudioSourceKind` and its parser.

The fake architectures used by tests are deliberately not production
registrations. They prove different descriptor and resolved-model capability
sets, model facts, boundary policies and budgets, runtime sessions, and
mixed-timeline cut assembly without shipping another production family.

## Deliberate non-goals

Three things look like inconsistencies and are not. Each was considered and
rejected on the merits.

- **LTX vocabulary stays in the generic capability enums.** `PromptRelay`,
  `Retake`, `IcLora`, and the four upscale modes are a published
  cross-language contract. Hiding the spec's LTX-shaped fields behind an opaque
  payload would cost the catalog its typed wire and buy nothing: the meaning of
  each is already module-owned.
- **The timeline audio track has no per-clip capability gate.** Every
  `ArchitectureFeature` is clip-scoped and a document-level entity has no owning
  clip. The backend validates the projected segments after projection, which is
  the first point at which a clip owns them.
- **`frontend/renderUtils.ts` keeps its own frame-alignment constant.**
  Publishing it would change the wire contract for a display-only rounding
  helper.

## Gates

`dotnet test SwarmUI-VideoStages.Tests.sln` is the backend gate; `npm run build`
runs Biome, TypeScript, jest, and the esbuild bundle for the frontend. Both were
green when this document was last updated. Counts are deliberately not recorded
here — they go stale within days and the gates do not.
