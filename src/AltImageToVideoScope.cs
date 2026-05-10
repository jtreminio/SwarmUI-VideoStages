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
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(static g => Fire(g, isPre: true));
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(static g => Fire(g, isPre: false));
    }

    public static IDisposable Pre(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
        => Attach(expectedGenInfo, handler, isPre: true);

    public static IDisposable Post(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
        => Attach(expectedGenInfo, handler, isPre: false);

    private static IDisposable Attach(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler,
        bool isPre)
    {
        ArgumentNullException.ThrowIfNull(expectedGenInfo);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _registered) == 0)
        {
            throw new SwarmReadableErrorException(
                "AltImageToVideoScope.RegisterDispatcher() must be called before Pre/Post.");
        }

        Bucket bucket = _table.GetValue(expectedGenInfo, static _ => new Bucket());
        lock (bucket.Gate)
        {
            if (isPre)
            {
                bucket.Pre = bucket.Pre.Add(handler);
            }
            else
            {
                bucket.Post = bucket.Post.Add(handler);
            }
        }

        return new Detach(bucket, handler, isPre);
    }

    private static void Fire(WorkflowGenerator.ImageToVideoGenInfo genInfo, bool isPre)
    {
        if (genInfo is null || !_table.TryGetValue(genInfo, out Bucket bucket))
        {
            return;
        }

        ImmutableList<Action<WorkflowGenerator.ImageToVideoGenInfo>> snapshot;
        lock (bucket.Gate)
        {
            snapshot = isPre ? bucket.Pre : bucket.Post;
        }

        foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in snapshot)
        {
            handler(genInfo);
        }
    }

    private sealed class Bucket
    {
        public readonly object Gate = new();
        public ImmutableList<Action<WorkflowGenerator.ImageToVideoGenInfo>> Pre = [];
        public ImmutableList<Action<WorkflowGenerator.ImageToVideoGenInfo>> Post = [];
    }

    private sealed class Detach(Bucket bucket, Action<WorkflowGenerator.ImageToVideoGenInfo> handler, bool isPre)
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
                if (isPre)
                {
                    bucket.Pre = bucket.Pre.Remove(handler);
                }
                else
                {
                    bucket.Post = bucket.Post.Remove(handler);
                }
            }
        }
    }
}
