using System.Runtime.InteropServices;

namespace KSPTextureLoader.Internals;

[StructLayout(LayoutKind.Explicit)]
internal struct Win64Texture2D
{
    [FieldOffset(0x125)]
    public byte m_IsReadable;
}
