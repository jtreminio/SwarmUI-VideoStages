# VideoStages architecture

VideoStages supports one execution family: an LTX-Video timeline. WAN and mixed-model
timelines are rejected before VideoStages mutates the workflow graph.

## The execution model

Every run follows the same five-part pipeline:

1. `VideoStagesSpecParser` reads the saved timeline.
2. `VideoExecutionPlanCompiler` turns it into an immutable `VideoExecutionPlan`.
3. `StageSequenceRunner` walks the planned clips and asks `StageRunner` to execute each
   planned LTX stage.
4. `TimelineAssembler` applies the planned cut, continue, and crossfade boundaries.
5. `RootRuntimeSession` publishes the final artifact and removes only the displaced root
   nodes it owns.

The apparent entry points are inputs to that pipeline, not separate executors:

- Text to image to video starts with the host image as clip-zero media.
- Text to video starts the generated clip from an empty LTX latent and replaces the host
  text-to-video root.
- An init image starts clip zero from the supplied image.
- An init video or sourced clip starts from installed footage.
- A global refine video replaces the host root before clip execution.

Single-clip and multi-clip, single-stage and multi-stage combinations all use the same
runner. Their differences are expressed in `ClipPlan`, `StagePlan`, `BoundaryPlan`, and
`StageExecutionOptions`.

## Option ownership

- `StagePlan` owns model settings, prompt relay, LoRAs, IC-LoRAs, retakes, frame
  references, upscaling, and stage-output policy.
- `AudioPlan` owns the base source, duration owner, segments, voice reference, and reuse
  policy for one clip.
- `AudioTimelinePlan` projects clip audio and authored track spans onto the final timeline,
  including spans that cross one or more clips. Pending or provisional spans remain atomic
  until their timing can be resolved; they are never partially mixed.
- `AudioTimelineExecutor` resolves runtime audio sources once and executes the planned
  per-clip source, length, segment, and conditioning decisions.
- `TimelineAssemblySession` owns runtime boundary degradation. For example, a planned
  continue boundary becomes an explicit cut when its continuity artifact cannot be built.

## Runtime invariants

- An active run must have a valid LTX `VideoExecutionPlan`.
- Every requested stage returns a valid `RuntimeArtifact` or the run fails.
- A multi-clip assembly receives exactly one valid artifact per planned clip.
- Source installation, model resolution, stage execution, assembly, and final publication
  fail closed; prior host media is never silently presented as a successful result.
- Intermediate publications stay attached to their stage artifacts.
- Only the final publisher may advance the captured host save nodes.
- HDR conversion receives the exact final save IDs and cannot rewrite unrelated or
  intermediate publications.

## Compatibility boundary

`StageSpec` and `ClipSpec` remain parser/input models. They do not select an alternate
execution engine. There is no StageSpec adapter, native stage fallback, WAN dispatch, or
distributed save-retarget path.
