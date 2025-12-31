using System;
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
            throw new InvalidOperationException(
                $"input and output lengths do not match (input {input.Length}, output {output.Length})"
            );

        output.CopyFrom(input);
    }
}
