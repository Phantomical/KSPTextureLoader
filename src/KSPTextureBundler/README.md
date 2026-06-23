# KSPTextureBundler

A command line tool to pack dds and png textures into a unity asset bundle that
can be loaded by KSPTextureLoader. This is meant to allow you to easily create
asset bundles without having to import stuff into the unity editor.

## Usage
### Building an asset bundle

```sh
KSPTextureLoader build <input-dir> -o output-bundle.unity3d
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
```

### Extracting textures from an asset bundle

```sh
KSPTextureLoader extract asset-bundle.unity3d --output <outdir>
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
* png
* dds - provided the format is one supported by unity.

In addition, the dds importer also supports:
* Arrays, cubemaps, 3d textures, and cubemap arrays,
* The kopernicus 4bit/8bit palette formats. These are converted to RGBA4444,
  RGB565, or RGBA32, depending on what the palette colors allow.
