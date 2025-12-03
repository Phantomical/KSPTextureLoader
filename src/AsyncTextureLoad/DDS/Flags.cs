using System;

namespace AsyncTextureLoad.DDS;

[Flags]
internal enum DDS_HEADER_FLAGS : uint
{
    CAPS = 0x1,
    HEIGHT = 0x2,
    WIDTH = 0x4,
    PITCH = 0x8,
    PIXELFORMAT = 0x1000,
    MIPMAPCOUNT = 0x20000,
    LINEARSIZE = 0x80000,
    DEPTH = 0x800000,
}

[Flags]
internal enum DDSCAPS : uint
{
    COMPLEX = 0x8,
    MIPMAP = 0x400000,
    TEXTURE = 0x1000,
}

[Flags]
internal enum DDSCAPS2 : uint
{
    CUBEMAP = 0x200,
    CUBEMAP_POSITIVEX = 0x400,
    CUBEMAP_NEGATIVEX = 0x800,
    CUBEMAP_POSITIVEY = 0x1000,
    CUBEMAP_NEGATIVEY = 0x2000,
    CUBEMAP_POSITIVEZ = 0x4000,
    CUBEMAP_NEGATIVEZ = 0x8000,
    VOLUME = 0x200000,
}

internal enum ResourceDimension : uint
{
    Texture1D = 2,
    Texture2D = 3,
    Texture3D = 4,
}

[Flags]
internal enum MiscFlags : uint
{
    TextureCube = 0x4,
}

internal enum MiscFlags2 : uint
{
    AlphaModeUnknown = 0,
    AlphaModeStraight = 1,
    AlphaModePremultiplied = 0x2,
    AlphaModeOpaque = 0x3,
    AlphaModeCustom = 0x4,
    AlphaModeMask = 0x7,
}
