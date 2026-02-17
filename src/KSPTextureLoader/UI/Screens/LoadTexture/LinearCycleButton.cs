using TMPro;

namespace KSPTextureLoader.UI.Screens.LoadTexture;

internal class LinearCycleButton : DebugScreenButton
{
    LoadTextureScreenContent screen;
    public TextMeshProUGUI label;

    protected override void SetupValues()
    {
        screen = GetComponentInParent<LoadTextureScreenContent>();
        label ??= button.GetComponentInChildren<TextMeshProUGUI>();
        UpdateText();
    }

    protected override void OnClick()
    {
        // Cycle: Default (null) → Linear (true) → sRGB (false) → Default
        screen.options.Linear = screen.options.Linear switch
        {
            null => true,
            true => false,
            false => null,
        };
        UpdateText();
    }

    void UpdateText()
    {
        label.text = screen.options.Linear switch
        {
            null => "Default",
            true => "Linear",
            false => "sRGB",
        };
    }
}
