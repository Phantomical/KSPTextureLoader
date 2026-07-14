using UnityEngine;

namespace KSPTextureLoader;

internal interface ITextureDestination : ISetException
{
    string Path { get; }

    void SetTexture<T>(
        Texture tex,
        TextureLoadOptions options,
        TextureConvertOptions setOptions = default
    )
        where T : Texture;
}
