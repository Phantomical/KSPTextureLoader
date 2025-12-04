using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSP.UI.Screens;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Profiling;

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
    Texture2D texture = null;

    void Start()
    {
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
            "Async Texture Load",
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

        if (GUILayout.Button("Load Texture"))
        {
            StartCoroutine(LoadTextureCoroutine());
        }

        GUILayout.Space(5f);

        if (texture != null)
        {
            var aspect = (float)texture.height / (float)texture.width;
            var width = Math.Min(window.width - 10f, 256f);
            var height = width * aspect;
            GUILayout.Box(texture, GUILayout.Width(width), GUILayout.Height(height));
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

        Destroy(texture);
        texture = null;

        try
        {
            handle.WaitUntilComplete();

            if (handle.AssetBundle is not null)
                Debug.Log(
                    $"[KSPTextureLoader] Loaded texture {handle.Path} from {handle.AssetBundle}"
                );
            else
                Debug.Log($"[KSPTextureLoader] Loaded texture {handle.Path}");

            texture = handle.TakeTexture();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load texture {texturePath}");
            Debug.LogException(e);
        }
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
