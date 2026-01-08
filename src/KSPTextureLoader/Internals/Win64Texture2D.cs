using System.Runtime.InteropServices;

namespace KSPTextureLoader.Internals;

interface ITexture2DInternals
{
    byte m_IsReadable { get; set; }
}

[StructLayout(LayoutKind.Explicit)]
internal struct DebugWin64Texture2D : ITexture2DInternals
{
    [field: FieldOffset(0x125)]
    public byte m_IsReadable { get; set; }
}

[StructLayout(LayoutKind.Explicit)]
internal struct ReleaseWin64Texture2D : ITexture2DInternals
{
    [field: FieldOffset(0x105)]
    public byte m_IsReadable { get; set; }
}
