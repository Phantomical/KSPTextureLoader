using System;
using UnityEngine;

namespace KSPTextureLoader;

internal class TextureCleanupGuard(Texture texture) : IDisposable
{
    internal Texture texture = texture;

    public void Dispose()
    {
        if (texture == null)
            return;

        UnityEngine.Object.Destroy(texture);
    }
}
