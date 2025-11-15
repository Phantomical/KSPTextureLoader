# Async Texture Loading

This repo shows a few different techniques for loading texture assets as
asynchronously as possible in KSP and Unity 2019.4.

The TLDR is:
* If possible use asset bundles. Use `UnityWebRequest` to load it async, then
  use `LoadAssetAsync` to load the individual textures.
* Otherwise use PNG assets, and use `UnityWebRequest` to load them.
  Be aware, however, that the textures will be decompressed on the rendering
  thread and may cause stuttering.
* If you need to load loose DDS textures, use the `TextureLoadManager` class
  in this repo. Note that there is still a significant CPU overhead when doing
  this because `Texture2D.GetRawTextureData` involves a large memcpy operation.

It might be possible to do better on the DDS front by using a native plugin.
However, this seems like it would be pretty bad to support and unity is handling
some texture details that I don't quite understand here. This repo does not
include any native plugins.

Note that in the following examples The only texture loaded is a copy of 
Parallax-Continued's Kerbin_Color.dds which has been re-encoded or packed
into the relevant format under test.

## Loading Asset Bundles
This is quite literally the best case. If you are able to use asset bundles
then unity will:
* Use `AsyncLoadManager` behind the scenes to perform the disk read
* Use jobs to actually do the texture upload
* Not bother the main thread for _any_ of this.

It is also rather simple to do. The minimal code you need is basically:
```cs
var uri = new Uri(path);
var request = AssetBundle.LoadAssetBundleAsync(uri);
yield return request;

if (request.assetBundle == null)
{
    Debug.LogError($"Failed to load asset bundle");
    return
}

var bundle = request.assetBundle;
var texreq = bundle.LoadAssetAsync<Texture2D>(name);
yield return texreq;

var texture = (Texture2D)texreq.asset;

bundle.Unload(false);
```

In more productionized code, you will likely want to have have multiple textures
per asset bundle. However, in any case. This will have basically no overhead on
the main thread.

Here's what a profile looks like:

<img width="1352" height="797" alt="image" src="https://github.com/user-attachments/assets/a50c9977-70e3-4e4e-8b0b-a62c1c2e6b29" />

It isn't possible to load a single asset bundle repeatedly, so I only have
a profile of loading a single one. However, we can see the texture being loaded
on a jobs thread, which is exactly what we want.

## Loading PNG Textures
PNGs are also possible to load entirely async. However, Unity will do the png
decompression on the rendering thread. This means that you will get stuttering
if you even try to load moderately sized png images. It doesn't impact the main
thread though and so it is likely to be perfectly acceptable during scene
switches. KSP especially has >10s load times which means you can load quite a
few textures during the scene switch delay.

Like with asset bundles, the code to load png images is straightforward. Just us
`UnityWebRequest` and the rest will be taken care of for you:

```cs
var uri = new Uri(path);
var request = UnityWebRequestTexture.GetTexture(uri, nonReadable: true);
yield return request.SendWebRequest();

if (request.isHttpError || request.isNetworkError)
{
    Debug.LogError($"Failed to load texture: {request.error}");
    yield break;
}

var texture = DownloadHandlerTexture.GetContent(request);
```

Here is what a (partial) profile looks like when loading 50 copies of the same png texture:

<img width="1612" height="662" alt="image" src="https://github.com/user-attachments/assets/ae1fdbf6-5205-4d3a-86bc-c7d206605fac" />

One caveat here is that Unity loads png textures as RGBA32. For large textures
this could result in issues due to running out of GPU memory. One option to
avoid this would be load them onto the GPU and then use a compute shader to
compress them to DXT1/DXT5. See this repo for an example of how to do this:
<https://github.com/aras-p/UnityGPUTexCompression>.

I am unconvinced that gpu compression is worth the effort. Better to just get people to
use asset bundles where it matters.

## Loading DDS Textures
Unity doesn't provide a good way to load large DDS textures efficiently. The
best we can do is combine a bunch of different async primitives. Unfortunately,
we are limited by the fact that `Texture2D` automatically uploads its initial
data to the GPU, and Unity 2019.4 doesn't have a way to prevent that.
(Later versions of Unity have the `DontUploadOnCreate` flag, which does what we
want here).

Through a collection of ~~black magic~~ undocumented hacks, the
`TextureLoadManager` class in this repo allows you to (somewhat) perform an
asynchronous load of a DDS texture. In short, it:
* Uses an asynchronous read to actually read the texture data off the disk.
* Uses undocumented unity flags to create an uninitialized texture.
* Calls `Texture2D.GetRawTextureData` while the disk read is being performed.
* Uses a dependent job to actually copy the data to the main texture.
* Calls `Texture2D.Apply` on the main thread.

Most of these operations are either fairly quick or can happen off the main
thread... except `Texture2D.GetRawTextureData`. It performs a fairly expensive
memcpy of all the uninitialized data within the texture and this usually results
in a ton of page faults, meaning it takes almost as long as the disk read.

As such, this implementation is only somewhat asynchronous. You still want to
batch together multiple texture reads, but there is a pretty significant amount
of work that needs to be done on the main thread so it is much more expensive
in that respect than the other options.

Here's what a profile of loading 50 copies of the same DDS image texture looks
like:

<img width="1770" height="647" alt="image" src="https://github.com/user-attachments/assets/4d109b15-fb66-4be0-8322-6c82a4e3eb1d" />

## Possible Future Options
I think I have exhausted all the options for asynchronous loading of textures
without using native rendering extensions. With a native rendering extension,
though, you could do the entire upload through the underlying rendering API.
All of those support asynchronous texture upload in one way or another.

However, I don't think this is worth the effort to figure out. I expect that
there would be all sorts of hard-to-reproduce bugs that couldn't be debugged
without having the specific platform and configuration of the user. I don't
think this is worth it, since there are options that are better supported by
Unity where it is not necessary to worry about these issues.

Unity is also doing some texture adjustments under the hood that I don't
understand, and it would be necessary to figure that out to make use of this.
