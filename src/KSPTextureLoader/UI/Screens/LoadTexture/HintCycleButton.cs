using System;
using TMPro;

namespace KSPTextureLoader.UI.Screens.LoadTexture;

internal class HintCycleButton : DebugScreenButton
{
    static readonly TextureLoadHint[] HintValues = (TextureLoadHint[])
        Enum.GetValues(typeof(TextureLoadHint));

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
        int current = Array.IndexOf(HintValues, screen.options.Hint);
        screen.options.Hint = HintValues[(current + 1) % HintValues.Length];
        UpdateText();
    }

    void UpdateText()
    {
        label.text = screen.options.Hint.ToString();
    }
}
