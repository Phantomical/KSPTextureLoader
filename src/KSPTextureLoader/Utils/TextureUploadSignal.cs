using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace KSPTextureLoader.Utils;

internal unsafe class TextureUploadSignal : CustomYieldInstruction
{
    static CommandBuffer SignalBuffer = null;

    bool complete = false;

    public override bool keepWaiting => !complete;

    private TextureUploadSignal() { }

    public static TextureUploadSignal Submit()
    {
        SignalBuffer ??= new CommandBuffer { name = "Signal Texture Upload Complete" };
        SignalBuffer.Clear();

        var signal = new TextureUploadSignal();

        var uploadHandle = (ObjectHandle<TextureUploadSignal>*)
            UnsafeUtility.Malloc(
                sizeof(ObjectHandle<TextureUploadSignal>),
                16,
                Allocator.Persistent
            );
        *uploadHandle = new(signal);

        SignalBuffer.IssuePluginEventAndData(SignalUploadCompleteFuncPtr, 0, (IntPtr)uploadHandle);
        Graphics.ExecuteCommandBuffer(SignalBuffer);

        return signal;
    }

    static readonly ProfilerMarker SignalUploadMarker = new("SignalUploadComplete");

    static void SignalUploadComplete(uint eventID, ObjectHandle<TextureUploadSignal>* data)
    {
        using var scope = SignalUploadMarker.Auto();
        using var handle = *data;
        UnsafeUtility.Free(data, Allocator.Persistent);
        handle.Target.complete = true;
    }

    static readonly IntPtr SignalUploadCompleteFuncPtr = Marshal.GetFunctionPointerForDelegate(
        SignalUploadComplete
    );
}
