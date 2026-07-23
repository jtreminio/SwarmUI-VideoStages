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
6. Assemble same-architecture non-cut runs, then hard-cut those runs together.
7. Trim and publish one final timeline.

The apparent entry points are inputs to this path, not separate executors:

- text-to-video;
- host image or user init image to video;
- source video followed by zero, one, or several generation stages;
- source-video-only clips; and
- the separate global Refine Video action.

Single-clip/multi-clip and single-stage/multi-stage combinations use the same
coordinator. Options decorate a clip or stage; they do not create parallel
execution engines.

## Identity and lock

`ArchitectureId` identifies a video family such as `ltx2`, `wan`, or `none`.
`ModelProfileId` identifies a more specific model generation such as
`ltx-2.3`.

Authored stage 0 establishes the clip architecture and clip model profile.
Every later authored stage is validated against that architecture, even when a
stage is skipped. Later stages may resolve to different profiles inside the
same architecture; each stage keeps its own resolved profile. A sourced clip
with no active generation stages executes through the neutral `none` runtime
while its dormant authored stage chain is still checked for architecture and
per-stage profile consistency.

Unknown models, unknown profiles, mixed stage architectures, unsupported
options, and invalid joins produce blocking diagnostics before the extension
mutates the workflow graph.

## Architecture module boundary

Common code knows only:

- architecture and profile identifiers;
- generic clip ordering, timing, source, and boundary plans;
- capability and rule decisions;
- opaque architecture-owned clip payloads;
- runtime-session and boundary-assembler contracts; and
- decoded clip artifacts.

An architecture module owns:

- model recognition and profile resolution;
- its catalog descriptor and conditional rules;
- validation and compilation of architecture-specific stage options;
- creation of its runtime session;
- latent/VAE/conditioning/stage transitions;
- non-cut joins it supports; and
- decoding its final clip.

LTX implementations live under `VideoStages.Architectures.Ltx2`. Common
planning, coordination, and assembly must not instantiate LTX managers, inspect
LTX compatibility IDs, or create LTX nodes.

`VideoArchitectureManifest` is the production composition root. Each
registration supplies its module, runtime factory, host integration, API
routes, and dependency registration together. The rest of the application does
not maintain parallel architecture lists.

## Capability catalog

The backend catalog is authoritative. It publishes stable capabilities by
scope:

- architecture: entry modes, multi-stage, native audio, decoded output;
- model profile: samplers, schedulers, dimensions, frames, and normal LoRA;
- clip: source video, prompts, relay, references, retakes, audio;
- stage: input modes, each upscale mode, LoRA, IC-LoRA, HDR, frame references;
- boundary: cut, continue, and crossfade rules; and
- output: decoded video, attached audio, standalone audio.

A rule decision includes support state, a stable code, a user-facing reason,
scope, optional entity identity, and typed constraints. The frontend mirrors
these decisions for authoring, but the backend revalidates them before graph
mutation.

Typed boundary and conditional-rule policies are also the publication source:
the evaluator that accepts or rejects a plan consumes the same policy object
serialized into the catalog. A shared C#/TypeScript contract fixture guards the
wire keys, constraints, and profile gates.

## Neutral artifact and joins

An architecture runtime returns a `DecodedClipArtifact` containing only:

- decoded video output identity;
- optional decoded audio output identity;
- literal width, height, FPS, and frame count;
- architecture provenance; and
- clip provenance.

It cannot carry a latent, VAE, model compatibility object, or a nested media
wrapper that carries those values transitively.

Cross-architecture boundaries are cut-only. A persisted invalid continue or
crossfade keeps its requested value for repair, compiles to an effective cut,
and blocks generation. Within one architecture, its boundary rules determine
whether continue or crossfade is valid.

Timeline assembly partitions clips into maximal runs connected by effective
non-cut boundaries. Each run is assembled by its architecture; the decoded run
outputs are then concatenated with architecture-neutral cuts. Final output
metadata never inherits the first clip's model compatibility.

## Audio ownership

Clip-local source selection, clip-length ownership, segments, voice reference,
and stage-latent reuse are separate decisions. Architecture modules own any
model-latent audio behavior.

For LTX multi-stage clips, separated audio latents remain architecture-owned
and flow directly between stages. They are not decoded to audio and encoded
again merely to construct the next stage.

`AudioTimelinePlan` also represents architecture-neutral authored tracks whose
spans may cover one clip, several adjacent clips, a timeline window, or
discontiguous windows. Planned/provisional spans remain atomic until their
timing and runtime mixer are available; VideoStages must not partially execute
an unresolved span.

## Runtime invariants

- A plan is fully architecture-resolved before any extension graph mutation.
- One runtime session handles only its declared architecture.
- A session result must match the requested clip and architecture.
- Every clip returns a decoded video artifact with positive literal metadata.
- Multi-clip assembly receives exactly one valid artifact per planned clip.
- Source installation, execution, assembly, and publication fail closed.
- Only the final publisher advances captured host save nodes.
- Intermediate artifacts and architecture compatibility never leak into the
  final mixed timeline.

## Adding another architecture

Adding WAN or another family should require:

1. a model resolver and profile descriptors;
2. scoped capabilities and rules;
3. an architecture-owned clip compiler and opaque payload;
4. a runtime-session factory;
5. optional same-architecture boundary assembly; and
6. contract tests using the common dispatcher and timeline assembler.

It must not require changes to generic document parsing, clip ordering,
history, cut assembly, output publication, or the frontend's generic panel
routing.

The fake architectures used by tests are deliberately not production
registrations. They prove different frame grids, capability sets, profiles,
runtime sessions, and mixed-timeline cut assembly without implementing WAN.
