using KSP.UI.Screens.DebugToolbar.Screens;

namespace KSPTextureLoader.UI.Screens.Main;

internal class UseAsyncReadManagerToggle : DebugScreenToggle
{
    protected override void SetupValues()
    {
        SetToggle(KSPTextureLoader.Config.Instance.UseAsyncReadManager);
    }

    protected override void OnToggleChanged(bool state)
    {
        KSPTextureLoader.Config.Instance.UseAsyncReadManager = state;
    }
}
