using System;
using System.Net;
using System.Runtime.Serialization;
using KSPTextureLoader.Internals;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace KSPTextureLoader;

internal static class TextureUtils
{
    internal static void ApplyGeneric(
        this Texture tex,
        bool updateMipmaps,
        bool makeNoLongerReadable
    )
    {
        switch (tex)
        {
            case Texture2D tex2d:
                tex2d.Apply(updateMipmaps, makeNoLongerReadable);
                break;
            case Texture2DArray texture2DArray:
                texture2DArray.Apply(updateMipmaps, makeNoLongerReadable);
                break;
            case Texture3D texture3D:
                texture3D.Apply(updateMipmaps, makeNoLongerReadable);
                break;
            case Cubemap cubemap:
                cubemap.Apply(updateMipmaps, makeNoLongerReadable);
                break;
            case CubemapArray cubemapArray:
                cubemapArray.Apply(updateMipmaps, makeNoLongerReadable);
                break;

            default:
                throw new NotSupportedException(
                    $"Cannot apply a texture of type {tex.GetType().Name}"
                );
        }
    }

    internal static uint GetTextureSizeInMemory(Texture texture)
    {
        int w = texture.width;
        int h = texture.height;
        int d = 1;
        int arraySize = 1;
        int mipmaps = texture.mipmapCount;
        var format = texture.graphicsFormat;

        switch (texture)
        {
            case Texture2D texture2d:
                break;

            case Texture2DArray texture2DArray:
                arraySize = texture2DArray.depth;
                break;

            case Texture3D texture3D:
                d = texture3D.depth;
                break;

            case Cubemap cubemap:
                arraySize = 6;
                break;

            case CubemapArray cubemapArray:
                arraySize = 6 * cubemapArray.cubemapCount;
                break;
        }

        uint size = 0;
        for (int i = 0; i < mipmaps; ++i)
        {
            size += GraphicsFormatUtility.ComputeMipmapSize(w, h, d, format) * (uint)arraySize;

            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
            d = (d + 1) >> 1;
        }

        return size;
    }

    #region CreateUninitializedTexture
    // This reflects the actual creation flags in
    // https://github.com/Unity-Technologies/UnityCsReference/blob/59b03b8a0f179c0b7e038178c90b6c80b340aa9f/Runtime/Export/Graphics/GraphicsEnums.cs#L626
    //
    // Most of the extra ones here are completely undocumented.
    [Flags]
    internal enum InternalTextureCreationFlags
    {
        None,
        MipChain = 1 << 0,
        DontInitializePixels = 1 << 2,
        DontDestroyTexture = 1 << 3,
        DontCreateSharedTextureData = 1 << 4,
        APIShareable = 1 << 5,
        Crunch = 1 << 6,

        /// <summary>
        /// Not a unity flag. On Unity 6.7 textures are created with
        /// <c>DontUploadUponCreate</c> unless this is set. Set it if the
        /// texture is going to be written to by <see cref="Graphics.CopyTexture"/>
        /// before it is applied, since that needs the GPU texture to exist.
        /// </summary>
#if UNITY_6_7
        UploadUponCreate = 1 << 30,
#else
        UploadUponCreate = 0,
#endif
    }

    /// <summary>
    /// Create a <see cref="Texture2D"/> without initializing its data.
    /// </summary>
    internal static Texture2D CreateUninitializedTexture2D(
        int width,
        int height,
        TextureFormat format = TextureFormat.RGBA32,
        bool mipChain = false,
        bool linear = false,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        // The code in here exactly matches the behaviour of the Texture2D
        // constructors which directly take a TextureFormat, with one
        // difference: it includes the DontInitializePixels flag.
        //
        // This is necessary because the Texture2D constructors that take
        // GraphicsFormat validate the format differently than those that take
        // TextureFormat, and only the GraphicsFormat constructors allow you to
        // pass TextureCreationFlags.
        //
        // I (@Phantomical) have taken at look at decompiled implementation for
        // Internal_Create_Impl and validated that this works as you would expect.

        if (GraphicsFormatUtility.IsCrunchFormat(format))
            flags |= InternalTextureCreationFlags.Crunch;
        int mipCount = !mipChain ? 1 : -1;

        return CreateUninitializedTexture2D(
            width,
            height,
            mipCount,
            GraphicsFormatUtility.GetGraphicsFormat(format, isSRGB: !linear),
            flags
        );
    }

#if UNITY_6_7
    static TextureCreationFlags GetUnityCreationFlags(InternalTextureCreationFlags flags)
    {
        var unity = (TextureCreationFlags)(flags & ~InternalTextureCreationFlags.UploadUponCreate);
        if ((flags & InternalTextureCreationFlags.UploadUponCreate) == 0)
            unity |= TextureCreationFlags.DontUploadUponCreate;
        return unity;
    }
#endif

    static readonly ProfilerMarker CreateTexture2DMarker = new("CreateUninitializedTexture2D");

    internal static Texture2D CreateUninitializedTexture2D(
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        using var scope = CreateTexture2DMarker.Auto();

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

#if UNITY_6_7
        return new Texture2D(width, height, format, mipCount, GetUnityCreationFlags(flags));
#else
        var tex = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        Texture2D.Internal_Create(
            tex,
            width,
            height,
            mipCount,
            format,
            (TextureCreationFlags)flags,
            IntPtr.Zero
        );

        return tex;
#endif
    }

    static readonly ProfilerMarker CreateTexture2DArrayMarker = new(
        "CreateUninitializedTexture2DArray"
    );

    internal static Texture2DArray CreateUninitializedTexture2DArray(
        int width,
        int height,
        int depth,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        using var scope = CreateTexture2DArrayMarker.Auto();

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

#if UNITY_6_7
        return new Texture2DArray(
            width,
            height,
            depth,
            format,
            GetUnityCreationFlags(flags),
            mipCount
        );
#else
        var tex = (Texture2DArray)FormatterServices.GetUninitializedObject(typeof(Texture2DArray));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        Texture2DArray.Internal_Create(
            tex,
            width,
            height,
            depth,
            mipCount,
            format,
            (TextureCreationFlags)flags
        );

        return tex;
#endif
    }

    static readonly ProfilerMarker CreateTexture3DMarker = new("CreateUninitializedTexture3D");

    internal static Texture3D CreateUninitializedTexture3D(
        int width,
        int height,
        int depth,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        using var scope = CreateTexture3DMarker.Auto();

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

#if UNITY_6_7
        return new Texture3D(width, height, depth, format, GetUnityCreationFlags(flags), mipCount);
#else
        var tex = (Texture3D)FormatterServices.GetUninitializedObject(typeof(Texture3D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        Texture3D.Internal_Create(
            tex,
            width,
            height,
            depth,
            mipCount,
            format,
            (TextureCreationFlags)flags
        );

        return tex;
#endif
    }

    static readonly ProfilerMarker CreateCubemapMarker = new("CreateUninitializedCubemap");

    internal static Cubemap CreateUninitializedCubemap(
        int extent,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        using var scope = CreateCubemapMarker.Auto();

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

#if UNITY_6_7
        return new Cubemap(extent, format, GetUnityCreationFlags(flags), mipCount);
#else
        var tex = (Cubemap)FormatterServices.GetUninitializedObject(typeof(Cubemap));

        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        Cubemap.Internal_Create(
            tex,
            extent,
            mipCount,
            format,
            (TextureCreationFlags)flags,
            IntPtr.Zero
        );

        return tex;
#endif
    }

    static readonly ProfilerMarker CreateCubemapArrayMarker = new(
        "CreateUninitializedCubemapArray"
    );

    internal static CubemapArray CreateUninitializedCubemapArray(
        int extent,
        int count,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        using var scope = CreateCubemapArrayMarker.Auto();

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

#if UNITY_6_7
        return new CubemapArray(extent, count, format, GetUnityCreationFlags(flags), mipCount);
#else
        var tex = (CubemapArray)FormatterServices.GetUninitializedObject(typeof(CubemapArray));

        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        CubemapArray.Internal_Create(
            tex,
            extent,
            count,
            mipCount,
            format,
            (TextureCreationFlags)flags
        );

        return tex;
#endif
    }
    #endregion

    #region CreateExternalTexture
    internal static Texture2D CreateExternalTexture2D(
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        IntPtr nativePtr,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

        Texture2D.Internal_Create(
            tex,
            width,
            height,
            mipCount,
            format,
#if UNITY_6_7
            tex.GetTextureColorSpace(format),
#endif
            (TextureCreationFlags)flags,
#if UNITY_6_7
            nativePtr,
            true,
            null
#else
            nativePtr
#endif
        );

        return tex;
    }

    internal static Cubemap CreateExternalCubemap(
        int extent,
        int mipCount,
        GraphicsFormat format,
        IntPtr nativePtr,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Cubemap)FormatterServices.GetUninitializedObject(typeof(Cubemap));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

        Cubemap.Internal_Create(
            tex,
            extent,
            mipCount,
            format,
#if UNITY_6_7
            tex.GetTextureColorSpace(format),
#endif
            (TextureCreationFlags)flags,
            nativePtr
        );

        return tex;
    }
    #endregion

    #region CloneTexture
    internal static Texture CloneTexture(Texture src)
    {
        return src switch
        {
            Texture2D texture2d => CloneTexture(texture2d),
            Texture2DArray texture2darray => CloneTexture(texture2darray),
            Texture3D texture3d => CloneTexture(texture3d),
            Cubemap cubemap => CloneTexture(cubemap),
            CubemapArray cubemapArray => CloneTexture(cubemapArray),
            _ => throw new NotImplementedException(
                $"Cannot clone a texture of type {src.GetType().Name}"
            ),
        };
    }

    internal static Texture2D CloneTexture(Texture2D src)
    {
        var dst = CreateUninitializedTexture2D(
            src.width,
            src.height,
            src.mipmapCount,
            src.graphicsFormat,
            InternalTextureCreationFlags.UploadUponCreate
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }

    internal static Texture2DArray CloneTexture(Texture2DArray src)
    {
        var dst = CreateUninitializedTexture2DArray(
            src.width,
            src.height,
            src.depth,
            src.mipmapCount,
            src.graphicsFormat,
            InternalTextureCreationFlags.UploadUponCreate
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }

    internal static Texture3D CloneTexture(Texture3D src)
    {
        var dst = CreateUninitializedTexture3D(
            src.width,
            src.height,
            src.depth,
            src.mipmapCount,
            src.graphicsFormat,
            InternalTextureCreationFlags.UploadUponCreate
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }

    internal static Cubemap CloneTexture(Cubemap src)
    {
        var dst = CreateUninitializedCubemap(
            src.width,
            src.mipmapCount,
            src.graphicsFormat,
            InternalTextureCreationFlags.UploadUponCreate
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }

    internal static CubemapArray CloneTexture(CubemapArray src)
    {
        var dst = CreateUninitializedCubemapArray(
            src.width,
            src.cubemapCount,
            src.mipmapCount,
            src.graphicsFormat,
            InternalTextureCreationFlags.UploadUponCreate
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }
    #endregion

    #region Cubemap

    static int GetSupportedMipMapLevels(int cubedim, GraphicsFormat format)
    {
        var tzcnt = TrailingZeroCount(cubedim);

        // We cannot subdivide compressed formats to smaller than supported by
        // their block size.
        if (GraphicsFormatUtility.IsCompressedFormat(format))
            tzcnt -= TrailingZeroCount((int)GraphicsFormatUtility.GetBlockHeight(format));

        if (tzcnt < 1)
            tzcnt = 1;

        return tzcnt;
    }

    /// <summary>
    /// Extracts a single face from a cubemap as a Texture2D using GPU-side copy.
    /// Works on non-readable cubemaps.
    /// </summary>
    internal static Texture2D ExtractCubemapFace(Cubemap src, CubemapFace face)
    {
        var flags = TextureCreationFlags.None;
        if (src.mipmapCount > 1)
            flags |= TextureCreationFlags.MipChain;

        var tex = new Texture2D(src.width, src.height, src.graphicsFormat, flags)
        {
            name = $"{src.name} ({face})",
        };
        Graphics.CopyTexture(src, (int)face, tex, 0);
        return tex;
    }

    /// <summary>
    /// Extracts a single slice from a Texture2DArray as a Texture2D using GPU-side copy.
    /// Works on non-readable array textures.
    /// </summary>
    internal static Texture2D ExtractArraySlice(Texture2DArray src, int slice)
    {
        var flags = TextureCreationFlags.None;
        if (src.mipmapCount > 1)
            flags |= TextureCreationFlags.MipChain;

        var tex = new Texture2D(src.width, src.height, src.graphicsFormat, flags)
        {
            name = $"{src.name} [{slice}]",
        };
        Graphics.CopyTexture(src, slice, tex, 0);
        return tex;
    }

    /// <summary>
    /// Whether <see cref="ConvertTexture2dToCubemap"/> can use
    /// <see cref="Graphics.CopyTexture"/>, in which case an unreadable cubemap
    /// does not need the source to be readable.
    /// </summary>
    internal static bool CanGpuCopyTexture2dToCubemap =>
        SystemInfo.copyTextureSupport.HasFlag(
            CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes
        );

    // The cell (in face units) within the 4x3 cross that each face is read from.
    static readonly (int x, int y, CubemapFace face)[] CubemapCrossLayout =
    [
        (2, 2, CubemapFace.NegativeY),
        (3, 1, CubemapFace.PositiveX),
        (2, 1, CubemapFace.PositiveZ),
        (1, 1, CubemapFace.NegativeX),
        (0, 1, CubemapFace.NegativeZ),
        (2, 0, CubemapFace.PositiveY),
    ];

    /// <summary>
    /// Converts a 4x3 cross-layout texture into a cubemap.
    /// </summary>
    ///
    /// <remarks>
    /// If the platform supports it then this is done using
    /// <see cref="Graphics.CopyTexture"/>, which preserves the source format. It
    /// copies the CPU-side data as well when both textures are readable, so
    /// <paramref name="src"/> only needs to be readable if
    /// <paramref name="readable"/> is true.
    ///
    /// Otherwise, the source must be readable and the resulting cubemap will be
    /// RGBA32.
    /// </remarks>
    internal static Cubemap ConvertTexture2dToCubemap(Texture2D src, bool readable)
    {
        var cubedim = src.width / 4;
        if (src.width != cubedim * 4 || src.height != cubedim * 3)
            throw new Exception(
                "2D texture was not in the right format for a cubemap. Dimensions need to be 4*cubedim x 3*cubedim."
            );

        // This also ensures that every face region in every mip is aligned to
        // the block size of compressed formats.
        var mips = GetSupportedMipMapLevels(cubedim, src.graphicsFormat);
        if (mips > src.mipmapCount)
            mips = src.mipmapCount;

        Cubemap cube;

        // The cubemap is created with DontInitializePixels so every mip level
        // needs to be copied over from the source, otherwise it will contain
        // garbage.
        if (CanGpuCopyTexture2dToCubemap && (!readable || src.isReadable))
        {
            cube = CreateUninitializedCubemap(
                cubedim,
                mips,
                src.graphicsFormat,
                InternalTextureCreationFlags.UploadUponCreate
            );

            // Make sure the GPU texture exists and drop the CPU copy so that
            // nothing uploads the uninitialized CPU data over what we copy.
            if (!readable)
                cube.Apply(false, true);

            for (int mip = 0; mip < mips; ++mip)
            {
                var dim = cubedim >> mip;
                foreach (var (x, y, face) in CubemapCrossLayout)
                {
                    Graphics.CopyTexture(
                        src,
                        0,
                        mip,
                        x * dim,
                        y * dim,
                        dim,
                        dim,
                        cube,
                        (int)face,
                        mip,
                        0,
                        0
                    );
                }
            }
        }
        else
        {
            cube = CreateUninitializedCubemap(
                cubedim,
                mips,
                GraphicsFormatUtility.GetGraphicsFormat(
                    TextureFormat.RGBA32,
                    GraphicsFormatUtility.IsSRGBFormat(src.graphicsFormat)
                )
            );

            for (int mip = 0; mip < mips; ++mip)
            {
                var dim = cubedim >> mip;
                foreach (var (x, y, face) in CubemapCrossLayout)
                {
                    var pixels = src.GetPixels(x * dim, y * dim, dim, dim, mip);
                    cube.SetPixels(pixels, face, mip);
                }
            }

            cube.Apply(false, !readable);
        }

        cube.name = src.name;
        return cube;
    }

    internal static Texture2DArray ConvertTexture2DToArray(Texture2D src)
    {
        var flags = InternalTextureCreationFlags.DontInitializePixels;
        if (src.mipmapCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;
        if (!src.isReadable)
            flags |= InternalTextureCreationFlags.DontCreateSharedTextureData;

#if UNITY_6_7
        var dst = new Texture2DArray(
            src.width,
            src.height,
            1,
            src.graphicsFormat,
            (TextureCreationFlags)flags,
            src.mipmapCount
        );
#else
        var dst = (Texture2DArray)FormatterServices.GetUninitializedObject(typeof(Texture2DArray));

        Texture2DArray.Internal_Create(
            dst,
            src.width,
            src.height,
            1,
            src.mipmapCount,
            src.graphicsFormat,
            (TextureCreationFlags)flags
        );
#endif

        for (int i = 0; i < src.mipmapCount; ++i)
            Graphics.CopyTexture(src, 0, i, dst, 0, i);

        return dst;
    }

    internal static CubemapArray ConvertCubemapToArray(Cubemap src)
    {
        var flags = InternalTextureCreationFlags.UploadUponCreate;
        if (!src.isReadable)
            flags |= InternalTextureCreationFlags.DontCreateSharedTextureData;
        var dst = CreateUninitializedCubemapArray(
            src.width,
            1,
            src.mipmapCount,
            src.graphicsFormat,
            flags
        );

        Graphics.CopyTexture(src, dst);
        return dst;
    }
    #endregion

    internal static void MarkExternalTextureAsUnreadable(Texture tex)
    {
        if (!tex.isReadable)
            return;

        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
                if (IntPtr.Size != sizeof(ulong))
                    goto default;

                if (TryMarkExternalTextureAsUnreadableWin64(tex))
                    break;

                goto default;

            default:
                if (tex is Texture2D tex2d)
                {
                    // This works but results in a warning being emitted from the
                    // render thread.
                    //
                    // Pretty much all users of DX11 should be on Win64, so the
                    // special case above should avoid that warning in most cases.
                    tex2d.Apply(false, true);
                }
                else
                {
                    // Attempting to apply an external Cubemap texture will cause
                    // a segfault since unity doesn't guard against the texture
                    // being null.
                    //
                    // Luckily we should never hit this path since the branch
                    // above should take care of it.
                    Debug.LogWarning(
                        $"{tex.GetType().Name} cannot be marked as unreadable. Will be left as readable."
                    );
                }

                break;
        }
    }

    enum Build
    {
        Unknown,
        Debug,
        Release,
    }

    static Build? _buildInfo;
    static Build BuildInfo => _buildInfo ??= DetectBuildInfo();

    static Build DetectBuildInfo()
    {
        if (Application.unityVersion != "2019.4.18f1")
            return Build.Unknown;

        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
                if (IntPtr.Size != sizeof(ulong))
                    goto default;

                // Unity's profiler is only supported on development/debug
                // builds of the player, so we use it to tell which build we're
                // running against. This determines the field offsets within
                // Unity's native Texture2D struct.
                return Profiler.supported ? Build.Debug : Build.Release;

            default:
                return Build.Unknown;
        }
    }

    static bool TryMarkExternalTextureAsUnreadableWin64(Texture tex)
    {
        if (Application.unityVersion != "2019.4.18f1")
            return false;

        return BuildInfo switch
        {
            Build.Debug => TryMarkExternalTextureAsUnreadable<DebugWin64Texture>(tex),
            Build.Release => TryMarkExternalTextureAsUnreadable<ReleaseWin64Texture>(tex),
            _ => false,
        };
    }

    static unsafe bool TryMarkExternalTextureAsUnreadable<T>(Texture tex)
        where T : unmanaged, ITexture2DInternals
    {
        var wintex = (T*)tex.m_CachedPtr;
        if (wintex is null)
            return false;

        if (wintex->m_IsReadable != 1)
        {
            Debug.LogWarning(
                "[KSPTextureLoader] Attempted to access Texture2D.m_IsReadable but it had the wrong value"
            );
            return false;
        }

        wintex->m_IsReadable = 0;

        if (tex.isReadable)
        {
            wintex->m_IsReadable = 1;
            Debug.LogWarning("[KSPTextureLoader] Texture2D.m_IsReadable offset was incorrect");
            return false;
        }

        return true;
    }

    static unsafe int TrailingZeroCount(int value)
    {
        uint v = (uint)value;
        if (v == 0)
            return 32;

        float f = v & -v;
        uint r = *(uint*)&f;
        return (int)(r >> 23) - 0x7F;
    }
}
