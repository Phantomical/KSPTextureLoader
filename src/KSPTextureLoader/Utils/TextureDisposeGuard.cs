using System;
using UnityEngine;

namespace KSPTextureLoader.Utils;

internal class TextureDisposeGuard(Texture texture) : IDisposable
{
    public Texture texture = texture;

    public void Clear() => texture = null;

    public void Dispose()
    {
        if (texture is null)
            return;

        UnityEngine.Object.Destroy(texture);
    }
}
