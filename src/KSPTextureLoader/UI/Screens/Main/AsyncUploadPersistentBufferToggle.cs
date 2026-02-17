using KSP.UI.Screens.DebugToolbar.Screens;

namespace KSPTextureLoader.UI.Screens.Main;

internal class AsyncUploadPersistentBufferToggle : DebugScreenToggle
{
    protected override void SetupValues()
    {
        SetToggle(KSPTextureLoader.Config.Instance.AsyncUploadPersistentBuffer);
    }

    protected override void OnToggleChanged(bool state)
    {
        KSPTextureLoader.Config.Instance.AsyncUploadPersistentBuffer = state;
    }
}
