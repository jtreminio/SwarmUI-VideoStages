namespace VideoStages.LTX2;

internal static class LtxNodeTypes
{
    public const string LTXVSeparateAVLatent = "LTXVSeparateAVLatent";
    public const string LTXVConcatAVLatent = "LTXVConcatAVLatent";
    public const string LTXVEmptyLatentAudio = "LTXVEmptyLatentAudio";
    public const string EmptyLTXVLatentVideo = "EmptyLTXVLatentVideo";
    public const string LTXVAudioVAEDecode = "LTXVAudioVAEDecode";
    public const string LTXVPreprocess = "LTXVPreprocess";
    public const string LTXVImgToVideoInplace = "LTXVImgToVideoInplace";
    public const string LTXVAddGuide = "LTXVAddGuide";
    public const string LTXAddVideoICLoRAGuide = "LTXAddVideoICLoRAGuide";
    public const string LTXICLoRALoaderModelOnly = "LTXICLoRALoaderModelOnly";
    public const string LTXVCropGuides = "LTXVCropGuides";
    public const string LTXVLatentUpsampler = "LTXVLatentUpsampler";
    public const string LTXVConditioning = "LTXVConditioning";
    public const string LTXVAudioVAEEncode = "LTXVAudioVAEEncode";
}
