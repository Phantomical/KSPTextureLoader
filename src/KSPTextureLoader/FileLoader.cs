using System.IO;
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
        // FileReadJob doesn't use Seek since it appears to not work reliably
        // on all systems. To avoid lots of extra work we use AsyncReadManager
        // if the offset is large since it handles offsets correctly.
        if (Config.Instance.UseAsyncReadManager || offset > 1024)
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
