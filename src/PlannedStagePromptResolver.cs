using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Resolves one compiled clip/stage prompt against the host's original prompt text.</summary>
internal sealed class PlannedStagePromptResolver(WorkflowGenerator g)
{
    public (string Positive, string Negative) Resolve(ClipPlan clip, StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);

        string positive = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negative = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositive = PromptParser.GetOriginalPrompt(
            g.UserInput,
            T2IParamTypes.Prompt.Type.ID,
            positive);
        string originalNegative = PromptParser.GetOriginalPrompt(
            g.UserInput,
            T2IParamTypes.NegativePrompt.Type.ID,
            negative);
        return (
            PromptParser.ExtractPrompt(
                positive,
                originalPositive,
                clip.ClipId,
                stage.StageId,
                stage.ClipStageIndex),
            PromptParser.ExtractPrompt(
                negative,
                originalNegative,
                clip.ClipId,
                stage.StageId,
                stage.ClipStageIndex));
    }
}
