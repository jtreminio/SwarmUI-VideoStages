namespace VideoStages.Planning;

/// <summary>
/// Declares which host targets make a normal LoRA row effective. This is a graph-free planning
/// concern: architecture runtimes still choose the concrete host loader.
/// </summary>
internal enum NormalLoraTargetPolicy
{
    ModelAndTextEncoder,
    ModelOnly,
}
