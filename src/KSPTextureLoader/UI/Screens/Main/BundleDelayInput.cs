namespace KSPTextureLoader.UI.Screens.Main;

internal class BundleDelayInput : DebugScreenInputInt
{
    protected override void SetupValues()
    {
        SetValue(KSPTextureLoader.Config.Instance.BundleUnloadDelay);
    }

    protected override int GetValue() => KSPTextureLoader.Config.Instance.BundleUnloadDelay;

    protected override void OnValueChanged(int value)
    {
        KSPTextureLoader.Config.Instance.BundleUnloadDelay = value;
    }
}
