# VideoStages architecture

VideoStages supports one execution family: an LTX-Video timeline. WAN and mixed-model
timelines are rejected before VideoStages mutates the workflow graph.

## The execution model

Every run follows the same five-part pipeline:

1. `VideoStagesSpecParser` coordinates focused JSON, clip, stage, and resource parsers.
2. `VideoExecutionPlanCompiler` composes pure root, clip, stage-option, boundary, and audio
   planners into an immutable `VideoExecutionPlan`.
3. `StageSequenceRunner` walks planned clips; `StageClipExecutor` and `StageRunner`
   coordinate focused source, prompt, upscale, IC-LoRA, conditioning, latent, sampler, and
   output services.
4. `TimelineAssembler` applies the same typed cut, continue, and crossfade windows used by
   audio planning; runtime mismatches explicitly degrade to cuts.
5. `GlobalVideoFrameTrimmer` trims the completed single- or multi-clip timeline once and
   trims decoded attached audio to the same frame-derived time window, then
   `RootRuntimeSession` publishes it and removes only the displaced root nodes it owns.

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
  per-clip source, length, segment, and conditioning decisions, including sourced clips
  that intentionally have no generation stages.
- Timeline-wide authored tracks are a planning contract today. The runtime does not
  partially execute an unresolved or provisional cross-clip span; a future mixer can consume
  the plan without changing clip or stage execution.
- `TimelineAssemblySession` owns runtime boundary degradation. For example, a planned
  continue boundary becomes an explicit cut when its continuity artifact cannot be built.
- Latent upscales may chain. Once a clip has entered a latent-upscaled resolution, a later
  pixel/model upscale is deliberately skipped to avoid a decode-resize-reencode round trip.

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

`StageSpec` and `ClipSpec` are parser/compiler input models only. Active LTX root, stage,
source, prompt, reference, IC-LoRA, ControlNet, retake, audio, boundary, trim, HDR, and
publication execution consumes typed plans. There is no StageSpec adapter, native stage
fallback, WAN dispatch, or distributed save-retarget path.
