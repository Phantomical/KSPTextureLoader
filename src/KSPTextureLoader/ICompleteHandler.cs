using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader;

internal interface ICompleteHandler
{
    bool IsComplete { get; }

    void WaitUntilComplete();
}

internal class AssetBundleCompleteHandler(AssetBundleCreateRequest request) : ICompleteHandler
{
    public bool IsComplete => request.isDone;

    public void WaitUntilComplete()
    {
        request.priority = 100;
        _ = request.assetBundle;
    }
}

internal class TaskCompleteHandler(Task task) : ICompleteHandler
{
    readonly Task task = task;
    readonly ICompletionContext context = CompletionContext.Current;

    public bool IsComplete => task.IsCompleted;

    public void WaitUntilComplete()
    {
        TextureLoader.Context.WaitUntilComplete(task, context);
    }
}

internal interface ICompletionContext
{
    public void MarkBlockedOn(ICompleteHandler handler);
    public void MarkCompleted(ICompleteHandler handler);
    public bool CompleteOne();
}

internal static class CompletionContext
{
    static readonly AsyncLocal<ICompletionContext> LocalContext = new();
    public static ICompletionContext Current => LocalContext.Value;

    public static Guard Enter(ICompletionContext context) => new(context);

    public static HandlerGuard BlockedOn(ICompleteHandler handler)
    {
        var context = Current;
        context.MarkBlockedOn(handler);
        return new(handler, context);
    }

    internal readonly struct Guard : IDisposable
    {
        readonly ICompletionContext previous;

        public Guard(ICompletionContext context)
        {
            previous = LocalContext.Value;
            LocalContext.Value = context;
        }

        public void Dispose()
        {
            LocalContext.Value = previous;
        }
    }

    internal readonly struct HandlerGuard(ICompleteHandler handler, ICompletionContext context)
        : IDisposable
    {
        readonly ICompleteHandler handler = handler;
        readonly ICompletionContext context = context;

        public void Dispose() => context.MarkCompleted(handler);
    }
}
