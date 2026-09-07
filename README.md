# SwarmUI VideoStages

Take a video from first draft to finished clip in a few simple stages.

VideoStages adds a multi-step video flow to SwarmUI. Instead of asking one generation to do everything at once, you can build on your result stage by stage to improve motion, detail, and overall polish while keeping the whole process in one place. If your workflow also creates audio, VideoStages automatically carries that into the finished video too, including audio from AceStep and, soon, Qwen-TTS.

Think of it as draft, refine, and polish for video, built right into the normal SwarmUI experience.

# The clip editor

Open **VideoStages** in SwarmUI's bottom bar. The clip strip on the left shows generation order, source relationships, and output duration. Select a clip to edit its continuous sections on the right. Section navigation and sticky headings keep your place while scrolling through stages, references, audio, and output settings. The enable switch and the VideoStages group toggle control the same state; a checkmark on the tab shows when it is active.

Edits use SwarmUI's existing parameters and prompt box and are included in your normal generation. Use a clip's action menu to move it earlier or later in the sequence.

## Clips and stages

A **clip** is one piece of video with its own duration, model, prompt, and options. A **stage** is one generation pass over that clip. Add stages to go draft → refine → polish: each stage has its own model, steps, sampler, and Control value (how much of the previous result it is allowed to redo). Stages can be skipped without deleting them.

All the stages in one clip must use the same model family. Switching a clip to another family is an explicit, undoable conversion that tells you up front which settings it has to drop.

A timeline can hold as many clips as you like, each with its own model and settings.

## Joins between clips

Every gap between two clips is a **join**. Click its button in the clip strip or open **Join from previous clip**:

- **Cut** — a hard splice.
- **Continue** — the next clip picks up from the tail of the previous one, with a configurable overlap.
- **Crossfade** — the two clips blend across the overlap.

You can also carry the outgoing clip's audio tail across a non-cut join. Joins between clips using different model families are always cuts.

## Starting material

A clip can start from nothing (text to video), from an image, from an uploaded **source video**, or—on Clip 1 and later—from the previous clip's output. Source footage is conformed for you—resampled to the timeline frame rate, trimmed to the clip's length, and scaled to the timeline resolution—and can then be refined by stages or left alone as plain footage on the timeline.

There is also a global **Refine Video** action for taking one finished video back through the timeline.

## Prompts

Each clip has a prompt in **Prompt & clip** and, where supported, **Relay prompts** with editable start and end times. These let the description change as the shot progresses. The `<videoclip>` prompt syntax documented below also works.

## Retake windows

A **retake** regenerates only a chosen frame range of an existing video and leaves the rest untouched — useful for fixing one bad moment without redoing the shot. Retakes need a source video (or the global Refine Video source).

## Keyframes

The **Keyframes** section pins images to specific frames of a clip: a first keyframe to steer where the shot starts, a final keyframe to steer where it lands, or intermediate frames where the model supports them. Upload or select media, or drop files onto **Add keyframe**. **References** provides model-supported image, video, and audio conditioning; its row labels show the tags available in prompts.

## Audio

The **Audio** section selects the clip's audio source, such as native model audio, an upload, AceStep, or a supported control source. A clip can take its length from its audio. Uploaded media has a compact preview; open it to play the selected range, or use **Edit…** beside the range summary to trim it.

## LoRAs and IC-LoRAs

The **LoRAs** section lists the clip's models and weights. **IC-LoRAs** are the control-style adapters: pick one of the curated presets (union control, motion tracking, in/outpainting, lip sync, spatial upscalers, deblur, colorization, restyle, and more) or choose Custom and point it at your own weights, then choose what drives it — an upload you supply or media already entering a stage.

## Upscaling

Each stage can upscale in one of four ways — pixel, model, latent, or latent+model — so a polish stage can raise resolution without a separate workflow.

## Resolution and frame rate

Resolution and frame rate follow SwarmUI's core video parameters by default. The dimensions/FPS button above the clip strip opens **Output settings** to adjust them.

# Prompt syntax

VideoStages adds a `<videoclip>` prompt section that lets you target every clip, a single clip, or a single stage of a single clip. LoRAs placed inside a `<videoclip>` section are scoped to that same target.

| Tag                         | Applies to                                       |
|-----------------------------|--------------------------------------------------|
| `<videoclip>`               | All clips and all stages                         |
| `<videoclip[clip]>`         | Every stage of the specified clip                |
| `<videoclip[clip,stage]>`   | Only the specified stage of the specified clip   |

`clip` and `stage` are zero-based indices. `stage` is the stage's position within its clip, not a global stage number.

## How the prompt is built for each stage

For a given clip and stage, VideoStages walks the `<videoclip*>` tiers from most-specific to least-specific and **concatenates** the text of every tier that matches:

1. `<videoclip[clip,stage]>` — exact stage match
2. `<videoclip[clip]>` — same clip, any stage
3. `<videoclip>` — applies to every clip

Tiers that don't match (e.g. `<videoclip[2]>` when rendering clip 0) contribute nothing. A tier whose body is only tags such as `<lora:...>` contributes no text but still scopes its LoRAs to that tier's target.

If the concatenated `<videoclip*>` text is empty, VideoStages falls back — this part is **replacement**, not additive — to:

4. `<video>` — the stock SwarmUI video section
5. Global prompt — text outside any tagged section

Only the first fallback that has text is used; once `<video>` provides text, the global prompt is ignored, and vice versa.

## Example

```
A serene mountain lake at dawn
<video>cinematic, slow camera push-in, volumetric fog
<videoclip><lora:my-style:0.8>
<videoclip[1]>shot on 35mm film, golden-hour color grade
<videoclip[1,0]>wide establishing shot
```

| Render target       | Resulting prompt                                                              | Notes                                                                                       |
|---------------------|-------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------|
| Clip 0, any stage   | `cinematic, slow camera push-in, volumetric fog`                              | `<videoclip>` is LoRA-only and clip 0 has no other tiers, so the chain falls to `<video>`.  |
| Clip 1, stage 0     | `shot on 35mm film, golden-hour color grade wide establishing shot`           | `<videoclip[1]>` and `<videoclip[1,0]>` both match and are concatenated.                    |
| Clip 1, stage 1+    | `shot on 35mm film, golden-hour color grade`                                  | Only `<videoclip[1]>` matches; `<videoclip[1,0]>` is filtered out.                          |

The `<lora:my-style:0.8>` under bare `<videoclip>` is loaded for every clip regardless of which fallback supplies the text. The global line (`A serene mountain lake at dawn`) is never used here because `<video>` already supplies text for clip 0 and the `<videoclip[1]*>` tiers supply text for clip 1.

# Development

## Browser testing

See [the VideoStages browser guide](docs/BROWSER_TESTING.md) for verified
editing recipes, locator rules, and the boundary against submitting generation.

Use the global Playwright CLI for exploratory testing. Keep the commands in one
terminal session so its browser daemon remains alive:

```bash
scripts/videostages-browser open http://127.0.0.1:7801/Text2Image --browser=chromium
scripts/videostages-browser click "getByTestId('clip-editor-tab')"
scripts/videostages-browser snapshot
scripts/videostages-browser close
```

Use the stable tab locator above; generated element refs come from the latest snapshot.

The repeatable browser suite expects SwarmUI at `http://127.0.0.1:7801`.
Override it with `SWARMUI_URL` when needed:

```bash
source ~/.nvm/nvm.sh
npx playwright install chromium
npm run test:browser
```

Run `npm run test:browser:headed` from a graphical session for an interactive
browser. Failures keep a screenshot, video, and Playwright trace under
`test-results/`.

Architecture maps:

- [`docs/ARCHITECTURE_FLOW.md`](docs/ARCHITECTURE_FLOW.md) — start-to-finish
  model/catalog/frontend/backend flow;
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — backend ownership and invariants;
- [`FRONTEND_ARCHITECTURE.md`](FRONTEND_ARCHITECTURE.md) — authoring state,
  catalog policy, and UI composition; and
- [`docs/STAGE_RUNTIME.md`](docs/STAGE_RUNTIME.md) — prepared request state,
  workflow priorities, runtime lifetimes, stage engines, and generated-binding
  retention.

## Use ComfyTyped

### Generate node definitions with ComfyTyped
```
cd /path/to/ComfyTyped
dotnet build -c Release ComfyTyped.csproj
cp bin/Release/net8.0/ComfyTyped.dll \
    ../SwarmUI-VideoStages/lib/ComfyTyped.dll

dotnet run --project tools/ComfyTyped.CodeGen -- \
    --comfy-json http://127.0.0.1:7801/ComfyBackendDirect/api/object_info \
    --output ../SwarmUI-VideoStages/src/Generated \
    --namespace VideoStages.Generated \
    --keep-list ../SwarmUI-VideoStages/comfytyped.keep.json \
    --core-assembly ../SwarmUI-VideoStages/lib/ComfyTyped.dll
```

### Once ready to commit, prune unused node definitions
```
cd /path/to/ComfyTyped
dotnet run --project tools/ComfyTyped.CodeGen -- prune \
    --generated-dir ../SwarmUI-VideoStages/src/Generated \
    --source ../SwarmUI-VideoStages/src
```

`comfytyped.keep.json` and direct production references are both inputs to that
prune. After regenerating or pruning, run `./run-tests`; the generated-binding
retention test verifies every manifest entry still names a unique generated
node binding. See
[`docs/STAGE_RUNTIME.md`](docs/STAGE_RUNTIME.md#9-generated-binding-retention-audit)
for the distinction between code-generation pruning and .NET linker trimming.
