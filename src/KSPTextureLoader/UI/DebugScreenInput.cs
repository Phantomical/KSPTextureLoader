using TMPro;
using UnityEngine;

namespace KSPTextureLoader.UI;

/// <summary>
/// Base class for debug screen input fields, following the same pattern as
/// <see cref="KSP.UI.Screens.DebugToolbar.Screens.DebugScreenToggle"/> for toggles.
/// </summary>
internal abstract class DebugScreenInput : MonoBehaviour
{
    public TMP_InputField inputField;

    protected void Awake()
    {
        inputField.onEndEdit.AddListener(OnEndEdit);
        SetupValues();
    }

    protected void SetInputText(string text)
    {
        inputField.text = text;
    }

    protected virtual void SetupValues() { }

    protected abstract void OnEndEdit(string text);
}
