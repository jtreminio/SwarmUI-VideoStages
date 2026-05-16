# SwarmUI VideoStages

Take a video from first draft to finished clip in a few simple stages.

VideoStages adds a multi-step video flow to SwarmUI. Instead of asking one generation to do everything at once, you can build on your result stage by stage to improve motion, detail, and overall polish while keeping the whole process in one place. If your workflow also creates audio, VideoStages automatically carries that into the finished video too, including audio from AceStep and, soon, Qwen-TTS.

Think of it as draft, refine, and polish for video, built right into the normal SwarmUI experience.

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
    --core-assembly ../SwarmUI-VideoStages/lib/ComfyTyped.dll
```

### Once ready to commit, prune unused node definitions
```
cd /path/to/ComfyTyped
dotnet run --project tools/ComfyTyped.CodeGen -- prune \
    --generated-dir ../SwarmUI-VideoStages/src/Generated \
    --source ../SwarmUI-VideoStages/src
```
