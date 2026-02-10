using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal abstract class CPUTextureData : IDisposable
{
    public abstract NativeArray<byte> GetRawTextureData();

    public virtual void Dispose() { }
}

internal sealed class CPUTextureData_TextureHandle : CPUTextureData
{
    readonly Texture2D texture;
    readonly TextureHandleImpl handle;

    public CPUTextureData_TextureHandle(TextureHandleImpl handle)
    {
        using (handle)
        {
            texture = (Texture2D)handle.GetTexture();
            if (!texture.isReadable)
                throw new Exception("TextureHandle used for CPUTexture is not readable");

            this.handle = handle.Acquire();
        }
    }

    public override NativeArray<byte> GetRawTextureData() => texture.GetRawTextureData<byte>();

    public override void Dispose() => handle.Dispose();
}

// internal sealed class CPUTextureData_MemoryMap : CPUTextureData
// {

// }
