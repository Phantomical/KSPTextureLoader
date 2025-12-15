using KSPTextureLoader.Jobs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;

namespace KSPTextureLoader;

internal static unsafe class FileLoader
{
    internal static IFileReadStatus ReadFileContents(
        string path,
        long offset,
        NativeArray<byte> buffer,
        out JobHandle jobHandle
    )
    {
        if (Config.Instance.UseAsyncReadManager)
        {
            var command = new ReadCommand
            {
                Buffer = buffer.GetUnsafePtr(),
                Offset = offset,
                Size = buffer.Length,
            };
            var readHandle = AsyncReadManager.Read(path, &command, 1);

            jobHandle = readHandle.JobHandle;
            return new ReadHandleStatus(readHandle);
        }
        else
        {
            var exceptionStatus = new SavedExceptionStatus();
            var job = new FileReadJob
            {
                data = buffer,
                path = new(path),
                status = new(exceptionStatus),
                offset = offset,
            };

            jobHandle = job.Schedule();
            JobHandle.ScheduleBatchedJobs();
            return exceptionStatus;
        }
    }
}
