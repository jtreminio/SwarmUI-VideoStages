using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class AltImageToVideoScope
{
    public static IDisposable Post(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
    {
        Action<WorkflowGenerator.ImageToVideoGenInfo> scoped = WrapWithIdentityCheck(expectedGenInfo, handler);
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(scoped);
        return new Removal(() => WorkflowGenerator.AltImageToVideoPostHandlers.Remove(scoped));
    }

    public static IDisposable Pre(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
    {
        Action<WorkflowGenerator.ImageToVideoGenInfo> scoped = WrapWithIdentityCheck(expectedGenInfo, handler);
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(scoped);
        return new Removal(() => WorkflowGenerator.AltImageToVideoPreHandlers.Remove(scoped));
    }

    private static Action<WorkflowGenerator.ImageToVideoGenInfo> WrapWithIdentityCheck(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
    {
        return current =>
        {
            if (ReferenceEquals(current, expectedGenInfo))
            {
                handler(current);
            }
        };
    }

    private sealed class Removal(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
