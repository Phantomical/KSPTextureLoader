using System.Runtime.CompilerServices;
using KSPTextureLoader.Utils;
using Unity.Burst;
using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// Forwards the block-compressed formats' per-pixel accessors to Burst-compiled function pointers
/// when they run as managed code (i.e. outside a Burst context).
/// </summary>
internal static class BurstForward
{
    /// <summary>
    /// True when a managed-side accessor should hand off to a Burst-compiled function pointer.
    /// Always false inside Burst-compiled code (the <c>Check</c> call is stripped), so a
    /// Burst-compiled accessor would run its own body rather than recursing back out.
    /// </summary>
    internal static bool ShouldForward
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            [BurstDiscard]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void Check(ref bool forward) => forward = BurstForwardState.Enabled;

            bool forward = false;
            Check(ref forward);
            return forward;
        }
    }

    // Each forwarder returns through an `out` parameter so it can be void, which lets it be
    // [BurstDiscard]: if a caller shell is ever pulled into a Burst compile, the call is stripped
    // to nothing rather than dragging BurstForwardState (and its uncompilable cctor) in with it.
    // At runtime the shells only reach these when ShouldForward is true, i.e. running as managed
    // code, so the discarded case never leaves the `out` unassigned in a path that reads it.

    [BurstDiscard]
    internal static void Bc7GetPixel(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Bc7GetPixel(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Bc7GetPixel32(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    ) => BurstForwardState.Bc7GetPixel32(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Bc6hGetPixel(
        in CPUTexture2D.BC6H self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Bc6hGetPixel(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Bc4GetPixel(
        in CPUTexture2D.BC4 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Bc4GetPixel(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Bc5GetPixel(
        in CPUTexture2D.BC5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Bc5GetPixel(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Dxt1GetPixel(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Dxt1GetPixel(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Dxt1GetPixel32(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    ) => BurstForwardState.Dxt1GetPixel32(in self, x, y, mipLevel, out result);

    [BurstDiscard]
    internal static void Dxt5GetPixel(
        in CPUTexture2D.DXT5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => BurstForwardState.Dxt5GetPixel(in self, x, y, mipLevel, out result);
}

/// <summary>
/// Function pointers for the burst-compiled versions of these functions.
/// </summary>
internal static class BurstForwardState
{
    internal static readonly bool Enabled;

    internal static readonly Bc7GetPixelFn Bc7GetPixel;
    internal static readonly Bc7GetPixel32Fn Bc7GetPixel32;
    internal static readonly Bc6hGetPixelFn Bc6hGetPixel;
    internal static readonly Bc4GetPixelFn Bc4GetPixel;
    internal static readonly Bc5GetPixelFn Bc5GetPixel;
    internal static readonly Dxt1GetPixelFn Dxt1GetPixel;
    internal static readonly Dxt1GetPixel32Fn Dxt1GetPixel32;
    internal static readonly Dxt5GetPixelFn Dxt5GetPixel;

    static BurstForwardState()
    {
        try
        {
            // Only forward if a compiled pointer actually runs native code (probe returns 1).
            // Otherwise every forwarded call would re-enter the managed accessor and recurse.
            if (
                BurstCompiler.CompileFunctionPointer<ProbeFn>(BurstForwardTargets.Probe).Invoke()
                == 0
            )
                return;

            // Marshal each native pointer to a managed delegate once here and cache it. Reading
            // FunctionPointer<T>.Invoke allocates a fresh delegate on every access, so invoking the
            // property per pixel would allocate on the hot path; the cached delegate does not.
            Bc7GetPixel = BurstCompiler
                .CompileFunctionPointer<Bc7GetPixelFn>(BurstForwardTargets.Bc7GetPixel)
                .Invoke;
            Bc7GetPixel32 = BurstCompiler
                .CompileFunctionPointer<Bc7GetPixel32Fn>(BurstForwardTargets.Bc7GetPixel32)
                .Invoke;
            Bc6hGetPixel = BurstCompiler
                .CompileFunctionPointer<Bc6hGetPixelFn>(BurstForwardTargets.Bc6hGetPixel)
                .Invoke;
            Bc4GetPixel = BurstCompiler
                .CompileFunctionPointer<Bc4GetPixelFn>(BurstForwardTargets.Bc4GetPixel)
                .Invoke;
            Bc5GetPixel = BurstCompiler
                .CompileFunctionPointer<Bc5GetPixelFn>(BurstForwardTargets.Bc5GetPixel)
                .Invoke;
            Dxt1GetPixel = BurstCompiler
                .CompileFunctionPointer<Dxt1GetPixelFn>(BurstForwardTargets.Dxt1GetPixel)
                .Invoke;
            Dxt1GetPixel32 = BurstCompiler
                .CompileFunctionPointer<Dxt1GetPixel32Fn>(BurstForwardTargets.Dxt1GetPixel32)
                .Invoke;
            Dxt5GetPixel = BurstCompiler
                .CompileFunctionPointer<Dxt5GetPixelFn>(BurstForwardTargets.Dxt5GetPixel)
                .Invoke;

            Enabled = true;
        }
        catch
        {
            // Burst is not available in this process. Leave Enabled false; the accessors run their
            // own (software-intrinsic) bodies, exactly as they would without this forwarder.
        }
    }

    #region Delegates

    internal delegate int ProbeFn();

    internal delegate void Bc7GetPixelFn(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    internal delegate void Bc7GetPixel32Fn(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    );

    internal delegate void Bc6hGetPixelFn(
        in CPUTexture2D.BC6H self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    internal delegate void Bc4GetPixelFn(
        in CPUTexture2D.BC4 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    internal delegate void Bc5GetPixelFn(
        in CPUTexture2D.BC5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    internal delegate void Dxt1GetPixelFn(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    internal delegate void Dxt1GetPixel32Fn(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    );

    internal delegate void Dxt5GetPixelFn(
        in CPUTexture2D.DXT5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    );

    #endregion
}

/// <summary>
/// The Burst-compiled trampolines that <see cref="BurstForwardState"/> turns into function pointers.
/// Each re-runs an accessor's unguarded <c>…Core</c> body on the passed texture. They target the
/// <c>…Core</c> methods rather than the public <c>GetPixel</c>/<c>GetPixel32</c> shells so the
/// guarded shells stay out of Burst compilation entirely.
///
/// <para>
/// This type deliberately has no static fields and no static constructor: Burst compiles these
/// trampolines, and a static method belonging to a type with a constructor would drag that
/// constructor into the compile.
/// </para>
/// </summary>
[BurstCompile]
internal static class BurstForwardTargets
{
    [BurstCompile]
    internal static int Probe() => BurstUtil.IsBurstCompiled ? 1 : 0;

    [BurstCompile]
    internal static void Bc7GetPixel(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);

    [BurstCompile]
    internal static void Bc7GetPixel32(
        in CPUTexture2D.BC7 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    ) => result = self.GetPixel32Core(x, y, mipLevel);

    [BurstCompile]
    internal static void Bc6hGetPixel(
        in CPUTexture2D.BC6H self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);

    [BurstCompile]
    internal static void Bc4GetPixel(
        in CPUTexture2D.BC4 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);

    [BurstCompile]
    internal static void Bc5GetPixel(
        in CPUTexture2D.BC5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);

    [BurstCompile]
    internal static void Dxt1GetPixel(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);

    [BurstCompile]
    internal static void Dxt1GetPixel32(
        in CPUTexture2D.DXT1 self,
        int x,
        int y,
        int mipLevel,
        out Color32 result
    ) => result = self.GetPixel32Core(x, y, mipLevel);

    [BurstCompile]
    internal static void Dxt5GetPixel(
        in CPUTexture2D.DXT5 self,
        int x,
        int y,
        int mipLevel,
        out Color result
    ) => result = self.GetPixelCore(x, y, mipLevel);
}
