using System;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPTextureLoaderTests;

[KSPAddon(KSPAddon.Startup.EveryScene, once: false)]
internal class TestUI
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

    private static void Nothing() { }

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
}
