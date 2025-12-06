using Unity.Collections;
using Unity.Jobs;

namespace KSPTextureLoader.Jobs;

struct BufferCopyJob : IJob
{
    [ReadOnly]
    public NativeArray<byte> input;

    [WriteOnly]
    public NativeArray<byte> output;

    public readonly void Execute()
    {
        if (input.Length != output.Length)
            return;

        output.CopyFrom(input);
    }
}
