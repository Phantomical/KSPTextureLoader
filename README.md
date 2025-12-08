# KSP Texture Loader

KSPTextureLoader is a library for KSP that takes care of loading textures as
quickly as possible with minimal overhead on the main thread.

Features:
* Asynchronous texture loading. Whenever possible work is moved off the main
  thread so that other stuff can be done in the meantime.
* Performance. Even when loading textures synchronously this should perform
  better then most naive texture loading implementations. This includes most
  "high performance" ones you see on the unity forums.
* Asset bundle support. By just using this library you can now automatically
  load textures out of asset bundles, no other code changes required.
* Broad(er) format support: BC7, that weird kopernicus palette format, and
  more! Future changes for texture formats become my problem, not yours.

Mods using KSPTextureLoader:
* None, at the moment. Support for Parallax Continued and Kopernicus is a work
  in progress.

## Working with KSPTextureLoader

* Want to use asset bundles within your planet pack?
  See [Shipping Textures Using Asset Bundles][shipping].
* Want to use KSPTextureLoader to load textures for your mod?
  See [Using KSPTextureLoader to Load Textures][loading].

[shipping]: https://github.com/Phantomical/KSPTextureLoader/wiki/Shipping-Textures-Using-Asset-Bundles
[loading]: https://github.com/Phantomical/KSPTextureLoader/wiki/Using-KSPTextureLoader-to-Load-Textures
