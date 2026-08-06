namespace VideoStages.Planning;

/// <summary>
/// One persisted clip- or stage-authored LoRA row after per-stage weight resolution.
/// Architecture payloads own the resulting immutable arrays.
/// </summary>
internal sealed record LoraPlan(
    string Name,
    double ModelWeight,
    double TextEncoderWeight);
