namespace VideoStages;

internal static class NodeTypes
{
    public const string VAEDecode = "VAEDecode";
    public const string VAEDecodeTiled = "VAEDecodeTiled";
    public const string VAEEncode = "VAEEncode";
    public const string CLIPTextEncode = "CLIPTextEncode";
    public const string VAEDecodeAudio = "VAEDecodeAudio";
    public const string SaveImage = "SaveImage";
    public const string SwarmSaveImageWS = "SwarmSaveImageWS";
    public const string SwarmSaveAudioWS = "SwarmSaveAudioWS";
    public const string SwarmSaveAnimationWS = "SwarmSaveAnimationWS";
    public const string ImageScale = "ImageScale";
    public const string UpscaleModelLoader = "UpscaleModelLoader";
    public const string ImageUpscaleWithModel = "ImageUpscaleWithModel";
    public const string LatentUpscaleBy = "LatentUpscaleBy";
    public const string LatentUpscaleModelLoader = "LatentUpscaleModelLoader";
    public const string LTXVSeparateAVLatent = "LTXVSeparateAVLatent";
    public const string LTXVConcatAVLatent = "LTXVConcatAVLatent";
    public const string LTXVEmptyLatentAudio = "LTXVEmptyLatentAudio";
    public const string EmptyLTXVLatentVideo = "EmptyLTXVLatentVideo";
    public const string LTXVAudioVAEDecode = "LTXVAudioVAEDecode";
    public const string LTXVPreprocess = "LTXVPreprocess";
    public const string LTXVAddGuide = "LTXVAddGuide";
    public const string LTXVImgToVideoInplace = "LTXVImgToVideoInplace";
    public const string LTXVCropGuides = "LTXVCropGuides";
    public const string LTXVLatentUpsampler = "LTXVLatentUpsampler";
    public const string LTXVConditioning = "LTXVConditioning";
    public const string AudioLengthToFrames = "SwarmAudioLengthToFrames";
    public const string SolidMask = "SolidMask";
    public const string SetLatentNoiseMask = "SetLatentNoiseMask";
    public const string SwarmClipTextEncodeAdvanced = "SwarmClipTextEncodeAdvanced";
}
