using KSP.UI.Screens.DebugToolbar;
using KSPTextureLoader.UI.Screens.LoadTexture;
using KSPTextureLoader.UI.Screens.Main;
using KSPTextureLoader.UI.Screens.Textures;
using UnityEngine;

namespace KSPTextureLoader.UI;

/// <summary>
/// Registers KSPTextureLoader into the Alt+F12 debug menu.
/// Runs once at MainMenu, after DebugScreenSpawner has created the debug screen.
/// </summary>
[KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
internal class DebugMenuScreen : MonoBehaviour
{
    void Start()
    {
        if (!DebugUIManager.Initialize())
        {
            Debug.LogError(
                "[KSPTextureLoader] Failed to initialize DebugUIManager, skipping debug menu registration"
            );
            return;
        }

        DebugUIManager.BuildWindowPrefab();
        TexturePreviewPopup.BuildPrefab();

        var mainScreen = DebugUIManager.CreateScreenPrefab<MainScreenContent>(
            "KSPTextureLoader_DebugScreen"
        );
        mainScreen.GetComponent<MainScreenContent>().BuildUI();
        AddDebugScreen(null, "KSPTextureLoader", "KSP Texture Loader", mainScreen);

        var texturesScreen = DebugUIManager.CreateScreenPrefab<TexturesScreenContent>(
            "KSPTextureLoader_TexturesScreen"
        );
        texturesScreen.GetComponent<TexturesScreenContent>().BuildUI();
        AddDebugScreen("KSPTextureLoader", "KSPTextureLoader_Textures", "Textures", texturesScreen);

        var loadTextureScreen = DebugUIManager.CreateScreenPrefab<LoadTextureScreenContent>(
            "KSPTextureLoader_LoadTextureScreen"
        );
        loadTextureScreen.GetComponent<LoadTextureScreenContent>().BuildUI();
        AddDebugScreen(
            "KSPTextureLoader",
            "KSPTextureLoader_LoadTexture",
            "Load Texture",
            loadTextureScreen
        );
    }

    static void AddDebugScreen(string parentName, string name, string text, RectTransform prefab)
    {
        DebugScreenSpawner.Instance.debugScreens.screens.Add(
            new LabelledScreenWrapper()
            {
                parentName = parentName,
                name = name,
                text = text,
                screen = prefab,
            }
        );
    }

    class LabelledScreenWrapper : AddDebugScreens.ScreenWrapper
    {
        public override string ToString() => name;
    }
}
