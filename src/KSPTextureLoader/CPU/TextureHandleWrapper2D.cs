using System;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class TextureHandleWrapper2D(TextureHandle<Texture2D> handle)
    : UnityTexture2D(handle?.GetTexture(), false)
{
    TextureHandle<Texture2D> handle = handle.Acquire();

    public override void Dispose()
    {
        base.Dispose();
        handle.Dispose();
        handle = null;

        GC.SuppressFinalize(this);
    }

    ~TextureHandleWrapper2D()
    {
        if (handle is null)
            return;

        TextureLoader.Instance?.ExecuteOnMainThread(handle.Dispose);
    }

    internal new readonly struct Factory : ICPUTexture2DFactory
    {
        readonly TextureHandle<Texture2D> handle;

        public Factory(TextureHandle<Texture2D> handle)
        {
            this.handle = handle ?? throw new ArgumentNullException(nameof(handle));

            var texture = handle.GetTexture();
            if (!texture.isReadable)
                throw new Exception($"texture {texture.name} is not readable");
        }

        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D
        {
            return new TextureHandleWrapper2D<T>(texture, handle);
        }

        public CPUTexture2D CreateFallback()
        {
            return new TextureHandleWrapper2D(handle);
        }
    }
}

internal sealed class TextureHandleWrapper2D<T>(T texture, TextureHandle<Texture2D> handle)
    : UnityTexture2D<T>(texture, handle.GetTexture(), false)
    where T : ICPUTexture2D
{
    TextureHandle<Texture2D> handle = handle.Acquire();

    public override void Dispose()
    {
        base.Dispose();
        handle.Dispose();
        handle = null;

        GC.SuppressFinalize(this);
    }

    ~TextureHandleWrapper2D()
    {
        if (handle is null)
            return;

        TextureLoader.Instance?.ExecuteOnMainThread(handle.Dispose);
    }
}
