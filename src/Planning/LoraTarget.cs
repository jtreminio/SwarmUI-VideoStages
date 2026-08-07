namespace VideoStages.Planning;

/// <summary>
/// Declares which host targets make a normal LoRA row effective.
/// </summary>
internal enum LoraTarget
{
    ModelAndTextEncoder,
    ModelOnly,
}
