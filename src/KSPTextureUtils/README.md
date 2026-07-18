# KSPTextureUtils

A command line tool to pack dds and png textures into a unity asset bundle that
can be loaded by KSPTextureLoader. This is meant to allow you to easily create
asset bundles without having to import stuff into the unity editor.

## Usage
### Building an asset bundle

```sh
ksp-texture-util bundle <input-dir> -o output-bundle.unity3d
```

This command takes a directory a builds an asset bundle with the textures within.
The texture paths in the asset bundle will be relative to `<input-dir>`. Note
that KSPTextureLoader expects texture paths within asset bundles to be as if they
were relative to `GameData`. If your local file structure doesn't exactly match
how they would be in `GameData` then you can use `--prefix` to prepend a prefix
path to them.

Available options are:
```
-o, --output       Path the output bundle will be written to.
-n, --name         Override the internal name of the asset bundle (used verbatim).
--prefix <prefix>  A path prefix to prepend to every bundle texture path.
--seed             Override the seed bundle used for type trees.
--mipmap-streaming Enable mipmap streaming on all textures in the bundle.
--properties       A YAML file assigning per-texture properties by glob.
```

### Per-texture properties

`--properties <file.yaml>` sets properties on individual textures instead of
the whole bundle:

```yaml
properties:
  - files: 'a/*/*.dds'
    readable: true        # keep a CPU-side copy so scripts can read the pixels
    mipmapStreaming: true # enable mipmap streaming for this texture
    filter: trilinear     # point, bilinear (default), or trilinear
    wrap: clamp           # repeat (default), clamp, mirror, or mirrorOnce
  - files: 'a/special/*.dds'
    mipmapStreaming: false
    wrapU: repeat         # per-axis wrap; overrides 'wrap' for that axis
    wrapV: clamp
  - files: 'skybox/*_cube.dds'
    cubemap: true         # repack a 4x3 cross into a native cubemap
```

Each entry needs a `files` glob plus any of the properties:

* `readable` - keep a CPU-side copy of the pixels (`m_IsReadable`). Default false.
* `mipmapStreaming` - enable mipmap streaming. Defaults to false, or true if
  `--mipmap-streaming` was passed. Only applies to 2D textures and cubemaps;
  array and 3D textures cannot be streamed by unity and ignore the setting.
* `streamingMipmapsPriority` - mip streaming priority (-128 to 127, default
  0): higher-priority textures keep their mips resident longer under memory
  pressure. Only meaningful when `mipmapStreaming` is enabled.
* `filter` - the sampler filter mode: `point`, `bilinear` (default), or
  `trilinear`.
* `wrap`, `wrapU`, `wrapV`, `wrapW` - the sampler wrap mode: `repeat`
  (default), `clamp`, `mirror`, or `mirrorOnce`. `wrap` sets every axis at
  once; the per-axis keys override it. Which axes are used depends on the
  texture type: 2D textures and arrays sample U/V, 3D textures also use W,
  and cubemaps are effectively always clamped by unity.
* `aniso` - anisotropic filtering level (0 to 16, default 1). 0 disables
  anisotropic filtering entirely, even when the quality settings force it on.
* `mipBias` - mip level bias applied when sampling (default 0).
* `colorSpace` - `srgb` or `linear`, overriding what was detected from the
  source file. Useful for data textures like normal maps stored as PNG, which
  would otherwise be tagged sRGB and be incorrectly gamma-converted when
  sampled.
* `cubemap` - `true` repacks a 2D input into a native cubemap at build time, so
  KSPTextureLoader loads a cubemap directly instead of converting it in-game.
  The input must be a 4x3 horizontal cross (width = 4 faces, height = 3 faces)
  laid out like `TextureUtils.ConvertTexture2dToCubemap` expects. The faces keep
  the source format (a compressed cross stays compressed), which requires the
  face size to be a multiple of the format's block size (4 for BC/DXT); inputs
  that aren't a valid cross or aren't block-aligned are skipped with a message.
  Only the base mip is used, producing a single-mip cubemap.

Globs are matched case-insensitively against the same input-relative path that
becomes the texture's bundle path, before `--prefix` is applied: `*` matches
within one path segment, `**` spans directories, and `?` matches a single
character. Every matching entry applies in order, so later entries override
earlier ones for whichever properties they specify.

### Extracting textures from an asset bundle

```sh
ksp-texture-util extract asset-bundle.unity3d --output <outdir>
```

This extracts all the textures contained within the asset bundle. Textures that
were originally png will be extracted as png, everything else will be extracted
as dds textures.

Available options are:
```
-o, --output  Output directory to write files to.
--flat        Put all texture files directly in the output directory.
```

## Supported texture formats
This tool can bundle any of the following file formats:
* png,
* dds - provided the format is one supported by unity.

In addition, the dds importer also supports:
* Arrays, cubemaps, 3d textures, and cubemap arrays,
* The kopernicus 4bit/8bit palette formats. These are converted to RGBA4444,
  RGB565, or RGBA32, depending on what the palette colors allow.
