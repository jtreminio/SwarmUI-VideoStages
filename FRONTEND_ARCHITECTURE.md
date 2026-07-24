# VideoStages frontend architecture

The frontend authors one timeline. Each generated clip is locked to one video
architecture, while different clips may use different architectures.
Production currently offers LTX Video 2.3 models; the editor itself is driven
by the backend architecture catalog rather than LTX checks in generic UI code.

## User-facing path model

For each clip, the user makes three kinds of decisions:

1. Choose starting material: generated from text, guided by an image, sourced
   from a video, or source-video-only.
2. Run zero, one, or several stages, all from the same architecture.
3. Add options supported by that architecture and profile.

Finished clips are joined on one timeline. A boundary between different
architectures is always a cut. Same-architecture continue and crossfade are
offered only when the owning architecture supports them.

Upscaling, LoRAs, IC-LoRAs, major/relay prompts, retakes, frame references, and
audio policies decorate this path. They do not create separate editors or
execution engines.

## Ownership

```text
VideoStagesApp
├── VideoStagesHostBridge
│   ├── host lifecycle and carrier events
│   ├── model metadata
│   ├── root defaults and starting media
│   └── media selection and probing
├── AuthoringRepository
│   ├── strict schema-v3 decode
│   ├── prompt/UI sidecar codecs
│   └── backend-compatible encode
├── DocumentStore
│   ├── canonical document and revision
│   ├── named commands
│   ├── undo/redo
│   └── typed change impact
├── ArchitectureCatalog
│   ├── backend DTO codec
│   ├── model/profile resolution
│   ├── clip/stage/boundary capability views
│   └── stable rule diagnostics
├── ArchitectureAuthoringAdapters
│   └── architecture-owned panels, presets, defaults, and normalizers
├── Domain
│   ├── current-schema normalization
│   ├── stable entity identity
│   ├── architecture-neutral selectors
│   └── execution-path projection
└── UI
    ├── timeline renderers and gestures
    ├── detail panels
    └── draft/focus sessions
```

Only the host bridge reads SwarmUI globals or host DOM. Only the repository
reads or writes carriers. Only document commands commit canonical state.
Panels and timeline gestures consult capability views before creating values.

Generic patch commands cannot write clip architecture/profile or stage
model/profile identity. Named retarget and conversion commands resolve their
targets through the catalog, and whole-document diffs are accepted only when
identity changes can be re-derived from structural or source edits.

Structural edits (add/remove/move a ref, prompt window, stage, retake, or a
skip toggle) dispatch named commands; `saveState`/`saveClips` remain the debounced
path for value edits and are translated into the same commands by
`documentDiff`. One descriptor table (`documentCommands/listEntities.ts`)
describes every ID-addressed list — its collection, command field names, and
patchable keys — and both the reducer and the diff consume it, so a new
canonical field cannot be classified in one place and forgotten in the other.

## Catalog and capability views

The backend catalog retains architecture/profile identities, scoped capability
sets, boundary decisions, conditional rules, and constraints. The frontend may
use an LTX bootstrap descriptor while the catalog request is pending, but the
backend response becomes authoritative when available.

Catalog decoding is all-or-nothing: duplicate architecture/profile/model IDs,
dangling references, unknown capability values, or malformed rule constraints
reject the response instead of creating a partial capability view. A shared
C#/TypeScript fixture keeps entry modes, boundary constraint keys, conditional
rules, and model-profile gates aligned.

Every option is resolved through the same view:

```text
decision(feature)
authoringState(feature, hasPersistedValue)
├── visible
├── can create/edit
├── can remove
└── reason
```

Supported values are authorable. Unsupported values that are absent are
hidden or disabled. Persisted unsupported values stay visible, read-only, and
removable with an inline reason; normalization must not silently erase them.
The same resolver supplies diagnostics so panels and the error summary cannot
disagree.

This policy applies to:

- source video and entry modes;
- major and relay prompts;
- frame references and retakes;
- clip audio, source kinds, clip-length ownership, reuse, and segments;
- normal LoRAs, IC-LoRAs, and HDR;
- each supported upscale mode; and
- boundary choices.

## Clip architecture conversion

Changing stage 0 to another architecture is an explicit destructive but
undoable conversion. The command:

1. previews incompatible settings that will be removed;
2. retargets every authored stage, including skipped stages;
3. updates architecture and profile identities;
4. removes only unsupported architecture-owned settings;
5. preserves every supported prompt/media setting plus duration, stable IDs,
   ordering, and architecture-neutral audio placement;
6. repairs affected executable-neighbor boundaries to cuts; and
7. commits as one history entry.

Direct edits to later stage models cannot change the clip architecture.
Persisted mixed-stage data is retained and diagnosed rather than normalized
away.

Stages may retarget to another profile inside the locked architecture. Authored
stage 0 remains the source of the clip profile; later stages retain their own
model profiles.

## Source-only clips

A sourced clip with no active generation stage uses architecture/profile
`none`. It remains selectable and editable: its source can be changed or
removed and a stage can be added. Re-enabling a skipped authored stage restores
the architecture/profile resolved from stage 0.

Adding the first active generation stage is one named batch and therefore one
revision, notification, and undo/redo point.

The `none` capability view is cut-only and exposes only neutral source/audio
features supported by the backend.

## Boundaries and structural edits

Boundary policy uses executable neighbors, not raw array adjacency. Adding,
deleting, skipping, re-enabling, reordering, or converting clips re-evaluates
the affected executable boundaries. This handles active clips separated by
skipped or otherwise non-executable clips.

Invalid persisted joins keep their requested value for repair while the
execution projection shows the effective cut and generation remains blocked.
Overlap choices and previews use the owning boundary rule's frame step,
minimum, maximum, default, and continuity-window offset rather than a global
LTX frame grid.

## Planned multi-clip audio

Clip-local audio and segments remain supported according to the clip
architecture. The root document separately stores architecture-neutral logical
audio tracks:

```text
AudioTrack
├── stable track ID
├── source metadata
└── spans[]
    ├── stable span ID
    ├── optional first/last clip IDs
    ├── optional timeline start/length
    ├── source start
    └── optional clip-relative start/length
```

A track may span one clip or several clips. Until a runtime mixer consumes
cross-clip tracks, the UI labels them as planned/non-executing and never
partially executes an unresolved span.

## Execution-path projection

`executionPath.ts` is a nontechnical projection for the toolbar and interactive
architecture map. It reports:

- entry material;
- single/multi-clip and single/multi-stage shape;
- architecture/profile per executable clip;
- requested and effective joins;
- option summaries; and
- planned audio-track coverage.

It does not reproduce backend nodes, latents, or graph construction and never
hardcodes one global engine label.

The implementation is split by responsibility: boundary projection, audio
projection, and display formatting are separate from the main orchestration.
Architecture policy similarly separates identity, feature values, clip/stage
views, and boundary policy. LTX IC-LoRA normalization, HDR recognition, presets,
and editor sections live behind the LTX authoring adapter.

## Completion rules

- The current schema is v5 only; no older/unversioned/PascalCase migration path.
- The emitted document is pinned by `Tests/fixtures/authoring-document.json`,
  asserted from both jest and the C# suite.
- Every clip/stage model choice is catalog-resolved.
- Every option panel and timeline creation gesture uses capability views.
- Unsupported persisted values survive with actionable diagnostics.
- Architecture conversion is atomic and exactly undoable/redoable.
- Generic frontend modules contain no LTX architecture branches.
- Planned audio tracks may span one or more clips.
- Jest, TypeScript, Biome, bundle build, and the extension C# suite pass.
