using System;
using System.IO;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.Profiling;

namespace KSPTextureLoader.Jobs;

internal struct FileReadJob : IJob
{
    [WriteOnly]
    public NativeArray<byte> data;
    public ObjectHandle<string> path;
    public ObjectHandle<SavedExceptionStatus> status;
    public long offset;

    public void Execute()
    {
        using var pg = this.path;
        using var sg = this.status;
        var status = this.status.Target;
        var path = this.path.Target;

        try
        {
            Profiler.BeginSample($"File.Read: {path}");
            ExecuteImpl(path);
        }
        catch (Exception ex)
        {
            status.exception = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            Profiler.EndSample();
        }
    }

    unsafe void ExecuteImpl(string path)
    {
        using var reader = File.OpenRead(path);
        var ptr = (byte*)data.GetUnsafePtr();

        reader.Position = this.offset;

        int offset = 0;
        int length = data.Length;
        var buffer = new byte[Math.Min(length, 64 * 1024)];

        fixed (byte* bufptr = buffer)
        {
            while (offset < length)
            {
                int count = reader.Read(buffer, 0, buffer.Length);
                if (count > length - offset || count <= 0)
                    throw new Exception("the length of the file changed while it was being read");

                UnsafeUtility.MemCpy(ptr + offset, bufptr, count);
                offset += count;
            }
        }
    }
}
