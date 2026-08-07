namespace VideoStages.Architectures.Ltx2.Runtime;

internal static class LtxRuntimeKeyScope
{
    private static string Prefix { get; } =
        $"videostages.arch.{Ltx2ArchitectureModule.ArchitectureId}";

    internal static string StageRefMedia(StageRefStore.StageKind kind) =>
        $"{Prefix}.stage-ref.{StageName(kind)}.media";

    internal static string StageRefVae(StageRefStore.StageKind kind) =>
        $"{Prefix}.stage-ref.{StageName(kind)}.vae";

    internal static string StageRefAudio(StageRefStore.StageKind kind) =>
        $"{Prefix}.stage-ref.{StageName(kind)}.audio";

    internal static string ControlNetNormalized => $"{Prefix}.controlnet.normalized";

    internal static string ControlNetFullImage(int index) =>
        $"{Prefix}.controlnet.fullimage.{index}";

    internal static string ControlNetFrameCount(int index) =>
        $"{Prefix}.controlnet.framecount.{index}";

    internal static string IcLoraAudioReference(
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

    internal static string IcLoraControlSignal(
        int clipId,
        int entryIndex,
        int? rawStageIndex = null)
    {
        string key = $"{Prefix}.iclora.control.{clipId}.{entryIndex}";
        return rawStageIndex is int stageIndex
            ? $"{key}.{stageIndex}"
            : key;
    }

    internal static string IcLoraUploadedDriveImages(int clipId, int entryIndex) =>
        $"{Prefix}.iclora.upload.{clipId}.{entryIndex}";

    private static string StageName(StageRefStore.StageKind kind) => kind switch
    {
        StageRefStore.StageKind.Base => "base",
        StageRefStore.StageKind.Refiner => "refiner",
        StageRefStore.StageKind.Generated => "generated",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

}
