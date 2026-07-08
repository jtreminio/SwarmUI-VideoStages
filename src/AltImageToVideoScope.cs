using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

internal static class AltImageToVideoScope
{
    private static readonly ConditionalWeakTable<WorkflowGenerator.ImageToVideoGenInfo, Bucket> _table = [];
    private static int _registered;

    public static void RegisterDispatcher()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(static g => Fire(g));
    }

    public static IDisposable Post(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
    {
        ArgumentNullException.ThrowIfNull(expectedGenInfo);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _registered) == 0)
        {
            throw new SwarmReadableErrorException(
                "AltImageToVideoScope.RegisterDispatcher() must be called before Post.");
        }

        Bucket bucket = _table.GetValue(expectedGenInfo, static _ => new Bucket());
        lock (bucket.Gate)
        {
            bucket.Post = bucket.Post.Add(handler);
        }

        return new Detach(bucket, handler);
    }

    private static void Fire(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (genInfo is null || !_table.TryGetValue(genInfo, out Bucket bucket))
        {
            return;
        }

        ImmutableList<Action<WorkflowGenerator.ImageToVideoGenInfo>> snapshot;
        lock (bucket.Gate)
        {
            snapshot = bucket.Post;
        }

        foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in snapshot)
        {
            handler(genInfo);
        }
    }

    private sealed class Bucket
    {
        public readonly object Gate = new();
        public ImmutableList<Action<WorkflowGenerator.ImageToVideoGenInfo>> Post = [];
    }

    private sealed class Detach(Bucket bucket, Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (bucket.Gate)
            {
                bucket.Post = bucket.Post.Remove(handler);
            }
        }
    }
}
