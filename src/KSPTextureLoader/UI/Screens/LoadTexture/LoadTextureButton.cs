namespace KSPTextureLoader.UI.Screens.LoadTexture;

internal class LoadTextureButton : DebugScreenButton
{
    LoadTextureScreenContent screen;

    protected override void SetupValues()
    {
        screen = GetComponentInParent<LoadTextureScreenContent>();
    }

    protected override void OnClick()
    {
        screen.LoadTexture();
    }
}
