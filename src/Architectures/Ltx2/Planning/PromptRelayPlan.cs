using System.Collections.Immutable;

namespace VideoStages.Architectures.Ltx2.Planning;

internal enum PromptRelayMode
{
    None,
    SinglePromptOverride,
    Relay,
    RequiresRuntimeLength,
}

internal sealed record PromptWindowPlan(
    string Prompt,
    double StartSeconds,
    double DurationSeconds,
    double EndSeconds);

internal sealed record PromptRelaySegmentPlan(
    string Prompt,
    double Seconds);

internal sealed record PromptRelayPlan(
    PromptRelayMode Mode,
    ImmutableArray<PromptWindowPlan> AuthoredWindows,
    ImmutableArray<PromptRelaySegmentPlan> Segments);
