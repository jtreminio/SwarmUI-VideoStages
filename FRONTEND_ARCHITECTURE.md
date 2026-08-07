# VideoStages frontend architecture

The frontend authors one timeline. Each generated clip is locked to one video
architecture, while different clips may use different architectures.
Production registers specialized LTX Video 2.3, MiniMax H3, WAN, the permissive
generic host-video fallback, and the source-only `none` architecture. The
editor is driven by the backend architecture catalog rather than model-name
checks in generic UI code.

## User-facing path model

For each clip, the user makes three kinds of decisions:

1. Choose starting material: generated from text, guided by an image, init-video
   from a video, or init-video-only.
2. Run zero, one, or several stages, all from the same architecture.
3. Add options supported by that architecture and profile.

Finished clips are joined on one timeline. A boundary between different
architectures is always a cut. Same-architecture continue and crossfade are
offered only when the owning architecture supports them.

Upscaling, LoRAs, IC-LoRAs, major/relay prompts, retakes, frame references,
clip audio, and timeline audio tracks decorate this path. They do not create
separate editors or execution engines.

## Layout

The layout is deliberately shallow: small semantic modules sit directly in
`frontend/`, with clustered subdirectories only where they improve navigation.
There is no `frontend/core` and no `frontend/ui`, and no file-count target.

```text
frontend/
├── main.ts                  entry point and host registration
├── videoStagesTimeline.ts   composition root
├── authoringSnapshot.ts     one catalog/default/policy snapshot per transaction
├── host/                    the only readers of SwarmUI globals
├── persistence/             carriers, codec, durable snapshot
├── store.ts                 canonical document, revision, dispatch
├── documentCommands.ts      command union and reducer
├── documentCommands/        list descriptor table, generic list reducer
├── documentDiff.ts          whole-document diff → command batch
├── architectures/           catalog, capability policy, conversion, local UI
├── timelineView/            track rendering and toolbar
├── detailStrip/             docked panel editors
└── *.ts                     domain, gestures, tracks, widgets, utilities
```

`main.ts` is the only esbuild entry point. It registers the `videoclip` prompt
prefix, the Refine Video media button, and the bottom-bar tab, then schedules
`timeline.init()` on the host's post-param-build pass, retrying every 250 ms
until the hidden Data input exists and warning once after 10 s if it never
appears.

`Assets/video-stages.js` is build output; edit `frontend/*.ts` and rebuild.

## Host boundary

`frontend/host/**` is the only code that touches SwarmUI globals. It has two
parts: `VideoStagesHostBridge`, an injectable interface over host inputs, model
metadata, media primitives and lifecycle hooks; and `swarmUiAdapters.ts`, narrow
free functions over host UI services (bottom-tab mount, popovers, sliders, input
browser, websocket, param refresh).

Everything else reads only extension-owned DOM plus document-level event
plumbing (pointer, keydown, pagehide) that no bridge could usefully wrap.

Host-adjacent state that is not the bridge: `rootDefaults.ts` resolves dropdown
values and core dimensions through it; `swarmInputs.ts` names the carriers and
the enable toggle; `initVideoProbe.ts` owns probing policy over the bridge's
raw media element; `timelineHostLifecycle.ts` binds the prompt input's
input/change events, the group toggle, a 200 ms `syncFromCarrier` poll (host
presets and cookie restores are not observable any other way), the
pagehide/beforeunload draft flush, Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y, and the
host's param-refresh hook so model and LoRA selects repaint after a refresh.

## Document, carriers, and commits

```text
persistence/carrierAdapter.ts        the only writer of the authoring carriers
├── data param (input_videostages)   the canonical document JSON
├── prompt param                     clip prompts and relay windows
└── uiState.ts                       browser-local hues and window IDs
persistence/documentCodec.ts         strict decode, canonical encode
persistence/durableAuthoringState.ts localStorage snapshot + boot protection
persistence/repository.ts            saveState / saveClips / dispatch facade
store.ts                             canonical model, revision, subscribers
documentCommands/                    list descriptor table and generic reducer
documentDiff.ts                      before/after documents → one batch command
```

Schema v6 is the exact current contract. Decode has one bounded v5 migration
that renames clip `architecture` to `architectureHint`; every other version
surfaces a one-shot notice and loads nothing rather than loading partially.

Every canonical commit reaches `store.dispatch`. Two APIs get there:

- `dispatchDocumentCommand` — structural edits. Adding and deleting frame refs,
  prompt windows, retakes, stages and clips, both skip toggles, and the
  source-only clip's first-stage batch all dispatch named commands. The draft
  queue owns dispatch, the revision compare-and-set, staleness, and where the
  selection lands afterwards.
- `saveState` / `saveClips` — debounced value edits. The caller mutates a
  cloned document and `diffDocuments` derives the equivalent command batch,
  failing closed on duplicate IDs or identity changes it cannot re-derive.

`documentCommands/listEntities.ts` is one descriptor table describing every
ID-addressed list once — collection path, owner kind, identity field, patchable
keys — and both the reducer and `documentDiff` consume it, so a new canonical
field cannot be classified in one place and forgotten in the other.
Exhaustiveness is a compile error: a key that is neither patchable nor
explicitly reserved fails the type gate by name.

Generic patch commands cannot write the clip architecture/profile hints or
stage model/profile identity, and a patch carrying a reserved key is rejected
rather than silently overwriting a child collection. Named retarget and
conversion commands resolve their targets through the catalog.

Reads are not centralized in the same way. `readStateToken()` has exactly two
importers — the carrier adapter and its own definition; everything else uses
`store.revision()` as its staleness guard.

The store commits in a load-bearing order: serialize, prove the exact bytes
decode, write both carriers quietly, adopt the reparse as canonical, bump the
revision, then dispatch host change events — whose synchronous listeners
re-enter `syncFromCarrier` and must already see the new token.

`persistence/durableAuthoringState.ts` keeps a localStorage snapshot of the
document plus per-clip prompts. On first load the snapshot re-hydrates both
carriers and holds a short boot-protection window, because SwarmUI restores
cookie-backed params asynchronously and the first value observed can be blank.

## Catalog and capability views

Backend catalog schema v2 has exactly architecture and resolved-model records.
Architecture records carry ID, label, complete descriptor capabilities,
boundary decisions, conditional rules, and constraints. Resolved-model records
carry architecture/profile identity, core model facts, frame grid, entry
abilities, complete effective capabilities, and frame-reference positions. It
is the frontend's sole authority for executable architecture/model identity and
authoring policy; model names and host model-class metadata are never used to
infer identity.

`catalogRepository.ts` exposes five explicit states:

- `loading` and `unavailable` have no catalog authority;
- `ready` has the current authoritative DTO;
- `refreshing` retains the last-known DTO while requesting a replacement; and
- `stale` retains that exact DTO after refresh failure.

Initial loading/unavailable states render a status view instead of reading or
hydrating the authoring document. Unavailable offers Retry. Host model refresh
uses a forced catalog request without clearing last-known data; stale renders a
nonblocking warning and Retry while the existing capability-backed UI remains
active. Generation-owned requests prevent superseded responses from
overwriting newer state.

Catalog decoding is exact and all-or-nothing: the wrong schema version, unknown
or missing keys, duplicate architecture/model IDs, dangling architecture
references, unknown capability values, or malformed rule constraints reject
the response instead of creating a partial capability view. There is no
frontend profile table, extras overlay, or output-capability alias. Shared
C#/TypeScript fixtures keep entry abilities, frame-reference positions,
boundary constraint keys, conditional rules, and resolved-model gates aligned.

`buildArchitectureModelCatalog` may apply current host dropdown labels to
backend-known model entries and retains backend-only models. A host model absent
from the backend DTO has null architecture/profile identity and is not an
authorable architecture model.

`captureAuthoringTransactionSnapshot` captures the catalog state, root
defaults/model catalog, capability resolver, and generated entry mode once for
one synchronous render, save, or command dispatch. The timeline and detail dock
receive that snapshot rather than rereading live host/catalog state midway
through the transaction. The next user event captures again; this is not a
second global cache.

Options resolve through one view:

```text
ClipCapabilityView / StageCapabilityView
decision(feature)                     → supported, reason, rule
authoringState(feature, hasPersisted) → the above plus visible, enabled
```

`clipStageViews.architectureFeatureSupport` is the sole capability-support
predicate. Diagnostics and architecture conversion keep their distinct purposes
but share that one decision, so a conversion cannot keep a setting the
diagnostics call unsupported. All five conditional rules reach `decision()`.

Supported values are authorable. Unsupported values that are absent are hidden
or disabled. Persisted unsupported values stay visible and disabled with an
inline reason plus a panel-owned removal affordance; normalization never erases
them, and diagnostics report them instead.

The feature vocabulary is `initVideo`, `frameReferences`, `clipReferences`,
`retake`, `majorPrompt`, `promptRelay`, `clipAudio`, `audioReuse`,
`stageLoras`, `icLora`, `upscale`, plus per-stage `sampler` and
`scheduler`. Supported audio source kinds and upscale modes are lists on the
same views.

Two related gates use their own shapes rather than `decision()`: clip entry
material, through `architectureSupportsClipStart`; and clip-length ownership,
through plain source predicates.

Boundaries use `BoundaryCapabilityView`. Its `effective(requested)` is the sole
authority on join validity and honours both support and constraints, so a
persisted continue into a init-video clip, a clip with no active stage, or a clip
with a first-frame reference is reported rather than silently degraded.

Diagnostics are a second evaluator over the same catalog rules
(`authoringDiagnostics.ts` → `architectures/diagnostics.ts`,
`conditionalRules.ts`). Shared rule codes and reasons keep the panel notices and
the timeline error summary saying the same thing.

## Clip architecture conversion

Changing stage 0 to another architecture is an explicit destructive but
undoable conversion. Preview and reducer share one planner, so the confirmation
summary cannot drift from the mutation. The command:

1. resolves the target architecture, profile and model against the catalog;
2. retargets every authored stage, including skipped stages;
3. updates the cached architecture and profile hints;
4. removes only unsupported architecture-owned settings, clearing each field it
   reports;
5. preserves every supported prompt/media setting plus duration, stable IDs,
   ordering, clip hue, and root audio tracks;
6. repairs affected executable-neighbor boundaries to cuts; and
7. commits as one revision, notification, and history entry.

Direct edits to later stage models cannot change the clip architecture.
Persisted mixed-stage data is retained and diagnosed rather than normalized
away.

Stages may retarget to another profile inside the locked architecture. Authored
stage 0 remains the source of the clip profile; later stages retain their own
model profiles.

## Source-only clips

A init-video clip with no active generation stage resolves to architecture/profile
`none`. It remains selectable and editable: its source can be changed or
removed and a stage can be added, and a clip with a source video may now be
emptied down to zero stages. Emptying a clip that has no source video is still
rejected. Re-enabling a skipped authored stage restores the
architecture/profile resolved from stage 0.

Adding the first active generation stage is one named batch — convert then add
— and therefore one revision, notification, and undo/redo point.

The `none` capability view is cut-only and exposes only neutral source/audio
features supported by the backend.

## Boundaries and structural edits

Boundary policy uses executable neighbors, not raw array adjacency. Adding,
deleting, skipping, re-enabling, reordering, or converting clips re-evaluates
the affected executable boundaries. This handles active clips separated by
skipped or otherwise non-executable clips.

Invalid persisted joins keep their requested value for repair while the
boundary view reports the effective cut and generation stays blocked. Overlap
choices and previews use the owning boundary rule's frame step, minimum,
maximum, default, and continuity-window offset rather than a global LTX frame
grid.

## Timeline audio tracks

Clip base audio remains clip-owned and architecture-gated. Timeline audio is a
separate, architecture-neutral document-level model:

```text
AudioTrack
├── stable track ID
├── source
│   ├── kind (Upload | AceStepFun | Native | ControlNet | External)
│   ├── reference
│   └── optional uploaded media
├── optional volume
└── spans[]
    ├── stable span ID
    ├── timeline start / length (null until placed)
    └── source start (the trim)
```

There is no clip-local authored audio-span model any more; timeline tracks
replaced it outright at schema v5. New authoring writes exactly one span per
track — one lane, one window, free to cross clip seams — and the array shape
survives only for compatibility, normalized into independent lanes on load.
Lanes are authored on the audio row and edited in the audio-tracks panel; they
may overlap, and the backend mixes them additively. The clip audio panel lists
only the lanes whose windows intersect that clip.

## Timeline UI

`videoStagesTimeline.ts` is the composition root. It builds the viewport,
gesture router, detail strip, history, host lifecycle and the track modules,
attaches them in a load-bearing order — tracks, then the detail strip, then the
router, which honours the strip's capture-phase chip claim — and subscribes one
store listener that captures history and then repaints.

### Rendering

`timelineView/` renders a header (enable toggle, clip count, resolution/FPS
chip, add clip, zoom group, unit toggle, undo/redo, readout), a diagnostic
panel, a ruler, and the track rows: prompt (major segment plus relay windows),
video (clip regions, stage chips, badges, boundary seams), references, and
audio (per-clip base audio plus timeline audio lanes). Rendering is
string-built and wiped wholesale; the detail dock is a sibling element so it
survives a repaint.

`trackDomUtils.spanGeometry` is the one owner of span geometry — percent or px
units, minimum width, and output clamping as explicit options — so a span
longer than its lane can no longer render outside it, and preview and commit
cannot disagree.

### Gestures

One capture-phase router (`gestureRouter.ts`) owns every press-drag on the
tracks body. Tracks register routes with an explicit priority — retake 50,
timeline audio 40, references 30, prompt relay 20, clip linking 10 — and the
highest route to return a session wins. The router owns the shared lifecycle:
activation threshold, document move/up, Escape cancel, and the one-shot
post-drag click swallow. Click-only behavior (select, shift-delete, chips)
stays module-owned.

`windowTrack.ts` is the single owner of "a span on a lane": move, edge resize,
drag-to-create with ghost, tap-create, shift-click delete, router registration
and the stale-token guard. Its config carries a scope (read / resolveLane /
write, with an optional owner-removal hook for track-level delete), a
configurable owner attribute, and a snap-target supplier that defaults to the
lane's own walls. Retake, prompt relay, and the timeline audio track are all
configurations over it; clip linking (drag-reorder, resize, drop indicator) has
its own session on the same router.

### Viewport

`timelineViewport.ts` owns pixels-per-second, the seconds/frames unit, and
scroll restoration across re-renders, anchored so a zoom keeps the leading
edge. Zoom comes from toolbar buttons, a slider that commits on release, Fit,
and Ctrl+wheel anchored at the pointer; both values persist browser-locally in
`timelineViewState.ts`. `timelineSnap.ts` supplies the shared 8 px snap —
primary targets are the neighbouring span's edges, fallback targets are clip
seams — gated by the Snap setting.

### Detail strip

Selection drives one docked editor (`.vst-detail`, a fixed-width column inside
the timeline shell). `timelineDetailStrip.ts` owns the render loop;
`detailStrip/panelRouter.ts` clamps the selection against the current document,
builds the breadcrumb, and dispatches one of the panel builders — clip (also
serving ref, IC-LoRA and retake selections), clip audio, timeline audio tracks,
major prompt, relay window, boundary, and settings.

`focusSession.ts` captures and restores caret and selection across rebuilds and
suppresses commits during slider drags. `draftQueue.ts` is the single commit
pipeline: immediate commits, 200 ms debounced commits that coalesce keystrokes,
structural commits that dispatch a command envelope and then re-point the
selection, all guarded by revision staleness and flushed before teardown or
page exit.

`selection.selectionAfterRemoval` is the one delete-then-reselect policy,
including the IC-LoRA drive reconciliation. `applyClipSkip` / `applyStageSkip`
own the skip mutation and both of its reconciliations, and `skipVocabulary`
states the glyphs and wording once. `applySelectionHighlight` is the only writer
of the selected-region class.

### Widget vocabulary

`detailWidgets.ts` owns the panel widget vocabulary, built on SwarmUI's native
`.auto-input` classes rather than custom CSS: fields, selects, numbers, host
sliders, checkboxes, textareas, media-pick rows (upload or host input browser),
`?` help popovers wired to the host's `doPopover`, static and accordion sections
with remembered open state, and `buildRepeatingEditor` — the one owner of every
add / delete / select / skip repeater in the strip (stages, frame refs, IC-LoRAs,
relay windows, audio tracks). `Assets/video-stages.css` adds only `.vst-*`
layout hooks on top; both add buttons use the host's native button classes.

### Undo/redo

`timelineHistory.ts` is a 50-deep stack of serialized canonical documents, not
a command log. Every store notification captures before the repaint, so each
commit or external carrier change lands exactly one entry. Undo and redo
rewrite the whole document under a suppress flag, against an expected revision.
A restore whose write throws consumes the entry and rethrows, so the stack
advances instead of re-offering the same failing snapshot and reporting a
successful no-op.

### Settings

Two browser-local authoring toggles — Snap and Auto-collapse — live in
`timelineAuthoringSettings.ts` and are edited in a host-styled modal opened from
the detail-strip gear; re-enabling Auto-collapse clears the remembered accordion
sections. Timeline resolution and FPS are a separate concern: the topbar chip
opens the docked Settings panel, whose FPS field writes through to the core
Video FPS param and reads back through the carrier token's inherited-dims
signature.

### Clip colors

`clipColor.ts` assigns each clip a stable hue by maximizing circular distance
from the hues already in use. Hue is browser-local (`uiState.ts`), not part of
the backend document, and tints regions, audio cells, and chips.

## Architecture-owned authoring

LTX IC-LoRA normalization, presets, drive availability, weight download, and
the IC-LoRA editor section remain LTX-local.
`architectures/behaviorRegistry.ts` is a centralized set of explicit LTX
ownership guards, not a polymorphic registry.
`architectures/authoringPanels.ts` directly selects either the LTX editor or
the generic persisted-value removal panel.

Architecture policy separates identity, feature values, clip/stage views, and
boundary policy. Backend catalog/model facts authorize feature visibility;
local LTX code only implements already-authorized behavior and DOM. A second
bespoke frontend should add an explicit owner branch first and extract a common
contract only if two implementations reveal one. Labels, resolved model
identities, capabilities, rules, and model recognition always come from the
backend DTO.

## Completion rules

- Schema v6 is exact; only the bounded v5 `architecture` → `architectureHint`
  migration is accepted.
- The emitted document is pinned by `Tests/fixtures/authoring-document.json`,
  asserted from both jest and the C# suite.
- Every clip/stage model choice is catalog-resolved.
- Every option panel and timeline creation gesture uses capability views.
- Every render/save/command uses one authoring transaction snapshot.
- Unsupported persisted values survive with actionable diagnostics.
- Architecture conversion is atomic and exactly undoable/redoable.
- Only `frontend/host/**` reads SwarmUI globals.
- Only `persistence/carrierAdapter.ts` writes the authoring carriers.
- Every canonical commit reaches the store's command dispatch.
- Generic frontend modules contain no LTX architecture branches.
- Timeline audio tracks are architecture-neutral and execute.
- `npm run build` (Biome, TypeScript, jest, esbuild bundle) and the extension
  C# suite pass. Test counts are deliberately not recorded here.
