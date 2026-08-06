using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Execution;

internal sealed class PlannedStagePromptResolver(WorkflowGenerator g)
{
    public (string Positive, string Negative) Resolve(ClipPlan clip, StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);

        string positive = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negative = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositive = GetOriginal(
            g.UserInput,
            T2IParamTypes.Prompt.Type.ID,
            positive);
        string originalNegative = GetOriginal(
            g.UserInput,
            T2IParamTypes.NegativePrompt.Type.ID,
            negative);
        return (
            PromptText.ResolveForTarget(
                positive,
                originalPositive,
                clip.ClipId,
                stage.StageId,
                stage.ClipStageIndex),
            PromptText.ResolveForTarget(
                negative,
                originalNegative,
                clip.ClipId,
                stage.StageId,
                stage.ClipStageIndex));
    }

    private static string GetOriginal(T2IParamInput input, string paramId, string fallback)
    {
        if (input.ExtraMeta is not null
            && input.ExtraMeta.TryGetValue($"original_{paramId}", out object original)
            && original is string prompt)
        {
            return prompt;
        }
        return fallback ?? "";
    }
}
