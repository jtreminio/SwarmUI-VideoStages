namespace VideoStages;

internal static class ControlNetCaptureKeys
{
    private const string ImagePrefix = "videostages.controlnet.fullimage.";
    private const string AudioPrefix = "videostages.controlnet.audio.";
    private const string ApplyPrefix = "videostages.controlnet.apply.";

    public static string Image(int index) => $"{ImagePrefix}{index}";
    public static string Audio(int index) => $"{AudioPrefix}{index}";
    public static string Apply(int index) => $"{ApplyPrefix}{index}";

    public static bool IsValidIndex(int index) => index is >= 0 and <= 2;
}
