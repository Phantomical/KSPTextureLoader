namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// The Unity runtime class ids of the texture classes and <c>AssetBundle</c> that
/// the loader can emit into a generated bundle.
/// </summary>
///
/// <remarks>
/// The serialized field layouts these ids map to no longer live here: the loader
/// copies each class's type tree verbatim from an embedded reference bundle (see
/// <see cref="ReferenceTypeTrees"/>), so the schemas are sourced from Unity's own
/// class database rather than transcribed by hand.
/// </remarks>
internal static class SerializedTypeTrees
{
    public const int Texture2DClassId = 28;
    public const int CubemapClassId = 89;
    public const int Texture3DClassId = 117;
    public const int Texture2DArrayClassId = 187;
    public const int CubemapArrayClassId = 188;
    public const int AssetBundleClassId = 142;
}
