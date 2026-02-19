using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI;

/// <summary>
/// Manages a shared tooltip panel that is shown/hidden by <see cref="TooltipTrigger"/>.
/// The panel is created lazily and positioned below the hovered element.
/// </summary>
internal static class TooltipManager
{
    static GameObject _panel;
    static TextMeshProUGUI _text;
    static RectTransform _panelRect;
    static Canvas _canvas;

    public static void Show(string text, RectTransform anchor, Vector2 mouseScreenPos)
    {
        if (_panel == null)
            CreatePanel();
        if (_panel == null)
            return;

        // Reparent to the same canvas as the anchor so it renders on top
        var canvas = anchor.GetComponentInParent<Canvas>()?.rootCanvas;
        if (canvas != null && canvas != _canvas)
        {
            _canvas = canvas;
            _panel.transform.SetParent(canvas.transform, false);
        }

        _text.text = text;

        // Force layout rebuild so we get the correct size
        LayoutRebuilder.ForceRebuildLayoutImmediate(_panelRect);

        // Position below the anchor, horizontally aligned with the mouse
        PositionBelow(anchor, mouseScreenPos);

        _panel.transform.SetAsLastSibling();
        _panel.SetActive(true);
    }

    public static void Hide()
    {
        if (_panel != null)
            _panel.SetActive(false);
    }

    static void PositionBelow(RectTransform anchor, Vector2 mouseScreenPos)
    {
        // Get the anchor's world-space corners: [0]=bottomLeft, [1]=topLeft, [2]=topRight, [3]=bottomRight
        var corners = new Vector3[4];
        anchor.GetWorldCorners(corners);

        // Use the anchor's bottom edge for vertical position, mouse X for horizontal
        float bottomY = corners[0].y;
        var canvasRect = _canvas.GetComponent<RectTransform>();

        // Convert anchor bottom Y to screen space
        var bottomScreen = RectTransformUtility.WorldToScreenPoint(
            _canvas.worldCamera,
            new Vector3(0, bottomY, 0)
        );

        // Convert combined position (mouse X, anchor bottom Y) to canvas local space
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            new Vector2(mouseScreenPos.x, bottomScreen.y),
            _canvas.worldCamera,
            out var localPoint
        );

        _panelRect.anchoredPosition = new Vector2(localPoint.x, localPoint.y - 4f);

        // Clamp to stay within the canvas bounds
        ClampToCanvas(canvasRect);
    }

    static void ClampToCanvas(RectTransform canvasRect)
    {
        var pos = _panelRect.anchoredPosition;
        var size = _panelRect.sizeDelta;
        var canvasSize = canvasRect.sizeDelta;

        float halfW = size.x * _panelRect.pivot.x;
        float halfH = size.y * _panelRect.pivot.y;

        float minX = -canvasSize.x * 0.5f + halfW;
        float maxX = canvasSize.x * 0.5f - (size.x - halfW);
        float minY = -canvasSize.y * 0.5f + halfH;
        float maxY = canvasSize.y * 0.5f - (size.y - halfH);

        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.y = Mathf.Clamp(pos.y, minY, maxY);

        _panelRect.anchoredPosition = pos;
    }

    static void CreatePanel()
    {
        // Panel background
        _panel = new GameObject("TooltipPanel", typeof(RectTransform), typeof(CanvasRenderer));
        Object.DontDestroyOnLoad(_panel);

        _panelRect = _panel.GetComponent<RectTransform>();
        _panelRect.pivot = new Vector2(0.5f, 1f); // top-center pivot so it hangs below
        _panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        _panelRect.anchorMax = new Vector2(0.5f, 0.5f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.15f, 0.15f, 0.15f, 0.95f);

        // Content size fitter so the panel auto-sizes to fit text
        var csf = _panel.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Padding via HorizontalLayoutGroup
        var hlg = _panel.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(8, 8, 4, 4);
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        // Text child
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
        textGo.transform.SetParent(_panel.transform, false);

        _text = textGo.AddComponent<TextMeshProUGUI>();
        _text.fontSize = 12f;
        _text.color = new Color(0.9f, 0.9f, 0.9f, 1f);
        _text.enableWordWrapping = true;
        _text.overflowMode = TextOverflowModes.Overflow;

        var textLayout = textGo.AddComponent<LayoutElement>();
        textLayout.preferredWidth = 300f;

        _panel.SetActive(false);
    }
}
