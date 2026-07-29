using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Formats the request-local runtime keys owned by one architecture adapter.</summary>
internal sealed class LtxRuntimeKeyScope(ArchitectureId architectureId)
{
    internal enum StageRefComponent
    {
        Media,
        Vae,
        Audio,
    }

    private string Prefix { get; } = BuildPrefix(architectureId);

    internal string StageRef(
        StageRefStore.StageKind kind,
        StageRefComponent component) =>
        $"{Prefix}.stage-ref.{StageName(kind)}.{ComponentName(component)}";

    internal string PreCoreNodeIds => $"{Prefix}.pre-core-node-ids";

    internal string ControlNetNormalized => $"{Prefix}.controlnet.normalized";

    internal string ControlNetFullImage(int index) =>
        $"{Prefix}.controlnet.fullimage.{index}";

    internal string ControlNetFrameCount(int index) =>
        $"{Prefix}.controlnet.framecount.{index}";

    internal string IcLoraAudioReference(
        int clipId,
        int entryIndex,
        int? rawStageIndex = null)
    {
        string key =
            $"{Prefix}.iclora.audio-reference.{clipId}.{entryIndex}";
        return rawStageIndex is int stageIndex
            ? $"{key}.{stageIndex}"
            : key;
    }

    internal string IcLoraControlSignal(int clipId, int entryIndex) =>
        $"{Prefix}.iclora.control.{clipId}.{entryIndex}";

    internal string IcLoraUploadedDriveImages(int clipId, int entryIndex) =>
        $"{Prefix}.iclora.upload.{clipId}.{entryIndex}";

    private static string BuildPrefix(ArchitectureId architectureId)
    {
        if (string.IsNullOrWhiteSpace(architectureId.Value))
        {
            throw new ArgumentException(
                "Architecture id cannot be empty.",
                nameof(architectureId));
        }
        return $"videostages.arch.{architectureId}";
    }

    private static string StageName(StageRefStore.StageKind kind) => kind switch
    {
        StageRefStore.StageKind.Base => "base",
        StageRefStore.StageKind.Refiner => "refiner",
        StageRefStore.StageKind.Generated => "generated",
        StageRefStore.StageKind.PreRootVideo => "preroot",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ComponentName(StageRefComponent component) => component switch
    {
        StageRefComponent.Media => "media",
        StageRefComponent.Vae => "vae",
        StageRefComponent.Audio => "audio",
        _ => throw new ArgumentOutOfRangeException(
            nameof(component),
            component,
            null),
    };
}
