using KSP.UI.Screens.DebugToolbar.Screens;

namespace KSPTextureLoader.UI.Screens.LoadTexture;

internal class ReadableToggle : DebugScreenToggle
{
    LoadTextureScreenContent screen;

    protected override void SetupValues()
    {
        screen = GetComponentInParent<LoadTextureScreenContent>();
        SetToggle(false);
    }

    protected override void OnToggleChanged(bool state)
    {
        screen.options.Unreadable = !state;
    }
}
