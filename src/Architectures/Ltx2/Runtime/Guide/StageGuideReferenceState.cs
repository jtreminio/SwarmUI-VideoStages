using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class StageGuideReferenceState(
    WorkflowGenerator g,
    StageRefStore store,
    RootPlan root)
{
    private readonly Dictionary<int, StageRefStore.StageRef> _stageOutputs = [];
    private StageRefStore.StageRef _previousStageRef;

    /// <summary>
    /// Prevents Stage&lt;N&gt; selectors from resolving outputs captured for an earlier clip.
    /// </summary>
    public void BeginClip()
    {
        _stageOutputs.Clear();
        _previousStageRef = null;
    }

    public StageRefStore.StageRef Resolve(StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        return payload.Guide.Kind switch
        {
            StageGuideReferenceKind.Base => WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists."),
            StageGuideReferenceKind.Refiner => WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists."),
            // When a stage takes over core's text root there is deliberately no host generation to reference,
            // and every stage's ImageReference is rewritten to Generated on such a request — so a
            // miss here is the intended state for the whole timeline, not something to report.
            StageGuideReferenceKind.Generated => _previousStageRef
                ?? (root.IgnoresTextToVideoRoot
                    ? null
                    : WarnIfMissing(
                        store.Generated,
                        "VideoStages: ImageReference 'Generated' requested, but no generated "
                            + "reference exists.")),
            StageGuideReferenceKind.PreviousStage => _previousStageRef,
            StageGuideReferenceKind.ExplicitStage =>
                _stageOutputs.GetValueOrDefault(payload.Guide.ReferencedStageIndex!.Value),
            StageGuideReferenceKind.Base2Edit => ResolveBase2EditReference(payload.Guide),
            _ => null,
        };
    }

    public void CaptureStageOutput(StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        StageRefStore.StageRef captured = store.CaptureCurrentOutputReference();
        _stageOutputs[stage.ClipStageIndex] = captured;
        _previousStageRef = captured;
    }

    private StageRefStore.StageRef ResolveBase2EditReference(GuideReferencePlan guide)
    {
        int? stageIndex = guide.ReferencedStageIndex;
        if (stageIndex is int index
            && store.TryGetBase2EditStageRef(index, out StageRefStore.StageRef reference))
        {
            return reference;
        }
        RequestWarnings.Track(
            g.UserInput,
            $"VideoStages: ImageReference '{guide.RawValue}' requested, but Base2Edit stage {stageIndex} does not exist.");
        return null;
    }

    private StageRefStore.StageRef WarnIfMissing(
        StageRefStore.StageRef reference,
        string message)
    {
        if (reference is null)
        {
            RequestWarnings.Track(g.UserInput, message);
        }
        return reference;
    }
}
