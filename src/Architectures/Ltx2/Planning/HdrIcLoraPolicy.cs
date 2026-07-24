using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Identifies the LTX IC-LoRA entries that require final HDR publication.</summary>
internal static class HdrIcLoraPolicy
{
    internal static bool IsActive(ClipPlan clip) => clip.Stages.Any(stage =>
        stage.ArchitecturePayload is Ltx2StagePayload payload
        && payload.IcLoras.Any(IsHdr));

    /// <summary>Reads the persisted typed contract; never a preset or weight name.</summary>
    private static bool IsHdr(IcLoraPlan entry) => entry.IsHdr;
}
