namespace VideoStages.Planning;

/// <summary>
/// Declares which host targets make a normal LoRA row effective. Architecture runtimes still
/// choose the concrete host loader.
/// </summary>
internal enum LoraTarget
{
    ModelAndTextEncoder,
    ModelOnly,
}
