using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class VideoStagesPromptSection
{
    public const string Prefix = "videostages";
    private const string Opener = "<videostages:";

    private static string OriginalPositivePrompt(WorkflowGenerator g)
    {
        T2IParamInput input = g.UserInput;
        if (input.ExtraMeta is not null
            && input.ExtraMeta.TryGetValue($"original_{T2IParamTypes.Prompt.Type.ID}", out object original)
            && original is string originalPrompt)
        {
            return originalPrompt;
        }
        return input.Get(T2IParamTypes.Prompt, "");
    }

    public static string ExtractJson(WorkflowGenerator g) => ExtractJson(OriginalPositivePrompt(g));

    public static string ExtractJson(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return null;
        }
        int start = prompt.IndexOf(Opener, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        int dataStart = start + Opener.Length;
        int end = prompt.IndexOf('>', dataStart);
        return end < 0 ? null : prompt[dataStart..end];
    }

    public static bool IsPresent(WorkflowGenerator g) => ExtractJson(g) is not null;

    public static string StripSection(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return "";
        }
        int start = prompt.IndexOf(Opener, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return prompt.Trim();
        }
        int end = prompt.IndexOf('>', start + Opener.Length);
        if (end < 0)
        {
            return prompt.Trim();
        }
        return (prompt[..start] + prompt[(end + 1)..]).Trim();
    }
}
