namespace KSPTextureLoader.UI;

/// <summary>
/// Base class for debug screen input fields that accept an integer value.
/// Mirrors <see cref="KSP.UI.Screens.DebugToolbar.Screens.DebugScreenInputDouble"/>
/// but for <see cref="int"/> and using <c>onEndEdit</c> instead of <c>onValueChanged</c>.
/// </summary>
internal abstract class DebugScreenInputInt : DebugScreenInput
{
    protected void SetValue(int value)
    {
        SetInputText(value.ToString());
    }

    protected sealed override void OnEndEdit(string text)
    {
        if (int.TryParse(text, out var value))
            OnValueChanged(value);
        else
            SetValue(GetValue());
    }

    protected abstract int GetValue();

    protected abstract void OnValueChanged(int value);
}
