// Ensure that these get loaded before KSPTextureLoader to avoid some warnings
// in the log.
[assembly: KSPAssemblyDependency("SharpDX", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.DXGI", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.Direct3D11", 0, 0, 0)]
