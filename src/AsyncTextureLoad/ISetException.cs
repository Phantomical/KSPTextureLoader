using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace AsyncTextureLoad;

internal interface ISetException
{
    void SetException(ExceptionDispatchInfo ex);
}

internal static class ExceptionUtils
{
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
