using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Resolves typed guide plans and owns the stage-output reference state for one sequence run.
/// </summary>
internal sealed class StageGuideReferenceState(
    WorkflowGenerator g,
    StageRefStore store,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs,
    LtxManager ltxManager)
{
    private readonly Dictionary<int, StageRefStore.StageRef> _stageOutputs = [];
    private StageRefStore.StageRef _previousStageRef;

    public void Reset()
    {
        BeginClip();
    }

    /// <summary>
    /// Starts a new clip-local guide namespace. Authored Stage&lt;N&gt; selectors use the stage
    /// number shown within their clip, so outputs from an earlier clip must never satisfy them.
    /// </summary>
    public void BeginClip()
    {
        _stageOutputs.Clear();
        _previousStageRef = null;
    }

    public StageRefStore.StageRef Resolve(StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Guide.Kind switch
        {
            GuideReferenceKind.Base => WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists."),
            GuideReferenceKind.Refiner => WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists."),
            GuideReferenceKind.Generated => _previousStageRef ?? WarnIfMissing(
                store.Generated,
                "VideoStages: ImageReference 'Generated' requested, but no generated reference exists."),
            GuideReferenceKind.PreviousStage => ResolvePreviousStageReference(),
            GuideReferenceKind.ExplicitStage => ResolveExplicitStageReference(stage.Guide),
            GuideReferenceKind.Base2Edit => ResolveBase2EditReference(stage.Guide),
            _ => WarnUnknownGuideReference(stage.Guide.RawValue),
        };
    }

    public void CaptureStageOutput(StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        StageRefStore.StageRef captured = new(referenceMedia, referenceVae);
        _stageOutputs[stage.ClipStageIndex] = captured;
        _previousStageRef = captured;
    }

    private StageRefStore.StageRef ResolvePreviousStageReference()
    {
        if (_previousStageRef is null)
        {
            Logs.Warning("VideoStages: ImageReference 'PreviousStage' cannot be used for the first stage.");
        }
        return _previousStageRef;
    }

    private StageRefStore.StageRef ResolveExplicitStageReference(GuideReferencePlan guide)
    {
        int? stageIndex = guide.ReferencedStageIndex;
        if (stageIndex is int index
            && _stageOutputs.TryGetValue(index, out StageRefStore.StageRef reference))
        {
            return reference;
        }
        Logs.Warning(
            $"VideoStages: ImageReference '{guide.RawValue}' requested, but stage {stageIndex} does not exist.");
        return null;
    }

    private StageRefStore.StageRef ResolveBase2EditReference(GuideReferencePlan guide)
    {
        int? stageIndex = guide.ReferencedStageIndex;
        if (stageIndex is int index
            && base2EditPublishedStageRefs.TryGetStageRef(index, out StageRefStore.StageRef reference))
        {
            return reference;
        }
        Logs.Warning(
            $"VideoStages: ImageReference '{guide.RawValue}' requested, but Base2Edit stage {stageIndex} does not exist.");
        return null;
    }

    private static StageRefStore.StageRef WarnUnknownGuideReference(string rawValue)
    {
        Logs.Warning($"VideoStages: Unknown ImageReference value '{rawValue}'.");
        return null;
    }

    private static StageRefStore.StageRef WarnIfMissing(StageRefStore.StageRef reference, string message)
    {
        if (reference is null)
        {
            Logs.Warning(message);
        }
        return reference;
    }
}
