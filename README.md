# SwarmUI VideoStages

Take a video from first draft to finished clip in a few simple stages.

VideoStages adds a multi-step video flow to SwarmUI. Instead of asking one generation to do everything at once, you can build on your result stage by stage to improve motion, detail, and overall polish while keeping the whole process in one place. If your workflow also creates audio, VideoStages automatically carries that into the finished video too, including audio from AceStep and, soon, Qwen-TTS.

Think of it as draft, refine, and polish for video, built right into the normal SwarmUI experience.

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

## Use ComfyTyped

### Generate node definitions with ComfyTyped
```
cd /path/to/ComfyTyped
dotnet build -c Release ComfyTyped.csproj
cp bin/Release/net8.0/ComfyTyped.dll \
    ../SwarmUI-VideoStages/lib/ComfyTyped.dll

dotnet run --project tools/ComfyTyped.CodeGen -- \
    --comfy-json http://192.0.0.1:7801/ComfyBackendDirect/api/object_info \
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
