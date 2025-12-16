using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace KSPTextureLoader;

internal static class TextureUtils
{
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

    internal static Texture2D CreateUninitializedTexture2D(
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

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
    }

    internal static Texture2DArray CreateUninitializedTexture2DArray(
        int width,
        int height,
        int depth,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Texture2DArray)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

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
    }

    internal static Texture3D CreateUninitializedTexture3D(
        int width,
        int height,
        int depth,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Texture3D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

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
    }

    internal static Cubemap CreateUninitializedCubemap(
        int extent,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (Cubemap)FormatterServices.GetUninitializedObject(typeof(Cubemap));

        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.DontInitializePixels;

        Cubemap.Internal_Create(
            tex,
            extent,
            mipCount,
            format,
            (TextureCreationFlags)flags,
            IntPtr.Zero
        );

        return tex;
    }

    internal static CubemapArray CreateUninitializedCubemapArray(
        int extent,
        int count,
        int mipCount,
        GraphicsFormat format,
        InternalTextureCreationFlags flags = InternalTextureCreationFlags.None
    )
    {
        var tex = (CubemapArray)FormatterServices.GetUninitializedObject(typeof(Cubemap));

        if (!tex.ValidateFormat(GraphicsFormatUtility.GetTextureFormat(format)))
            return tex;

        flags |= InternalTextureCreationFlags.DontInitializePixels;
        if (mipCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;

        CubemapArray.Internal_Create(
            tex,
            extent,
            count,
            mipCount,
            format,
            (TextureCreationFlags)flags
        );

        return tex;
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
            src.graphicsFormat
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
            src.graphicsFormat
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
            src.graphicsFormat
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }

    internal static Cubemap CloneTexture(Cubemap src)
    {
        var dst = CreateUninitializedCubemap(src.width, src.mipmapCount, src.graphicsFormat);
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
            src.graphicsFormat
        );
        if (!src.isReadable)
            dst.Apply(false, true);

        Graphics.CopyTexture(src, dst);
        return dst;
    }
    #endregion

    #region Cubemap Conversion
    internal static Cubemap ConvertTexture2dToCubemap(Texture2D src, bool unreadable)
    {
        var cubedim = src.width / 4;
        if (src.width != cubedim * 4 || src.height != cubedim * 3)
            throw new Exception(
                "2D texture was not in the right format for a cubemap. Dimensions need to be 4*cubedim x 3*cubedim."
            );

        Cubemap cube;

        var mips = GetSupportedMipMapLevels(cubedim, src.graphicsFormat);
        if (mips > src.mipmapCount)
            mips = src.mipmapCount;

        bool doItManually = !SystemInfo.copyTextureSupport.HasFlag(
            CopyTextureSupport.DifferentTypes
        );

        // Graphics.CopyTexture doesn't support copying the readable texture data
        // for compressed formats when copying regions.
        //
        // So to make that case work we need to use SetPixels/GetPixels instead.
        if (GraphicsFormatUtility.IsCompressedFormat(src.graphicsFormat) && unreadable)
            doItManually = true;

        if (doItManually)
        {
            cube = CreateUninitializedCubemap(
                cubedim,
                mips,
                GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false)
            );

            cube.SetPixels(
                src.GetPixels(2 * cubedim, 2 * cubedim, cubedim, cubedim),
                CubemapFace.NegativeY
            );
            cube.SetPixels(
                src.GetPixels(3 * cubedim, cubedim, cubedim, cubedim),
                CubemapFace.PositiveX
            );
            cube.SetPixels(
                src.GetPixels(2 * cubedim, cubedim, cubedim, cubedim),
                CubemapFace.PositiveZ
            );
            cube.SetPixels(
                src.GetPixels(cubedim, cubedim, cubedim, cubedim),
                CubemapFace.NegativeX
            );
            cube.SetPixels(src.GetPixels(0, cubedim, cubedim, cubedim), CubemapFace.NegativeZ);
            cube.SetPixels(src.GetPixels(2 * cubedim, 0, cubedim, cubedim), CubemapFace.PositiveY);

            cube.Apply(true, false);
            return cube;
        }

        // TODO: Use blits to copy the cubemap faces in this case.
        if (cubedim % GraphicsFormatUtility.GetBlockHeight(src.graphicsFormat) != 0)
            throw new Exception(
                "Cubemap side dimension was not a multiple of compressed texture block height"
            );

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

        cube = CreateUninitializedCubemap(cubedim, mips, src.graphicsFormat);
        if (!src.isReadable)
            cube.Apply(false, makeNoLongerReadable: true);

        void CopyFace(int mip, int srcX, int srcY, CubemapFace dstFace)
        {
            Graphics.CopyTexture(
                src,
                srcElement: 0,
                srcMip: mip,
                srcX: srcX >> mip,
                srcY: srcY >> mip,
                srcWidth: cubedim >> mip,
                srcHeight: cubedim >> mip,
                cube,
                dstElement: (int)dstFace,
                dstMip: mip,
                dstX: 0,
                dstY: 0
            );
        }

        for (int mip = 0; mip < mips; ++mip)
        {
            CopyFace(mip, 2 * cubedim, 2 * cubedim, CubemapFace.NegativeY);
            CopyFace(mip, 3 * cubedim, cubedim, CubemapFace.PositiveX);
            CopyFace(mip, 2 * cubedim, cubedim, CubemapFace.PositiveZ);
            CopyFace(mip, cubedim, cubedim, CubemapFace.NegativeX);
            CopyFace(mip, 0, cubedim, CubemapFace.NegativeZ);
            CopyFace(mip, 2 * cubedim, 0, CubemapFace.PositiveY);
        }

        return cube;
    }

    internal static Texture2DArray ConvertTexture2DToArray(Texture2D src)
    {
        var dst = (Texture2DArray)FormatterServices.GetUninitializedObject(typeof(Texture2DArray));

        var flags = InternalTextureCreationFlags.DontInitializePixels;
        if (src.mipmapCount != 1)
            flags |= InternalTextureCreationFlags.MipChain;
        if (!src.isReadable)
            flags |= InternalTextureCreationFlags.DontCreateSharedTextureData;

        Texture2DArray.Internal_Create(
            dst,
            src.width,
            src.height,
            1,
            src.mipmapCount,
            src.graphicsFormat,
            (TextureCreationFlags)flags
        );

        for (int i = 0; i < src.mipmapCount; ++i)
            Graphics.CopyTexture(src, 0, i, dst, 0, i);

        return dst;
    }

    internal static CubemapArray ConvertCubemapToArray(Cubemap src)
    {
        var flags = InternalTextureCreationFlags.None;
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
