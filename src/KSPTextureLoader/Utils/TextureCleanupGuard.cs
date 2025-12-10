using System;
using UnityEngine;

namespace KSPTextureLoader.Utils;

internal class TextureCleanupGuard(Texture texture) : IDisposable
{
    internal Texture texture = texture;

    public void Clear() => texture = null;

    public void Dispose()
    {
        if (texture == null)
            return;

        UnityEngine.Object.Destroy(texture);
    }
}
