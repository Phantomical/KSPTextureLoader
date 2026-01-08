using System;
using System.Collections;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPTextureLoader;

[KSPAddon(KSPAddon.Startup.AllGameScenes, once: false)]
internal class DebugUI : MonoBehaviour
{
    const int DefaultWidth = 600;
    const int DefaultHeight = 100;
    const int CloseButtonSize = 15;
    const int CloseButtonMargin = 5;

    static ApplicationLauncherButton button;
    static Texture2D ButtonTexture;
    static bool InitializedStatics = false;

    Rect window;
    bool showGUI = false;

    string texturePath = "";
    string assetBundle = "";
    TextureLoadHint hint = TextureLoadHint.BatchAsynchronous;
    Texture2D[] textures = [];

    void Start()
    {
        if (Config.Instance.DebugMode != DebugLevel.Info)
        {
            Destroy(this);
            return;
        }

        if (!InitializedStatics)
        {
            ButtonTexture = GameDatabase.Instance.GetTexture(
                "KSPTextureLoader/Textures/ToolbarIcon",
                false
            );
            InitializedStatics = true;
        }

        window = new Rect(
            Screen.width / 2 - DefaultWidth / 2,
            Screen.height / 2 - DefaultHeight / 2,
            DefaultWidth,
            DefaultHeight
        );

        if (button != null)
            return;

        button = ApplicationLauncher.Instance.AddModApplication(
            ShowToolbarGUI,
            HideToolbarGUI,
            Nothing,
            Nothing,
            Nothing,
            Nothing,
            ApplicationLauncher.AppScenes.ALWAYS,
            ButtonTexture
        );
    }

    void OnDestroy()
    {
        if (button != null)
            ApplicationLauncher.Instance.RemoveModApplication(button);
        button = null;
    }

    void ShowToolbarGUI()
    {
        showGUI = true;
    }

    void HideToolbarGUI()
    {
        showGUI = false;
    }

    void Nothing() { }

    void OnGUI()
    {
        if (!showGUI)
            return;

        window = GUILayout.Window(
            GetInstanceID(),
            window,
            DrawWindow,
            "KSP Texture Loader",
            HighLogic.Skin.window
        );
    }

    void DrawWindow(int windowId)
    {
        using var skin = new PushGUISkin(HighLogic.Skin);

        var closeButtonRect = new Rect(
            window.width - CloseButtonSize - CloseButtonMargin,
            CloseButtonMargin,
            CloseButtonSize,
            CloseButtonSize
        );
        if (GUI.Button(closeButtonRect, "X"))
            HideToolbarGUI();

        using var mainvert = new PushVertical();

        using (var horz = new PushHorizontal())
        {
            using (var vert = new PushVertical(GUILayout.MaxWidth(200f)))
            {
                GUILayout.Label("Texture Path", GUILayout.ExpandWidth(true));
                GUILayout.Label("Asset Bundle", GUILayout.ExpandWidth(true));
            }

            using (var vert = new PushVertical())
            {
                texturePath = GUILayout.TextField(texturePath, GUILayout.ExpandWidth(true));
                assetBundle = GUILayout.TextField(assetBundle, GUILayout.ExpandWidth(true));
            }
        }

        Config.Instance.AllowNativeUploads = GUILayout.Toggle(
            Config.Instance.AllowNativeUploads,
            "Allow Native Uploads"
        );

        if (GUILayout.Button("Load Texture"))
        {
            StartCoroutine(LoadTextureCoroutine());
        }

        if (GUILayout.Button("Load Cubemap"))
        {
            StartCoroutine(LoadCubemapCoroutine());
        }

        GUILayout.Space(5f);

        using (var horz = new PushHorizontal())
        {
            foreach (var texture in textures)
            {
                if (texture == null)
                    continue;

                var aspect = (float)texture.height / (float)texture.width;
                var width = Math.Min(DefaultWidth - 20f, texture.width);
                var height = width * aspect;
                GUILayout.Box(texture, GUILayout.Width(width), GUILayout.Height(height));
            }
        }

        GUI.DragWindow();
    }

    IEnumerator LoadTextureCoroutine()
    {
        var options = new TextureLoadOptions
        {
            AssetBundles = string.IsNullOrEmpty(assetBundle) ? [] : [assetBundle],
            Hint = hint,
        };
        var handle = TextureLoader.LoadTexture<Texture2D>(texturePath, options);
        yield return handle;

        DestroyAllTextures();

        try
        {
            handle.WaitUntilComplete();

            if (handle.AssetBundle is not null)
                Debug.Log(
                    $"[KSPTextureLoader] Loaded texture {handle.Path} from {handle.AssetBundle}"
                );
            else
                Debug.Log($"[KSPTextureLoader] Loaded texture {handle.Path}");

            textures = [handle.TakeTexture()];
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load texture {texturePath}");
            Debug.LogException(e);
        }
    }

    IEnumerator LoadCubemapCoroutine()
    {
        var options = new TextureLoadOptions
        {
            AssetBundles = string.IsNullOrEmpty(assetBundle) ? [] : [assetBundle],
            Hint = hint,
        };
        using var handle = TextureLoader.LoadTexture<Cubemap>(texturePath, options);
        yield return handle;

        DestroyAllTextures();

        textures = new Texture2D[6];
        var cubemap = handle.GetTexture();

        for (int i = 0; i < 6; ++i)
        {
            var texture = TextureUtils.CreateUninitializedTexture2D(
                cubemap.width,
                cubemap.height,
                cubemap.mipmapCount,
                cubemap.graphicsFormat
            );

            texture.Apply(false, true);
            Graphics.CopyTexture(cubemap, i, texture, 0);
            textures[i] = texture;
        }
    }

    void DestroyAllTextures()
    {
        textures ??= [];
        foreach (var texture in textures)
            Destroy(texture);

        textures = [];
    }

    readonly struct PushGUISkin : IDisposable
    {
        readonly GUISkin prev;

        public PushGUISkin(GUISkin skin)
        {
            prev = GUI.skin;
            GUI.skin = skin;
        }

        public readonly void Dispose()
        {
            GUI.skin = prev;
        }
    }

    readonly struct PushHorizontal : IDisposable
    {
        public PushHorizontal() => GUILayout.BeginHorizontal();

        public PushHorizontal(params GUILayoutOption[] options) =>
            GUILayout.BeginHorizontal(options);

        public readonly void Dispose() => GUILayout.EndHorizontal();
    }

    readonly struct PushVertical : IDisposable
    {
        public PushVertical() => GUILayout.BeginVertical();

        public PushVertical(params GUILayoutOption[] options) => GUILayout.BeginVertical(options);

        public readonly void Dispose() => GUILayout.EndVertical();
    }
}
