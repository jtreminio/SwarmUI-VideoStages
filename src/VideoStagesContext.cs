using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    // Prompt-tag section resolution has no live WorkflowGenerator to key the cache on, so repeated
    // videoclip[clip,stage] lookups in one generation share the parse via the stable param input.
    private static readonly ConditionalWeakTable<T2IParamInput, VideoStagesSpec> PromptParseCache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, VideoStagesSpecParser.Parse);

    public static VideoStagesSpec GetVideoStagesSpecForPromptParse(T2IParamInput input) =>
        PromptParseCache.GetValue(input, ParseForPromptTag);

    private static VideoStagesSpec ParseForPromptTag(T2IParamInput input)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/"
        };
        return VideoStagesSpecParser.Parse(generator);
    }
}
