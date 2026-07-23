namespace VideoStages.Planning;

/// <summary>Identifies the IC-LoRA entries that require final HDR publication.</summary>
internal static class HdrIcLoraPolicy
{
    internal static bool IsActive(ClipPlan clip) => clip.Stages.Any(stage =>
        stage.IcLoras.Any(IsHdr));

    private static bool IsHdr(IcLoraPlan entry) =>
        StringUtils.Equals(entry.Preset?.Trim(), "hdr")
        || (entry.ModelName?.Contains(
            "ic-lora-hdr",
            StringComparison.OrdinalIgnoreCase) ?? false);
}
