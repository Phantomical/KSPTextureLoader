using KSP.UI.Screens.DebugToolbar.Screens;

namespace KSPTextureLoader.UI.Screens.Main;

internal class AllowNativeUploadsToggle : DebugScreenToggle
{
    protected override void SetupValues()
    {
        SetToggle(KSPTextureLoader.Config.Instance.AllowNativeUploads);
    }

    protected override void OnToggleChanged(bool state)
    {
        KSPTextureLoader.Config.Instance.AllowNativeUploads = state;
    }
}
