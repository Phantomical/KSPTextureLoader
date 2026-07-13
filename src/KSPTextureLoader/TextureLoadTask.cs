using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using UnityEngine;

namespace KSPTextureLoader;

/// <summary>
/// A task used for loading an owned texture. This can be both yielded on or
/// awaited, either will work.
/// </summary>
public sealed class TextureLoadTask<T>
    : CustomYieldInstruction,
        ICompletionContext,
        ICompleteHandler
    where T : Texture
{
    readonly Task<T> task;
    MiniStack<ICompleteHandler> completeHandlers;

    public override bool keepWaiting => !task.IsCompleted;

    public bool IsComplete => task.IsCompleted;

    internal TextureLoadTask(Task<T> task)
    {
        if (task is null)
            throw new ArgumentNullException(nameof(task));

        this.task = task;
    }

    public Awaiter GetAwaiter() => new(this);

    public T GetTexture()
    {
        if (!task.IsCompleted)
            WaitUntilComplete();

        return task.Result;
    }

    public void WaitUntilComplete() => TextureLoader.Context.WaitUntilComplete(task, this);

    public readonly struct Awaiter(TextureLoadTask<T> task)
        : INotifyCompletion,
            ICriticalNotifyCompletion
    {
        readonly TextureLoadTask<T> ttask = task;
        readonly Task<T> Task => ttask.task;

        public T GetResult()
        {
            if (!Task.IsCompleted)
                ttask.WaitUntilComplete();

            return Task.Result;
        }

        public void OnCompleted(Action continuation) => Task.GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) =>
            Task.GetAwaiter().UnsafeOnCompleted(continuation);
    }

    void ICompletionContext.MarkBlockedOn(ICompleteHandler handler)
    {
        completeHandlers.Push(handler);
    }

    void ICompletionContext.MarkCompleted(ICompleteHandler handler)
    {
        if (completeHandlers.TryPeek(out var top) && ReferenceEquals(top, handler))
            completeHandlers.Pop();
    }

    bool ICompletionContext.CompleteOne()
    {
        if (!completeHandlers.TryPop(out var handler))
            return false;

        handler.WaitUntilComplete();
        return true;
    }
}
