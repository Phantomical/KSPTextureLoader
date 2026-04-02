using System;
using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens.DebugToolbar;
using KSPTextureLoader.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader;

internal class TexturePreviewPopup : MonoBehaviour
{
    static GameObject _prefab;

    // 4x3 cross layout matching ConvertTexture2dToCubemap.
    // null entries are empty cells.
    static readonly CubemapFace?[,] CrossLayout =
    {
        { null, null, CubemapFace.NegativeY, null },
        {
            CubemapFace.NegativeZ,
            CubemapFace.NegativeX,
            CubemapFace.PositiveZ,
            CubemapFace.PositiveX,
        },
        { null, null, CubemapFace.PositiveY, null },
    };

    bool owned = true;
    TextureHandle handle;
    Texture texture;
    Texture2D[] cubemapFaceTextures;
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
    GameObject cubemapGrid;

    [SerializeField]
    RawImage[] cubemapFaceImages;

    // Order matches the cross layout iterated row-by-row, skipping empty cells.
    static readonly CubemapFace[] CubemapFaceOrder =
    {
        CubemapFace.NegativeY,
        CubemapFace.NegativeZ,
        CubemapFace.NegativeX,
        CubemapFace.PositiveZ,
        CubemapFace.PositiveX,
        CubemapFace.PositiveY,
    };

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

        // Cubemap cross grid (hidden by default)
        BuildCubemapGrid(popup, contentArea);

        // Scrollable error area (hidden by default)
        BuildErrorScrollView(popup, contentArea);

        _prefab = window;
    }

    static void BuildCubemapGrid(TexturePreviewPopup popup, Transform contentArea)
    {
        int rows = CrossLayout.GetLength(0);
        int cols = CrossLayout.GetLength(1);

        // Outer container participates in the content area's layout group.
        // The inner grid uses AspectRatioFitter.FitInParent to maintain 4:3,
        // avoiding conflicts between the layout group and the fitter.
        var containerGo = new GameObject("CubemapGrid", typeof(RectTransform));
        containerGo.transform.SetParent(contentArea, false);
        containerGo.SetActive(false);
        popup.cubemapGrid = containerGo;

        var containerLayout = containerGo.AddComponent<LayoutElement>();
        containerLayout.flexibleWidth = 1f;
        containerLayout.flexibleHeight = 1f;

        var gridGo = new GameObject("CubemapGridInner", typeof(RectTransform));
        gridGo.transform.SetParent(containerGo.transform, false);

        var gridFitter = gridGo.AddComponent<AspectRatioFitter>();
        gridFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        gridFitter.aspectRatio = (float)cols / rows;

        var gridVlg = gridGo.AddComponent<VerticalLayoutGroup>();
        gridVlg.childControlWidth = true;
        gridVlg.childControlHeight = true;
        gridVlg.childForceExpandWidth = true;
        gridVlg.childForceExpandHeight = true;
        gridVlg.spacing = 2f;

        var images = new List<RawImage>();

        for (int row = 0; row < rows; row++)
        {
            var rowGo = new GameObject($"Row{row}", typeof(RectTransform));
            rowGo.transform.SetParent(gridGo.transform, false);

            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = true;
            rowHlg.childForceExpandHeight = true;
            rowHlg.spacing = 2f;

            var rowLayout = rowGo.AddComponent<LayoutElement>();
            rowLayout.flexibleWidth = 1f;
            rowLayout.flexibleHeight = 1f;

            for (int col = 0; col < cols; col++)
            {
                var entry = CrossLayout[row, col];

                var cellGo = new GameObject(
                    entry.HasValue ? $"Cell_{entry.Value}" : $"Cell_Empty",
                    typeof(RectTransform)
                );
                cellGo.transform.SetParent(rowGo.transform, false);

                var cellLayout = cellGo.AddComponent<LayoutElement>();
                cellLayout.flexibleWidth = 1f;
                cellLayout.flexibleHeight = 1f;

                if (!entry.HasValue)
                    continue;

                var imgGo = new GameObject("FaceImage", typeof(RectTransform));
                imgGo.transform.SetParent(cellGo.transform, false);

                var faceImage = imgGo.AddComponent<RawImage>();
                faceImage.enabled = false;

                var fitter = imgGo.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 1f;

                images.Add(faceImage);
            }
        }

        popup.cubemapFaceImages = images.ToArray();
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

    static TexturePreviewPopup Create(string title)
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
        go.name = $"TexturePreviewPopup({title})";

        // Set title
        var titleText = go.transform.Find("VerticalLayout/TitleBar/TitleText");
        if (titleText != null)
        {
            var tmp = titleText.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                if (string.IsNullOrEmpty(title))
                    tmp.text = "<unnamed>";
                else
                    tmp.text = title;
            }
        }

        // Wire close button
        var exitButton = go.transform.Find("VerticalLayout/TitleBar/ExitButton");
        var closeButton = exitButton?.GetComponent<Button>();
        if (closeButton != null)
        {
            closeButton.onClick.RemoveAllListeners();
            closeButton.onClick.AddListener(() => Destroy(go));
        }
        var popup = go.GetComponent<TexturePreviewPopup>();

        go.SetActive(true);

        return popup;
    }

    public static TexturePreviewPopup Create(Texture texture, bool owned = true, string name = null)
    {
        var popup = Create(name ?? texture.name);
        popup.texture = texture;
        popup.owned = owned;
        return popup;
    }

    public static TexturePreviewPopup Create(TextureHandle handle)
    {
        var popup = Create(handle.Path);
        popup.handle = handle.Acquire();
        popup.owned = true;
        return popup;
    }

    void Start()
    {
        StartCoroutine(WaitForTexture());
    }

    IEnumerator WaitForTexture()
    {
        if (handle is not null)
        {
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
        }

        if (texture != null)
        {
            infoLabel.text = $"{texture.width}x{texture.height} {texture.graphicsFormat}";

            if (texture is Cubemap cubemap)
                ShowCubemap(cubemap);
            else
                ShowTexture(texture);
        }
        else if (exception != null)
        {
            infoLabel.text = "Error";
            textureContainer.SetActive(false);
            errorLabel.text = exception;
            errorScrollView.SetActive(true);
        }
    }

    void ShowTexture(Texture tex)
    {
        rawImage.texture = tex;
        rawImage.enabled = true;
        aspectFitter.aspectRatio = (float)tex.width / tex.height;
    }

    void ShowCubemap(Cubemap cubemap)
    {
        textureContainer.SetActive(false);

        cubemapFaceTextures = new Texture2D[cubemapFaceImages.Length];
        for (int i = 0; i < cubemapFaceImages.Length; i++)
        {
            var faceTex = TextureUtils.ExtractCubemapFace(cubemap, CubemapFaceOrder[i]);
            cubemapFaceTextures[i] = faceTex;
            cubemapFaceImages[i].texture = faceTex;
            cubemapFaceImages[i].enabled = true;
        }

        cubemapGrid.SetActive(true);
    }

    void OnDestroy()
    {
        if (cubemapFaceTextures != null)
        {
            foreach (var faceTex in cubemapFaceTextures)
            {
                if (faceTex != null)
                    Texture.Destroy(faceTex);
            }
        }

        if (owned)
        {
            if (handle is not null)
                handle.Dispose();
            else if (texture is not null)
                Texture.Destroy(texture);
        }

        cubemapFaceTextures = null;
        handle = null;
        texture = null;
    }
}
