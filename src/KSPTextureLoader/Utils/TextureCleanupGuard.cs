using System;
using UnityEngine;

namespace KSPTextureLoader.Utils;

internal class TextureCleanupGuard(Texture texture = null, AssetBundleHandle bundle = null)
    : IDisposable
{
    internal AssetBundleHandle bundle = bundle;
    internal Texture texture = texture;

    public void Update(Texture newtex)
    {
        Dispose();
        texture = newtex;
    }

    public void Clear() => texture = null;

    public void Dispose()
    {
        if (texture == null)
            return;

        if (bundle is not null)
        {
            // Destroying textures loaded from an asset bundle means that we can
            // never load them again without reloading the whole asset bundle.
            // Resources.UnloadAsset avoids that issue.
            Resources.UnloadAsset(texture);
        }
        else
        {
            Texture.Destroy(texture);
        }

        texture = null;
        bundle = null;
    }
}
