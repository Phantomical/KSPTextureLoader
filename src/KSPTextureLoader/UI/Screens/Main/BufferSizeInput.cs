namespace KSPTextureLoader.UI.Screens.Main;

internal class BufferSizeInput : DebugScreenInputInt
{
    protected override void SetupValues()
    {
        SetValue(KSPTextureLoader.Config.Instance.AsyncUploadBufferSize);
    }

    protected override int GetValue() => KSPTextureLoader.Config.Instance.AsyncUploadBufferSize;

    protected override void OnValueChanged(int value)
    {
        KSPTextureLoader.Config.Instance.AsyncUploadBufferSize = value;
        KSPTextureLoader.Config.Instance.Apply();
    }
}
