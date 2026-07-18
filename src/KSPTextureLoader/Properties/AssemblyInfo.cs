// Every assembly we ship alongside KSPTextureLoader.dll in Plugins/. Declaring a
// dependency on each one ensures KSP's AssemblyLoader loads them before us, which
// avoids some warnings in the log.
//
// None of these carry a KSPAssemblyName attribute, so KSP treats their version as
// 0.0 — match that here or the dependency never resolves.
[assembly: KSPAssemblyDependency("KSPTextureLoader.Burst", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.Direct3D11", 0, 0, 0)]
[assembly: KSPAssemblyDependency("SharpDX.DXGI", 0, 0, 0)]
