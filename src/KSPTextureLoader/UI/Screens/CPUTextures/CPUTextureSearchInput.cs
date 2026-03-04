using UnityEngine;

namespace KSPTextureLoader.UI.Screens.CPUTextures;

internal class CPUTextureSearchInput : DebugScreenInput
{
    public Transform listContainer;

    protected override void SetupValues()
    {
        inputField.onValueChanged.AddListener(_ => ApplyFilter());
    }

    protected override void OnEndEdit(string text)
    {
        ApplyFilter();
    }

    internal void ApplyFilter()
    {
        if (listContainer == null)
            return;

        var text = inputField.text;
        var hasSearch = !string.IsNullOrEmpty(text);

        foreach (var item in listContainer.GetComponentsInChildren<CPUTexturePreviewItem>(true))
        {
            item.gameObject.SetActive(!hasSearch || item.Path.Contains(text));
        }
    }

    internal void ApplyFilter(CPUTexturePreviewItem item)
    {
        var text = inputField.text;
        if (string.IsNullOrEmpty(text))
            item.gameObject.SetActive(true);
        else
            item.gameObject.SetActive(item.Path.Contains(text));
    }
}
