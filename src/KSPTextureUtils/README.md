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

You can pass more than one input, and each one may be either a directory
(searched recursively for `.dds` and `.png` files) or an individual texture
file. Paths are made relative to the directory that was passed in, or to the
containing directory for a file argument.

Available options are:
```
-o, --output <output>      (required) Path that the output bundle will be written to.
-n, --name <name>          Override the internal name of the asset bundle.
--seed <seed>              Override the embedded seed bundle used for type trees.
--prefix <prefix>          A path prefix to prepend to every bundle texture path.
--properties <properties>  A YAML file assigning per-texture properties by glob.
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
* `mipmapStreaming` - enable mipmap streaming. Default false. Only applies to
  2D textures and cubemaps; array and 3D textures cannot be streamed by unity
  and ignore the setting.
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
* `cubemap` - `true` packs the input into a real cubemap, so KSP loads it as
  one directly instead of converting it at load time. The image must be a
  horizontal cross: four faces wide and three faces tall, with the faces laid
  out like this:

  ```
   .    .   +Y    .
  -Z   -X   +Z   +X
   .    .   -Y    .
  ```

  The blank cells are ignored, so you can put whatever you like there. Faces
  keep the format of the source image.

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

Cubemaps, texture arrays, 3d textures, and cubemap arrays all come back as dds
files of the same shape, with their mipmaps intact, so they can be fed straight
back into `bundle`.

Available options are:
```
-o, --output <output>  (required) Output directory to write files to.
--flat                 Put all texture files directly in the output directory,
                       instead of recreating the bundle's path structure.
```

### Converting a texture to a Kopernicus palette

```sh
ksp-texture-util palette <input.png> -o output.dds
```

This converts a PNG or DDS texture to a 16- or 256-colour Kopernicus palette
DDS, picking whichever palette size the source's colour count allows. It fails
if the input has more than 256 distinct colours.

Available options are:
```
-o, --output <output>  (required) Path that the output palette DDS will be
                       written to.
```

## Supported texture formats
This tool can bundle any of the following file formats:
* png,
* dds - provided the format is one supported by unity.

In addition, the dds importer also supports:
* Arrays, cubemaps, 3d textures, and cubemap arrays,
* The kopernicus 4bit/8bit palette formats. These are converted to RGBA4444,
  RGB565, or RGBA32, depending on what the palette colors allow.
