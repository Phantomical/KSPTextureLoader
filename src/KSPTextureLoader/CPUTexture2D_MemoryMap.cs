using System.IO.MemoryMappedFiles;

namespace KSPTextureLoader;

internal sealed class CPUTexture2D_MemoryMapped<TTexture> : CPUTexture2D<TTexture>
    where TTexture : ICPUTexture2D
{
    MemoryMappedFile mmf;
    MemoryMappedViewAccessor accessor;

    internal CPUTexture2D_MemoryMapped(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        TTexture texture
    )
        : base(texture)
    {
        this.mmf = mmf;
        this.accessor = accessor;
    }

    public override void Dispose()
    {
        base.Dispose();

        accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor?.Dispose();
        mmf?.Dispose();

        accessor = null;
        mmf = null;
    }
}
