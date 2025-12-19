using System;
using UnityEngine;

namespace KSPTextureLoader.Utils;

internal class TextureCleanupGuard(Texture texture, AssetBundleHandle bundle = null) : IDisposable
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
            bundle.AddLeakedTexture(texture);
        else
            Texture.Destroy(texture);

        texture = null;
        bundle = null;
    }
}
