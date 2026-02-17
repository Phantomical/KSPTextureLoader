namespace KSPTextureLoader.UI.Screens.Main;

internal class MaxMemInput : DebugScreenInputULong
{
    protected override void SetupValues()
    {
        SetValue(KSPTextureLoader.Config.Instance.MaxTextureLoadMemory);
    }

    protected override ulong GetValue() => KSPTextureLoader.Config.Instance.MaxTextureLoadMemory;

    protected override void OnValueChanged(ulong value)
    {
        KSPTextureLoader.Config.Instance.MaxTextureLoadMemory = value;
    }
}
