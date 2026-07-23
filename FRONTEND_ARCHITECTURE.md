# VideoStages frontend architecture

The frontend authors one LTX Video timeline. Entry points and options are data
on that timeline, not separate editors or execution engines.

## User-facing path model

The starting material is one of:

- text-to-video;
- a host-generated image used as clip-zero guidance;
- a user-provided init image used as guidance;
- a user-provided source video;
- a source-video-only clip; or
- the separate global Refine Video action.

Every authored timeline then has only two structural choices:

- one or many clips; and
- one or many active stages per clip.

Boundaries, upscaling, LoRAs, IC-LoRAs, major/relay prompts, retakes, frame
references, and audio policies decorate that structure. They do not create new
execution engines.

`executionPath.ts` is the pure, nontechnical projection of those decisions. It
must describe user intent and diagnostics without reproducing backend graph
construction.

## Ownership

```text
VideoStagesApp
├── LtxHostBridge
│   ├── lifecycle and carrier events
│   ├── LTX-only model capabilities
│   ├── host defaults and starting media
│   └── media selection and metadata probing
├── AuthoringRepository
│   ├── versioned JSON decode and migration
│   ├── prompt-carrier codec
│   ├── UI-state codec
│   └── backend-compatible encode
├── DocumentStore
│   ├── canonical authoring document
│   ├── monotonic revision
│   ├── commands and pure reducers
│   └── typed change impact
├── Domain
│   ├── defaults and normalization
│   ├── stable entity identity
│   ├── selectors and invariants
│   ├── authoring diagnostics
│   └── execution-path projection
└── UI
    ├── timeline renderers
    ├── detail panels
    ├── gesture controllers
    └── draft and focus sessions
```

The boundaries are strict:

- only the host bridge may read SwarmUI globals or host DOM;
- only the repository may read or write the Data, prompt, and UI-state
  carriers;
- only the document store may commit authored state;
- UI code reads cloned document snapshots and submits changes to the
  repository;
- the repository reduces compatibility snapshot submissions to one atomic
  batch of stable-ID commands before committing them;
- async work commits by stable entity ID and expected document revision;
- renderers are pure apart from wiring DOM callbacks supplied by the
  orchestrator.

## Canonical document

The canonical document is versioned and gives durable IDs to clips, stages,
references, prompt windows, retakes, audio segments, audio tracks, and audio
spans. Array indexes remain a rendering concern, never entity identity.

Legacy array and unversioned object JSON are accepted only by the decoder. The
encoder emits the current document while retaining the existing backend field
shape. Prompt text remains compatible with existing `<videoclip>` tags.

Effective width, height, and FPS are resolved before clips are normalized.
Every frame/duration selector receives the resolved document FPS; UI code must
not independently re-read host FPS.

Undo and redo snapshot this canonical document, including prompt and UI
sidecars, and restore it through the same revision-checked command boundary as
ordinary edits.

## Planned multi-clip audio

Clip-local base audio and clip-local audio segments remain compatible. The
root document also supports logical audio tracks whose spans can cover one
clip, several adjacent clips, a timeline-time window, or several discontiguous
windows.

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

This mirrors the backend planning contract. Until a runtime mixer consumes
authored cross-clip tracks, the UI must label them as planned/non-executing and
must not partially execute an unresolved span.

## Validation and failure behavior

The frontend mirrors stable, graph-independent backend diagnostics:

- captured-stage audio reuse needs at least three active stages;
- prompt relay cannot use audio- or ControlNet-owned duration;
- executable retakes cannot combine with frame references;
- ordinary generated clips cannot execute retakes without source/refine media;
- a multi-clip timeline cannot mix HDR and non-HDR IC-LoRA policy.

Persisted invalid state is preserved and explained. New invalid activation is
disabled where doing so does not hide existing authored data.

Async media probing is fail-closed: reorder, deletion, replacement, a newer
pick, or a revision mismatch discards the stale result instead of targeting a
different clip.

## Refactor completion criteria

- Every state write is reduced to named document commands before commit.
- No renderer, panel, or gesture controller writes a carrier or canonical
  store object directly.
- No production UI controller mixes host access, carrier encoding, domain
  mutation, rendering, and async effects.
- Legacy decoding is isolated behind a versioned repository boundary.
- LTX is the only authorable model family.
- The execution-path projection and diagnostics cover every supported entry,
  clip/stage shape, and option family.
- Planned audio tracks can represent spans across one or more clips.
- Frontend Jest, TypeScript, Biome, bundle build, and the extension C# suite
  all pass.
