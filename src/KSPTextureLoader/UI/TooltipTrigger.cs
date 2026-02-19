using UnityEngine;
using UnityEngine.EventSystems;

namespace KSPTextureLoader.UI;

/// <summary>
/// Attach to any UI element to show a tooltip on hover.
/// The tooltip text is displayed in a shared panel managed by <see cref="TooltipManager"/>.
/// </summary>
internal class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string text;

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!string.IsNullOrEmpty(text))
            TooltipManager.Show(text, GetComponent<RectTransform>(), eventData.position);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        TooltipManager.Hide();
    }

    void OnDisable()
    {
        TooltipManager.Hide();
    }
}
