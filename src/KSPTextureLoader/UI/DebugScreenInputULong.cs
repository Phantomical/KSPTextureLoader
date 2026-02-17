namespace KSPTextureLoader.UI;

/// <summary>
/// Base class for debug screen input fields that accept an unsigned long value.
/// </summary>
internal abstract class DebugScreenInputULong : DebugScreenInput
{
    protected void SetValue(ulong value)
    {
        SetInputText(value.ToString());
    }

    protected sealed override void OnEndEdit(string text)
    {
        if (ulong.TryParse(text, out var value))
            OnValueChanged(value);
        else
            SetValue(GetValue());
    }

    protected abstract ulong GetValue();

    protected abstract void OnValueChanged(ulong value);
}
