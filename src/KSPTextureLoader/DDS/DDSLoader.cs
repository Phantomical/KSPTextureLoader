using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace KSPTextureLoader.DDS;

internal static unsafe class DDSLoader
{
    static CommandBuffer SignalBuffer;

    public static IEnumerable<object> UploadTexture2D<T>(
        TextureHandleImpl handle,
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        TextureLoadOptions options,
        NativeArrayGuard<byte> bufGuard,
        SafeReadHandleGuard readGuard
    )
        where T : Texture
    {
        // Prefer a native texture upload if available.
        if (options.Unreadable && DX11.SupportsAsyncUpload(width, height, format))
        {
            return DX11.UploadTexture2D<T>(
                handle,
                width,
                height,
                mipCount,
                format,
                options,
                bufGuard,
                readGuard
            );
        }

        return UploadTexture2DBasic<T>(
            handle,
            width,
            height,
            mipCount,
            format,
            options,
            bufGuard,
            readGuard
        );
    }

    /// <summary>
    /// A comparatively straightforward texture upload: create an uninitialized
    /// texture, wait some amount of time (depending on hints), and then copy
    /// the data into the texture.
    /// </summary>
    static IEnumerable<object> UploadTexture2DBasic<T>(
        TextureHandleImpl handle,
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        TextureLoadOptions options,
        NativeArrayGuard<byte> bufGuard,
        SafeReadHandleGuard readGuard
    )
        where T : Texture
    {
        var texture = TextureUtils.CreateUninitializedTexture2D(width, height, mipCount, format);
        using var texGuard = new TextureDisposeGuard(texture);

        SignalBuffer ??= new CommandBuffer { name = "Signal Texture Upload Complete" };

        var signal = new SignalUploadCompleteData();

        unsafe
        {
            var uploadHandle = (ObjectHandle<SignalUploadCompleteData>*)
                UnsafeUtility.Malloc(
                    sizeof(ObjectHandle<SignalUploadCompleteData>),
                    16,
                    Allocator.Persistent
                );
            *uploadHandle = new(signal);

            SignalBuffer.IssuePluginEventAndData(
                SignalUploadCompleteFuncPtr,
                0,
                (IntPtr)uploadHandle
            );

            Graphics.ExecuteCommandBuffer(SignalBuffer);
            SignalBuffer.Clear();
        }

        handle.completeHandler = null;
        yield return signal;

        // If we are fully sync then we want to get this done while waiting for
        // the disk read to complete.
        if (options.Hint == TextureLoadHint.Synchronous)
            texture.GetRawTextureData<byte>();

        if (!readGuard.JobHandle.IsCompleted)
        {
            handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
            yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
            handle.completeHandler = null;
        }

        texture.LoadRawTextureData(bufGuard.array);
        bufGuard.array.Dispose(default);

        if (readGuard.Status != ReadStatus.Complete)
            throw new Exception("Failed to read texture data from file");

        texture.Apply(false, options.Unreadable);
        texGuard.Clear();
        handle.SetTexture<T>(texture, options);
    }

    class SignalUploadCompleteData : CustomYieldInstruction
    {
        public bool complete;

        public override bool keepWaiting => !complete;
    }

    static readonly IntPtr SignalUploadCompleteFuncPtr = Marshal.GetFunctionPointerForDelegate(
        SignalUploadComplete
    );

    static readonly ProfilerMarker SignalUploadMarker = new("SignalUploadComplete");

    /// <summary>
    /// This blocks the render thread until it is signaled by the
    /// <see cref="CountdownEvent"/>.
    /// </summary>
    /// <param name="eventID"></param>
    /// <param name="data"></param>
    static void SignalUploadComplete(uint eventID, ObjectHandle<SignalUploadCompleteData>* data)
    {
        using var scope = SignalUploadMarker.Auto();
        using var handle = *data;
        UnsafeUtility.Free(data, Allocator.Persistent);
        handle.Target.complete = true;
    }
}
