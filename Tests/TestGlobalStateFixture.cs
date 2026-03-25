using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[CollectionDefinition("VideoStagesTests")]
public class VideoStagesTestsCollection : ICollectionFixture<GlobalStateFixture>
{
}

public sealed class GlobalStateFixture : IDisposable
{
    private readonly List<WorkflowGenerator.WorkflowGenStep> _workflowSteps;
    private readonly Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> _promptBasic;
    private readonly Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> _promptProcessors;
    private readonly Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> _promptPost;
    private readonly Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> _promptLength;

    public GlobalStateFixture()
    {
        _workflowSteps = [.. WorkflowGenerator.Steps];
        _promptBasic = new(T2IPromptHandling.PromptTagBasicProcessors);
        _promptProcessors = new(T2IPromptHandling.PromptTagProcessors);
        _promptPost = new(T2IPromptHandling.PromptTagPostProcessors);
        _promptLength = new(T2IPromptHandling.PromptTagLengthEstimators);
    }

    public void Dispose()
    {
        WorkflowGenerator.Steps = [.. _workflowSteps];
        T2IPromptHandling.PromptTagBasicProcessors = new(_promptBasic);
        T2IPromptHandling.PromptTagProcessors = new(_promptProcessors);
        T2IPromptHandling.PromptTagPostProcessors = new(_promptPost);
        T2IPromptHandling.PromptTagLengthEstimators = new(_promptLength);
    }
}
