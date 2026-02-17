using System;
using System.Collections;
using KSP.UI.Screens.DebugToolbar;
using KSPTextureLoader.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader;

internal class TexturePreviewPopup : MonoBehaviour
{
    static GameObject _prefab;

    TextureHandle handle;
    Texture texture;
    string exception;

    [SerializeField]
    TextMeshProUGUI infoLabel;

    [SerializeField]
    RawImage rawImage;

    [SerializeField]
    AspectRatioFitter aspectFitter;

    [SerializeField]
    GameObject textureContainer;

    [SerializeField]
    GameObject errorScrollView;

    [SerializeField]
    TextMeshProUGUI errorLabel;

    /// <summary>
    /// Builds the popup prefab from the window prefab. Called once at startup.
    /// </summary>
    public static void BuildPrefab()
    {
        if (_prefab != null)
            return;

        var (window, contentArea, _) = DebugUIManager.InstantiateWindowPrefab(
            "",
            null,
            new Vector2(420, 300)
        );

        if (window == null)
            return;

        GameObject.DontDestroyOnLoad(window);
        window.name = "TexturePreviewPopup_Prefab";

        // Add the component to the prefab (inactive, so Awake/Start won't fire)
        var popup = window.AddComponent<TexturePreviewPopup>();

        // Info label
        popup.infoLabel = DebugUIManager.CreateLabel(contentArea, "Loading...");

        // Container participates in the layout group; the RawImage inside it uses
        // AspectRatioFitter.FitInParent relative to the container, avoiding conflicts
        // between the layout group and the fitter during window drag.
        var containerGo = new GameObject("TextureContainer", typeof(RectTransform));
        containerGo.transform.SetParent(contentArea, false);
        popup.textureContainer = containerGo;

        var containerLayout = containerGo.AddComponent<LayoutElement>();
        containerLayout.flexibleWidth = 1f;
        containerLayout.flexibleHeight = 1f;

        var imageGo = new GameObject("TextureImage", typeof(RectTransform));
        imageGo.transform.SetParent(containerGo.transform, false);

        popup.rawImage = imageGo.AddComponent<RawImage>();
        popup.rawImage.enabled = false;

        popup.aspectFitter = imageGo.AddComponent<AspectRatioFitter>();
        popup.aspectFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        popup.aspectFitter.aspectRatio = 1f;

        // Scrollable error area (hidden by default)
        BuildErrorScrollView(popup, contentArea);

        _prefab = window;
    }

    static void BuildErrorScrollView(TexturePreviewPopup popup, Transform contentArea)
    {
        // Root object that participates in the content area's VerticalLayoutGroup
        var scrollViewGo = new GameObject("ErrorScrollView", typeof(RectTransform));
        scrollViewGo.transform.SetParent(contentArea, false);
        scrollViewGo.SetActive(false);
        popup.errorScrollView = scrollViewGo;

        var scrollViewLayout = scrollViewGo.AddComponent<LayoutElement>();
        scrollViewLayout.flexibleWidth = 1f;
        scrollViewLayout.flexibleHeight = 1f;

        // Viewport with mask
        var viewportGo = new GameObject("Viewport", typeof(RectTransform));
        viewportGo.transform.SetParent(scrollViewGo.transform, false);
        var viewportRect = viewportGo.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewportGo.AddComponent<RectMask2D>();

        // Content inside viewport — sized by text
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewportGo.transform, false);
        var contentRect = contentGo.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = Vector2.one;
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        var contentLayout = contentGo.AddComponent<VerticalLayoutGroup>();
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;

        var fitter = contentGo.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Error text
        popup.errorLabel = DebugUIManager.CreateLabel(contentGo.transform, "");

        // ScrollRect
        var scrollRect = scrollViewGo.AddComponent<ScrollRect>();
        scrollRect.viewport = viewportRect;
        scrollRect.content = contentRect;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        // Scrollbar
        DebugUIManager.CreateScrollbar(scrollViewGo.transform, scrollRect);
    }

    public static TexturePreviewPopup Create(TextureHandle handle)
    {
        if (_prefab == null)
        {
            Debug.LogError("[KSPTextureLoader] TexturePreviewPopup: Prefab not built");
            return null;
        }

        // Find the canvas from the debug screen
        var canvas = DebugScreenSpawner.Instance.screen?.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[KSPTextureLoader] TexturePreviewPopup: No canvas found");
            return null;
        }

        var go = GameObject.Instantiate(_prefab, canvas.transform, false);
        go.name = $"TexturePreviewPopup({handle.Path})";

        var popup = go.GetComponent<TexturePreviewPopup>();
        popup.handle = handle.Acquire();

        // Set title
        var titleText = go.transform.Find("VerticalLayout/TitleBar/TitleText");
        if (titleText != null)
        {
            var tmp = titleText.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
                tmp.text = handle.Path;
        }

        // Wire close button
        var exitButton = go.transform.Find("VerticalLayout/TitleBar/ExitButton");
        var closeButton = exitButton?.GetComponent<Button>();
        if (closeButton != null)
        {
            closeButton.onClick.RemoveAllListeners();
            closeButton.onClick.AddListener(() => Destroy(go));
        }

        go.SetActive(true);

        return popup;
    }

    void Start()
    {
        StartCoroutine(WaitForTexture());
    }

    IEnumerator WaitForTexture()
    {
        if (handle is null)
            yield break;

        using var guard = handle.Acquire();
        yield return handle;

        try
        {
            texture = handle.GetTexture();
        }
        catch (Exception e)
        {
            exception = e.ToString();
        }

        if (texture != null)
        {
            infoLabel.text = $"{texture.width}x{texture.height} {texture.graphicsFormat}";
            rawImage.texture = texture;
            rawImage.enabled = true;
            aspectFitter.aspectRatio = (float)texture.width / texture.height;
        }
        else if (exception != null)
        {
            infoLabel.text = "Error";
            textureContainer.SetActive(false);
            errorLabel.text = exception;
            errorScrollView.SetActive(true);
        }
    }

    void OnDestroy()
    {
        handle?.Dispose();
        handle = null;
        texture = null;
    }
}
