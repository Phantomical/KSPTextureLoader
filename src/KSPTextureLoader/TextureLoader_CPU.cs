using System;
using System.Collections.Generic;

namespace KSPTextureLoader;

partial class TextureLoader
{
    internal readonly Dictionary<string, WeakReference<CPUTexture2D>> cpuTextures = [];
}
