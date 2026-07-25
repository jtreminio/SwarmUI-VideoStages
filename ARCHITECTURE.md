# VideoStages backend architecture

VideoStages owns one architecture-neutral timeline. A generated clip owns one
video architecture; all authored stages in that clip, including skipped
stages, must resolve to the same architecture. Different executable clips may
use different architectures.

Production currently registers source-only `none` and LTX Video 2.3 only. The
registry, planning contracts, runtime dispatch, and timeline assembly are
intentionally capable of hosting more modules without adding architecture
branches to common code.

## The execution model

Every run follows the same simple path:

1. Parse one versioned timeline document.
2. Resolve every authored stage model to an architecture and model profile.
3. Compile each executable clip through its owning architecture module.
4. Execute clips through per-architecture runtime sessions.
5. Return one decoded, neutral video/audio artifact per clip.
6. Assemble same-architecture non-cut runs, hard-cut those runs together, and
   apply the host's global frame trim.
7. Publish one final timeline, then let at most one architecture finalize it.

The apparent entry points are inputs to this path, not separate executors.
`ArchitectureEntryMode` names them, and each architecture publishes the ones
it supports:

- text-to-video;
- host image or user init image to video;
- source video followed by zero, one, or several generation stages;
- source-video-only clips; and
- the separate global Refine Video action, which may also skip the first N
  authored stages.

Single-clip/multi-clip and single-stage/multi-stage combinations use the same
coordinator. Options decorate a clip or stage; they do not create parallel
execution engines. A stage with Control 0, no retake window, and no latent
scaling is a passthrough: it carries media forward without sampling,
encoding, or decoding, and is still an ordinary stage in the plan.

### Host phases

VideoStages registers seven priority-ordered host workflow steps. Six dispatch
an `ArchitectureHostPhase` — ControlNet preprocessor capture, base and refiner
reference capture, pre-core media capture, core output drop, and root audio
mask sizing — and the seventh runs the configured stages. Every step first
requires a fully resolved plan, so nothing runs against an invalid document.

Each phase is scoped either to the single root-owning architecture or to every
active architecture. `ArchitectureRootOwnerResolver` picks the root owner: the
first clip with stages whose input is host root media or an empty latent.
Sourced clips consume their own media and never claim the host root. The
ControlNet preprocessor capture is architecture-neutral and therefore runs for
every active architecture, including the source-only adapter, guarded so a
mixed timeline captures once.

### Document carrier

The authoring document rides one hidden `Video Stages` string param, gated by a
hidden `Video Stages Enabled` toggle, alongside a global `Video Stages Refine
Source Video` image and a `Video Stages Refine Skip Stages` count. Per-clip and
per-stage prompt overrides ride in the prompt itself as
`<videoclip[clip,stage]>` sections through a registered `PromptRegion` custom
prefix. Saved metadata
strips embedded upload blobs and restores the `videoclip` tags as the user typed
them.

## The authoring document contract

The timeline rides one hidden `Video Stages` param holding a single JSON
object: a `schemaVersion` (currently 5, rejected outright when it differs), the
optional root `width`/`height`, `clips`, and `audioTracks`. Keys are camelCase
end to end — the backend readers name exactly the keys
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

The first authored stage establishes the clip architecture and clip model
profile. Every later authored stage is validated against that architecture,
even when a stage is skipped. Later stages may resolve to different profiles
inside the same architecture; each stage keeps its own resolved profile. A
sourced clip with no active generation stages executes through the neutral
`none` runtime; its declared identity is forced to `none`, while its dormant
authored stage chain is still checked for architecture and per-stage profile
consistency.

Unknown models, unknown profiles, mixed stage architectures, unsupported
options, and invalid joins produce blocking diagnostics before the extension
mutates the workflow graph.

`PlanDiagnostic` is the one diagnostic record every planner emits: a severity,
a stable machine code, a user-facing message, and whichever of clip, stage, raw
stage index, audio track, or span it knows about. `PlanDiagnosticReporter` is
the only place that decides what a diagnostic does — errors block,
warnings reach the host warning channel, info goes to the debug log, and
duplicate lines collapse. `RequireVideoExecutionPlanContext` is the single gate
that raises the blocking error, and it runs at the head of every host step.

## Architecture module boundary

Common code knows:

- architecture and profile identifiers;
- the authored document schema, including the shape of options an architecture
  interprets — retake windows, prompt-relay windows, IC-LoRA entries, upscale
  method names, audio sources, and timeline audio tracks;
- generic clip ordering, timing, source, and boundary plans;
- capability and rule decisions;
- opaque architecture-owned clip and stage payloads;
- runtime-session, boundary-assembler, host-phase, and timeline-lifecycle
  contracts;
- resolved audio runtime sources, root execution policy, and the timeline
  assembly session; and
- decoded clip artifacts.

Common code knows the *shape* of those authored options and nothing about their
meaning: interpretation, validation beyond capability gating, and graph
construction all belong to the module.

An architecture module owns:

- model recognition and profile resolution;
- its catalog descriptor and conditional rules;
- validation and compilation of architecture-specific stage options;
- request preflight of its own dependencies, and creation and preparation of its
  runtime session;
- latent/VAE/conditioning/stage transitions;
- non-cut joins it supports;
- decoding its final clip; and
- optional exclusive timeline finalization after publication.

LTX implementations live under `VideoStages.Architectures.Ltx2`. Common
planning, coordination, and assembly must not instantiate LTX managers, inspect
LTX compatibility IDs, or create LTX nodes.

`VideoArchitectureManifest` is the production composition root. Each
registration supplies its module, runtime factory, host integration, API
routes, and dependency registration together. The backend maintains no parallel
architecture list; the frontend keeps one deliberate mirror in its own
architecture registry, and the shared catalog fixtures exist to keep the two
honest.

## Capability catalog

The backend catalog is authoritative. It publishes stable capabilities by
scope:

- architecture: generated entry, sourced entry, entry modes, audio source
  kinds, multi-stage, native audio, decoded output;
- model profile: samplers, schedulers, dimensions, frames, and normal LoRA;
- clip: source video, prompts, relay, references, retakes, audio sources, and
  projected audio segments;
- stage: input modes, each upscale mode (also republished as a flat
  `upscaleModes` list), LoRA, IC-LoRA, HDR, frame references;
- boundary: cut, continue, and crossfade rules; and
- output: decoded video, attached audio, standalone audio.

A rule decision includes support state, a stable code, a user-facing reason,
scope, optional entity identity, and typed constraints. The frontend mirrors
these decisions for authoring, but the backend revalidates them before graph
mutation.

Typed boundary and conditional-rule policies are also the publication source:
the evaluator that accepts or rejects a plan consumes the same policy object
serialized into the catalog, and reads its thresholds back out of the published
rule rather than keeping a second copy. Shared C#/TypeScript contract fixtures
guard the wire keys, constraints, and profile gates, plus frame alignment,
crossfade budgeting, IC-LoRA presets, and the IC-LoRA drive contract.

### Clip and stage options

Capabilities say what is allowed; these are what they mean.

- Retake regenerates only a frame window of the base video. It requires a
  sourced clip or a global Refine Video source and is mutually exclusive with
  frame references.
- Prompt relay tiles a window list across the clip and requires a fixed frame
  count, so it cannot combine with audio-owned or ControlNet-owned length.
- HDR is a typed flag on an IC-LoRA entry, not a name match. Its activation
  must be uniform across the whole timeline, and it is the reason an
  architecture may finalize after publication.
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
— sourced target, target without a stage, target with an explicit first-frame
reference, an unknown mode, or an insufficient frame budget — degrades to a cut
with a warning that now actually reaches the user. Within one architecture, its
boundary rules determine whether continue or crossfade is valid and how the
overlap snaps to that architecture's grid. A non-cut boundary may also carry
the outgoing audio tail into the next clip, which requires the target to have a
generation stage that can consume it.

Timeline assembly partitions clips into maximal runs connected by effective
non-cut boundaries. Each run is assembled by its architecture; the decoded run
outputs are then concatenated with architecture-neutral cuts. Final output
metadata never inherits the first clip's model compatibility.

## Audio ownership

`AudioSourceKind` is the one vocabulary for "where does this audio come from",
and `AudioSourceParser` is the one parser for authored source strings. Clip
base audio, projected segments, timeline tracks, and architecture capability
declarations all name sources with the same enum, and an unparsable source
blocks rather than falling through to native audio.

Clip-local base-source selection, clip-length ownership, segments, and
stage-latent reuse are separate decisions. `ClipAudioBedDuration` is the single
rule for how long a clip's audio bed is, so a sourced clip at a resampled fps
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
that stream; and `Hdr` is a typed flag. Curated presets seed all four fields in
the frontend, but backend planning and runtime dispatch use the persisted typed
contract rather than matching preset or model names.

### Timeline audio tracks

Timeline audio is authored at the document root, not on a clip: a track owns a
source and one or more spans, and `AudioTimelinePlan` represents them
architecture-neutrally. A span may cover one clip, several adjacent clips, a
timeline window, or discontiguous windows.

Root-authored tracks execute. `TimelineAudioSegmentTrackSpecPlanner` compiles
them, `AudioTimelinePlanCompiler` partitions them across the final trimmed clip
windows so audio cannot drift after a continued or crossfaded seam, and
`TimelineAudioSegmentPlanProjector` folds the resolved windows into each clip's
`AudioPlan.Segments` for the architecture's own mixer. Source time advances with
the final timeline, not with authored clip duration. Overlapping tracks stay
independent and mix additively. A span whose timing cannot be resolved produces
no clip window at all, so an unresolved span is never partially mixed. Clip
audio-segment capability is validated after projection, because before it there
is nothing clip-scoped to validate.

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
  plan committed to refine semantics without its source, a trim that cannot be
  applied, and an upload blob the sanitizer cannot strip are all errors, not
  warnings.
- At most one architecture owns the host root, and at most one owns
  whole-timeline finalization.
- `OutputPublisher` is the only writer of the captured host save set. The one
  sanctioned exception is the exclusive `FinalizeTimeline` contract after
  publication: LTX uses it to delete each published animation save and graft in
  the HDR save. Nothing else may re-target that set.
- `VideoGraphHelpers` owns every write, read, removal, and invalidation of the
  `NodeHelpers` node-reference cache, across all three encodings VideoStages
  stores (bare id, JSON `[nodeId, slot]` path, pipe-delimited marker), so a
  removed node cannot leave a live-looking cache entry behind.
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
Window, Swarm Prompt Relay Encode, Swarm Ramp Mask Batch, Swarm Set Audio Mask
Windows, and the legacy Swarm Save HDR Animation WS. Typed C# bindings are
generated into `src/Generated/`. The package root imports the ComfyUI-facing
module lazily so the pure helpers stay importable and testable without ComfyUI.

## Adding another architecture

Adding another family should require:

1. a manifest registration supplying module, runtime provider, host handlers,
   API routes, and dependency registration together;
2. a model resolver and profile descriptors, with at least one profile and a
   declared default that the profile catalog contains;
3. scoped capabilities and rules, including a rule for every boundary mode —
   the registry rejects an incomplete catalog at construction;
4. an architecture-owned clip compiler and opaque payload;
5. a runtime provider that answers request preflight for its dependencies, and a
   runtime-session factory owning preparation and optional exclusive finalization;
6. same-architecture boundary assembly as soon as any non-cut join is declared
   supported or conditional;
7. a matching frontend architecture definition and one entry in
   `frontend/architectures/modules.ts`; and
8. contract tests using the common dispatcher and timeline assembler.

It must not require changes to generic document parsing, clip ordering,
history, cut assembly, output publication, or the frontend's generic panel
routing. Two shared extension points do exist by design: a genuinely new typed
rule constraint must be added to catalog serialization, and a genuinely new
audio source kind must be added to `AudioSourceKind` and its parser.

The fake architectures used by tests are deliberately not production
registrations. They prove different capability sets, profiles, boundary
policies and budgets, runtime sessions, and mixed-timeline cut assembly without
shipping a second family.

## Deliberate non-goals

Three things look like inconsistencies and are not. Each was considered and
rejected on the merits.

- **LTX vocabulary stays in the generic capability enums.** `PromptRelay`,
  `Retake`, `IcLora`, `Hdr`, and the four upscale modes are a published
  cross-language contract. Hiding the spec's LTX-shaped fields behind an opaque
  payload would cost the catalog its typed wire and buy nothing: the meaning of
  each is already module-owned.
- **The timeline audio track has no per-clip capability gate.** Every
  `AuthoringFeature` is clip-scoped and a document-level entity has no owning
  clip. The backend validates the projected segments after projection, which is
  the first point at which a clip owns them.
- **`frontend/renderUtils.ts` keeps its own frame-alignment constant.**
  Publishing it would change the wire contract for a display-only rounding
  helper.

## Gates

`dotnet test SwarmUI-VideoStages.Tests.sln` is the backend gate; `npm run build`
runs Biome, TypeScript, jest, and the esbuild bundle for the frontend. Both were
green at the commit this document describes (2026-07-24). Counts are
deliberately not recorded here — they go stale within days and the gates do not.
