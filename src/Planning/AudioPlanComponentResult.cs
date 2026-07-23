using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Output from one independently testable portion of audio planning.</summary>
internal sealed record AudioPlanComponentResult<TPlan>(
    TPlan Plan,
    ImmutableArray<AudioPlanDiagnostic> Diagnostics);
