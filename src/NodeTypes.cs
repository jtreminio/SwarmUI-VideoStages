namespace VideoStages;

internal static class NodeTypes
{
    public const string VAEDecode = "VAEDecode";
    public const string VAEDecodeTiled = "VAEDecodeTiled";
    public const string VAEEncode = "VAEEncode";
    public const string VAEEncodeTiled = "VAEEncodeTiled";
    public const string VAEDecodeAudio = "VAEDecodeAudio";
    public const string SaveImage = "SaveImage";
    public const string SwarmSaveImageWS = "SwarmSaveImageWS";
    public const string SwarmSaveAudioWS = "SwarmSaveAudioWS";
    public const string SwarmSaveAnimationWS = "SwarmSaveAnimationWS";
    public const string ImageBatch = "ImageBatch";
    public const string BatchImagesNode = "BatchImagesNode";
    public const string AudioConcat = "AudioConcat";
    public const string ImageScale = "ImageScale";
    public const string UpscaleModelLoader = "UpscaleModelLoader";
    public const string ImageUpscaleWithModel = "ImageUpscaleWithModel";
    public const string LatentUpscaleBy = "LatentUpscaleBy";
    public const string LatentUpscaleModelLoader = "LatentUpscaleModelLoader";
    public const string SwarmLoadAudioB64 = "SwarmLoadAudioB64";
    public const string SwarmEnsureAudio = "SwarmEnsureAudio";
    public const string AudioLengthToFrames = "SwarmAudioLengthToFrames";
    public const string SolidMask = "SolidMask";
    public const string SetLatentNoiseMask = "SetLatentNoiseMask";
    public const string SwarmClipTextEncodeAdvanced = "SwarmClipTextEncodeAdvanced";
}
