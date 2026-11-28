using System.Diagnostics.CodeAnalysis;


#if !KSP_RETOOLED
// With retooled we have access to Unity's burst libraries directly
[assembly: KSPAssemblyDependency("KSPTextureLoader.Burst", 0, 0, 0)]
#endif
[assembly: KSPAssemblyDependency("SharpDX", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.Direct3D11", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.DXGI", 0, 0, 0)]

// We want to use our own deprecated types internally
[assembly: SuppressMessage("Compiler", "CS0618", Justification = "Allow internal usage of own obsolete APIs")]
