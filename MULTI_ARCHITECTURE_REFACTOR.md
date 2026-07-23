# VideoStages multi-architecture refactor

## Outcome

VideoStages owns one generic timeline. Each clip owns exactly one video
architecture, every authored stage in that clip (including skipped stages) uses
that architecture, and different clips may use different architectures.
Architecture-specific modules plan and execute a clip; generic timeline code
joins their decoded outputs.

Production registers only source-only `none` and LTX Video 2.3. WAN behavior is
intentionally out of scope. Test-only architectures with deliberately different
profiles, frame grids, and capabilities prove that common code is not
accidentally coupled to LTX.

## Fixed product decisions

- Stage 0 establishes a generated clip's architecture.
- Every later authored stage in the clip must resolve to the same architecture.
- Later stages may use different model profiles within that architecture. The
  clip profile always follows authored stage 0, even when stage 0 is skipped.
- A sourced clip with no generation stages has architecture `none`.
- Changing a clip's architecture is one explicit, atomic, undoable conversion.
  It retargets all stages, removes incompatible architecture-owned settings,
  and preserves architecture-neutral clip data.
- Persisted invalid mixed-stage data remains visible and diagnostic, but
  generation is blocked.
- Different clips may use different architectures.
- A boundary between different architectures supports `cut` only. Explicit
  architecture conversion changes affected boundaries to cuts; invalid loaded
  non-cut requests remain visible, compile to an effective cut, and block
  generation.
- Same-architecture joins are limited by that architecture's capabilities.
- There is no v2-to-v3 authoring-document migration. The local-only schema is
  replaced directly and existing fixtures are updated.
- Model compatibility metadata helps recognize an architecture but does not
  replace a model profile. LTX model generations may share one compatibility
  family while differing in supported options.

## User-facing path model

The simple path model remains:

1. Choose the starting material for each clip.
2. Run zero, one, or several stages for that clip.
3. Apply supported stage and clip options.
4. Decode one finished video/audio artifact for the clip.
5. Join clip artifacts on the timeline.
6. Publish one final output.

Architecture affects steps 2–4. It must not create a separate timeline editor,
document store, history system, or output publisher.

## Identity model

Two identities are required:

- `ArchitectureId`: a stable family discriminator such as `ltx2`, `wan`, or
  `none`.
- `ModelProfileId`: a more specific stable model class such as `ltx-2.3`.

Resolved model information is represented independently from its display name:

```text
ResolvedVideoModel
├── model name
├── architecture id
├── model profile id
└── host compatibility metadata
```

The backend verifies authored architecture identity against resolved models.
The frontend uses the catalog to filter choices, but it is never the security or
correctness boundary.

## Capability and rule model

The backend owns the authoritative architecture catalog. The frontend consumes a
serializable projection of that catalog and derives panel views from it.

Capabilities are typed by scope:

| Scope | Capability groups |
| --- | --- |
| Architecture | entry modes, multi-stage generation, native audio, decoded output |
| Model profile | samplers, schedulers, dimensions, frame rules, normal LoRA |
| Clip | source video, prompts, relay, references, retakes, audio sources/segments |
| Stage | image/video input, upscale modes, LoRA, IC-LoRA, HDR, reference frames |
| Boundary | cut, continue, crossfade, continuity constraints |
| Output | video, attached audio, standalone audio |

A rule evaluation returns:

```text
supported | unsupported | conditional
├── stable code
├── user-facing reason
├── scope and entity id
└── optional typed constraints
```

Panels consume capability views rather than comparing architecture strings.
Unsupported new settings are hidden or disabled. Unsupported persisted settings
remain visible with diagnostics and a deliberate removal/conversion path.

## Backend module boundary

Target dependency direction:

```text
VideoStages.Core
    ↓ contracts only
VideoStages.Architectures.Abstractions
    ↑ implemented by
VideoStages.Architectures.Ltx2
    ↓ host adapters only
VideoStages.Infrastructure.Swarm
```

The common coordinator may know:

- architecture and profile identifiers;
- generic clip/stage/boundary plans;
- rule decisions;
- neutral decoded clip artifacts;
- architecture module interfaces.

It may not know:

- LTX node classes;
- LTX latent/audio-latent formats;
- LTX compatibility IDs;
- LTX guide, sampler, IC-LoRA, retake, or transition details.

An architecture module owns:

1. recognizing its models and resolving profiles;
2. publishing its capability descriptor;
3. validating architecture-specific authoring;
4. compiling architecture-specific clip/stage payloads;
5. selecting stage transitions;
6. building continuation inputs when supported;
7. executing stages;
8. decoding the final clip;
9. returning a neutral clip artifact.

Architecture-owned plan payloads are discriminated and opaque to the common
runner. Generic plans retain only fields required for ordering, diagnostics,
timeline timing, and dispatch.

The architecture manifest is the single production composition root. One
registration supplies the module, runtime-session factory, host-phase hooks,
API routes, and dependencies so catalog publication and execution cannot drift
apart.

## Runtime artifact boundary

The handoff from architecture execution to timeline assembly is decoded and
architecture-neutral:

```text
DecodedClipArtifact
├── decoded video media
├── optional decoded audio media
├── width / height / fps / frames
├── architecture provenance
└── clip provenance
```

Latents, VAEs, architecture compatibility tags, and intermediate graph details
do not cross this boundary, including transitively through a generic media
wrapper. The artifact carries only decoded graph outputs plus literal timeline
metadata. Timeline assembly must not copy the first clip's model compatibility
identity onto a mixed-architecture result.

For this refactor:

- cut is the only cross-architecture boundary;
- continue requires the same architecture and target support;
- the existing LTX-specific crossfade remains LTX-owned until a neutral pixel
  crossfade implementation replaces it;
- generic cut assembly operates on decoded video/audio outputs.

## Frontend module boundary

Target dependency direction:

```text
frontend/core
    ├── document and commands
    ├── architecture-neutral selectors
    ├── capability views and diagnostics
    └── execution-path projection
frontend/architectures
    ├── registry and catalog codec
    └── ltx2
        ├── descriptor
        ├── defaults
        ├── conversion
        └── architecture-owned panels/options
frontend/host
    └── VideoStagesHostBridge
```

The host bridge exposes model metadata and generic host lifecycle/media
operations. It is not named for an architecture. LTX-specific recognition and
defaults live under `frontend/architectures/ltx2`.

The canonical clip stores its architecture discriminator. Stage model choices
are resolved through the architecture catalog. Architecture conversion is a
named document command so history, diagnostics, persistence, selection, and
rendering observe one transaction.

The conversion command:

1. validates the target architecture and target model profile;
2. snapshots the current canonical document through existing history;
3. changes the clip discriminator;
4. retargets every stage to a valid target model/profile;
5. removes settings owned by unsupported architecture capabilities;
6. preserves prompt, duration, source media, generic audio placement, stable
   entity IDs, clip ordering, and other neutral data;
7. emits one change with value, structure, and capability impact.

Ordinary stage/source edits use the same command layer. It derives `none` for a
sourced clip with no active generation stage, restores identity from authored
stage 0 when generation becomes active, and rejects raw identity patches or
whole-document diffs that cannot be re-derived from the catalog.

## Implementation status

| Workstream | Delivered result |
| --- | --- |
| Identity and registry | Strong architecture/profile identities; production manifest contains `none` and LTX 2.3 |
| Backend planning | Common plans carry opaque architecture clip/stage payloads; validation completes before graph mutation |
| Backend runtime | Per-architecture sessions return decoded neutral artifacts; fake sessions prove mixed dispatch |
| Boundaries | Architecture-owned same-family policy; cross-family joins are cut-only |
| Frontend catalog | Strict all-or-nothing DTO parser plus shared C#/TypeScript rule-contract fixture |
| Frontend commands | Catalog-authoritative retarget/conversion and derived source-only transitions |
| Architecture UI | LTX IC-LoRA/HDR authoring lives behind an LTX adapter; generic panels use capability views |
| Persistence | Strict schema v3 codec; no legacy migration |
| Audio | Clip audio, segments, and native LTX latent reuse execute; spanning logical tracks remain explicitly planned/non-executing |
| Visualization | Interactive plain-language map covers entry material, clip/stage shapes, options, joins, conversion, and audio spans |

## Detailed implementation sequence

### Phase 0 — freeze the LTX behavior matrix

- Record the current supported LTX entry/options matrix as regression coverage.
- Run the full extension suite before architecture changes.

Exit: known behavior, passing tests, and no manual generated-bundle edits.

### Phase 1 — identities, registry, and catalog

- Add strong architecture/profile identifier types.
- Add model resolver and architecture registry contracts.
- Add typed capability descriptors and rule decisions.
- Register one production LTX module.
- Expose a serializable catalog through the extension API.
- Replace global `IsLtxTimeline` gating with per-clip resolution.

Exit: every planned clip has `ltx2` or `none`; unknown and mixed-stage clips
produce precise errors before graph mutation.

### Phase 2 — per-clip plan dispatch

- Split generic clip/timeline fields from LTX-owned plan payloads.
- Have the common plan compiler resolve one architecture module per clip.
- Delegate stage-option validation and compilation to LTX.
- Dispatch clip execution through the resolved module.
- Keep stages serial within a clip and existing clip parallelism unchanged.

Exit: the common runner does not construct an LTX manager or LTX stage
orchestrator directly.

### Phase 3 — neutral artifact and assembly

- Replace model/VAE-shaped terminal clip handoff with `DecodedClipArtifact`.
- Keep architecture-specific latent reuse entirely inside the clip executor.
- Make cut assembly accept neutral decoded outputs.
- Retain the requested cross-architecture continue/crossfade value for repair,
  compile its effective execution mode to cut, and add a blocking diagnostic
  before graph mutation.
- Stop inheriting the first clip's model compatibility metadata on final output.

Exit: a test-only second architecture can return a neutral artifact and be cut
beside an LTX clip without common code learning either implementation.

### Phase 4 — frontend catalog and clip lock

- Replace `ltxCapabilities.ts` with registry/catalog abstractions and an LTX
  descriptor under `frontend/architectures/ltx2`.
- Rename `LtxHostBridge` to `VideoStagesHostBridge`.
- Store architecture identity on each canonical clip.
- Filter stage 1..n model choices to the clip architecture.
- Block a direct stage edit that would create a mixed-architecture clip.
- Add the explicit architecture-conversion command.
- Replace the local schema directly; do not add a migration path.

Exit: ordinary edits cannot create mixed-stage clips; conversion is atomic and
undoable; invalid loaded state is preserved and diagnosed.

### Phase 5 — capability-driven UI

- Build architecture, clip, stage, and boundary capability views.
- Route option panels through those views.
- Remove architecture string checks from generic UI.
- Gate references, retakes, audio, relay prompts, LoRA, IC-LoRA, HDR,
  upscaling, and joins with stable rule decisions.
- Keep unsupported persisted values visible with reasons.
- Update the nontechnical execution-path projection to show architecture per
  clip and cut-only cross-architecture joins.

Exit: adding a registered architecture changes generic model choices and
capability views without editing unrelated panels.

### Phase 6 — proof and regression

- Add a test-only architecture with deliberately different capabilities.
- Test same-architecture multi-stage clips.
- Test different architectures in adjacent clips.
- Test cut-only cross-architecture assembly.
- Test invalid mixed-stage authoring and backend rejection.
- Test architecture conversion, undo, redo, and stable IDs.
- Test conditional/unsupported persisted options and diagnostics.
- Run all existing LTX entry, stage, audio, reference, retake, prompt, upscale,
  IC-LoRA, join, HDR, trim, and publication tests unchanged where possible.

Exit: extension tests, frontend tests, TypeScript, Biome, bundle build, and diff
checks pass.

### Phase 7 — structural review

- Search generic namespaces for LTX types, node names, compatibility IDs, and
  architecture-specific branches.
- Search generic frontend folders for architecture IDs and LTX-only option
  assumptions.
- Review failure paths to ensure validation precedes graph mutation.
- Review every new public/internal class for one responsibility and dependency
  direction.
- Update architecture documentation and the interactive path map.

Exit: no architecture leakage remains outside intentional adapters, all review
findings are fixed or explicitly documented, and the full suite passes again.

## Test matrix

| Area | Required proof |
| --- | --- |
| Model resolution | known profile, unknown model, same architecture/different profiles |
| Clip invariant | authored stage 0 establishes arch/profile; later same arch accepted; mixed rejected |
| Sourced-only | zero-stage clip resolves to `none` and can join by cut |
| Mixed timeline | LTX + fake architecture and fake + LTX both dispatch correctly |
| Boundaries | cross-arch cut accepted; continue/crossfade become cut with diagnostic |
| Conversion | all stages retargeted; incompatible options removed; neutral data kept |
| History | conversion undo/redo restores exact canonical document |
| Persisted invalid data | retained, shown, and generation-blocking |
| Capabilities | supported, unsupported, and conditional decisions round-trip |
| Catalog contract | C# serializer and strict TypeScript parser agree on rules, gates, and constraints |
| UI filtering | later stages list only models from the clip architecture |
| LTX regressions | every existing entry path and option family remains supported |
| Stage handoff | implicit incoming image guides only stage 0; later stages reuse the direct latent handoff |
| Stage audio | reusable LTX audio remains latent between stages without decode/ensure/re-encode churn |
| Planned audio | one logical track may span one or more clips and remains explicitly non-executing |
| Artifacts | generic assembly receives decoded outputs and owns final publication |
| Output metadata | mixed result has neutral output metadata, not first-clip model identity |

## Completion criteria

- Production registers LTX only, but common code supports multiple registered
  architectures.
- One clip cannot execute stages from multiple architectures.
- Different clips can execute different architectures in one timeline.
- Cross-architecture joins are cuts.
- The backend catalog is authoritative and the frontend mirrors it.
- Generic backend code has no LTX nodes, latent types, or compatibility checks.
- Generic frontend code has no LTX architecture branching.
- Architecture conversion is explicit, atomic, destructive, and undoable.
- Unsupported persisted settings are never silently discarded.
- Planned multi-clip audio remains architecture-neutral and may span one or
  more clips.
- All automated and structural checks pass.

## Completion evidence

- Production manifest: source-only `none` plus LTX Video 2.3; WAN remains
  unregistered.
- Backend suite: 638/638 tests.
- Frontend suite: 870/870 tests across 52 suites.
- Shared catalog contract: complete LTX descriptor is compared by C# and
  TypeScript.
- Required extension runner, TypeScript typecheck, Biome format/lint, bundle
  build, and diff whitespace checks pass.
- Final independent backend and frontend responsibility reviews report no
  unresolved MUST, SHOULD, or NICE findings.
