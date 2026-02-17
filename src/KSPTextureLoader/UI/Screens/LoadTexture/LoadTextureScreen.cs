using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI.Screens.LoadTexture;

internal class LoadTextureScreenContent : MonoBehaviour
{
    [SerializeField]
    TMP_InputField texturePathInput;

    [SerializeField]
    TMP_InputField assetBundleInput;

    // Runtime state (not serialized)
    internal TextureLoadOptions options = new();

    /// <summary>
    /// Builds the UI hierarchy. Called once during prefab creation.
    /// The prefab root is inactive, so Awake() does not fire yet.
    /// </summary>
    internal void BuildUI()
    {
        var content = transform;

        DebugUIManager.CreateLabel(content, "Texture Path");
        texturePathInput = DebugUIManager.CreateInputField(content);
        texturePathInput.placeholder.GetComponent<TextMeshProUGUI>().text =
            "e.g., Squad/Parts/Engine/solidBoosterRT5/model000";

        DebugUIManager.CreateLabel(content, "Asset Bundle");
        assetBundleInput = DebugUIManager.CreateInputField(content);
        assetBundleInput.placeholder.GetComponent<TextMeshProUGUI>().text = "e.g., mymod_assets";

        DebugUIManager.CreateSpacer(content);

        // Options section
        DebugUIManager.CreateHeader(content, "Options");

        DebugUIManager.CreateToggle<ReadableToggle>(content, "Readable");

        DebugUIManager.CreateLabeledButton<LinearCycleButton>(content, "Linear", "Default");
        DebugUIManager.CreateLabeledButton<HintCycleButton>(content, "Hint", "BatchAsynchronous");

        DebugUIManager.CreateSpacer(content);

        // Buttons row
        var btnRow = DebugUIManager.CreateHorizontalLayout(content);
        var btnHlg = btnRow.GetComponent<HorizontalLayoutGroup>();
        btnHlg.childControlWidth = true;
        btnHlg.childForceExpandWidth = true;
        DebugUIManager.CreateButton<LoadTextureButton>(btnRow.transform, "Load Texture");
        DebugUIManager.CreateButton<LoadCubemapButton>(btnRow.transform, "Load Cubemap");
    }

    TextureLoadOptions BuildOptions()
    {
        var bundle = assetBundleInput.text;
        options.AssetBundles = string.IsNullOrEmpty(bundle) ? [] : [bundle];
        return options;
    }

    internal void LoadTexture()
    {
        var path = texturePathInput.text;
        var handle = TextureLoader.LoadTexture<Texture2D>(path, BuildOptions());
        TexturePreviewPopup.Create(handle);
    }

    internal void LoadCubemap()
    {
        var path = texturePathInput.text;
        var handle = TextureLoader.LoadTexture<Cubemap>(path, BuildOptions());
        TexturePreviewPopup.Create(handle);
    }
}
