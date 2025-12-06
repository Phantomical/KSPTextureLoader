using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs;

namespace KSPTextureLoader.Jobs;

// All this job does is ensure that all the pages in the buffer are faulted
// in. This way Unity's AsyncReadManager can read data in more quickly.
internal struct BufferPrefaultJob(NativeArray<byte> buffer) : IJob
{
    [ReadOnly]
    public NativeArray<byte> buffer = buffer;

    public void Execute()
    {
        for (int i = 0; i < buffer.Length; i += 4096)
            ConsumeByte(buffer[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ConsumeByte(byte b) { }
}
