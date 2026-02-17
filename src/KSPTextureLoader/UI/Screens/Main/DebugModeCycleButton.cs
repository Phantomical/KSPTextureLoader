using System;
using TMPro;

namespace KSPTextureLoader.UI.Screens.Main;

internal class DebugModeCycleButton : DebugScreenButton
{
    private static readonly DebugLevel[] EnumValues = (DebugLevel[])
        Enum.GetValues(typeof(DebugLevel));

    public TextMeshProUGUI label;

    protected override void SetupValues()
    {
        label ??= button.GetComponentInChildren<TextMeshProUGUI>();
        UpdateText();
    }

    protected override void OnClick()
    {
        var config = KSPTextureLoader.Config.Instance;
        int current = Array.IndexOf(EnumValues, config.DebugMode);
        config.DebugMode = EnumValues[(current + 1) % EnumValues.Length];
        UpdateText();
    }

    void UpdateText()
    {
        label.text = KSPTextureLoader.Config.Instance.DebugMode.ToString();
    }
}
