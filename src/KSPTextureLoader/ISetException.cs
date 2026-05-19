using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace KSPTextureLoader;

internal interface ISetException
{
    void SetException(ExceptionDispatchInfo ex);
}

internal static class ExceptionUtils
{
    /// <summary>
    /// Like <c>task.GetAwaiter().GetResult()</c>, but if the task faulted with
    /// an <see cref="AggregateException"/> that wraps exactly one inner
    /// exception, throws that inner exception instead (recursively). This
    /// avoids surfacing nested <see cref="AggregateException"/> wrappers in
    /// load coroutines.
    /// </summary>
    internal static void GetResultUnwrapped(this Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(UnwrapSingleAggregate(e)).Throw();
        }
    }

    /// <inheritdoc cref="GetResultUnwrapped(Task)"/>
    internal static T GetResultUnwrapped<T>(this Task<T> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(UnwrapSingleAggregate(e)).Throw();
            throw; // unreachable; the line above always throws
        }
    }

    static Exception UnwrapSingleAggregate(Exception e)
    {
        while (e is AggregateException agg && agg.InnerExceptions.Count == 1)
            e = agg.InnerExceptions[0];
        return e;
    }

    /// <summary>
    /// Wraps an <see cref="IEnumerator"/>, catches any exceptions it throws,
    /// and forwards them to <paramref name="sink"/>.
    /// </summary>
    internal static IEnumerator<object> CatchExceptions(ISetException sink, IEnumerator enumerator)
    {
        using var dispose = enumerator as IDisposable;

        while (true)
        {
            object current;
            try
            {
                if (!enumerator.MoveNext())
                    break;

                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                sink.SetException(ExceptionDispatchInfo.Capture(ex));
                break;
            }

            yield return current;
        }
    }
}
