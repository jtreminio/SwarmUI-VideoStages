namespace VideoStages.LTX2;

/// <summary>
/// String constants for LTX node <c>class_type</c> values where we genuinely need the string —
/// <c>g.RunOnNodesOfClass</c> dispatch in <c>LtxFrameCountConnector</c> and
/// <c>media.SourceNodeData</c> comparisons. Other former entries have moved to typed bindings.
/// </summary>
internal static class LtxNodeTypes
{
    public const string EmptyLTXVLatentVideo = "EmptyLTXVLatentVideo";
    public const string LTXVEmptyLatentAudio = "LTXVEmptyLatentAudio";
    public const string LTXVConcatAVLatent = "LTXVConcatAVLatent";

    // Used by VideoStagesExtension.OnInit to register feature-flag mappings —
    // ComfyUIBackendExtension.NodeToFeatureMap is keyed by class_type strings.
    public const string LTXICLoRALoaderModelOnly = "LTXICLoRALoaderModelOnly";
    public const string LTXAddVideoICLoRAGuide = "LTXAddVideoICLoRAGuide";
}
