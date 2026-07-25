namespace VideoStages;

internal static class ControlNetCaptureKeys
{
    private const string ImagePrefix = "videostages.controlnet.fullimage.";
    private const string FrameCountPrefix = "videostages.controlnet.framecount.";
    private const string AudioPrefix = "videostages.controlnet.audio.";

    public static string Image(int index) => $"{ImagePrefix}{index}";
    public static string FrameCount(int index) => $"{FrameCountPrefix}{index}";
    public static string Audio(int index) => $"{AudioPrefix}{index}";

    public static bool IsValidIndex(int index) => index is >= 0 and <= 2;
}
