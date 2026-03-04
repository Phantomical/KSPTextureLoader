using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI.Screens.CPUTextures;

internal class CPUTexturesScreenContent : MonoBehaviour
{
    [SerializeField]
    Transform listContainer;

    [SerializeField]
    CPUTextureSearchInput searchInput;

    static GameObject rowPrefab;

    internal void BuildUI()
    {
        rowPrefab = BuildRowPrefab();

        var content = transform;

        DebugUIManager.CreateHeader(content, "Loaded CPU Textures");

        // Search input field
        searchInput = DebugUIManager.CreateInput<CPUTextureSearchInput>(content);
        searchInput.inputField.placeholder.GetComponent<TextMeshProUGUI>().text =
            "Search CPU textures...";

        // Create a scroll view for the texture list.
        var scrollGo = new GameObject("CPUTextureListScroll", typeof(RectTransform));
        scrollGo.transform.SetParent(content, false);

        var scrollLe = scrollGo.AddComponent<LayoutElement>();
        scrollLe.flexibleHeight = 1f;
        scrollLe.flexibleWidth = 1f;

        var scrollRect = scrollGo.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        // Viewport
        var viewportGo = new GameObject("Viewport", typeof(RectTransform));
        viewportGo.transform.SetParent(scrollGo.transform, false);

        var viewportRect = viewportGo.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = new Vector2(-16f, 0f);

        viewportGo.AddComponent<CanvasRenderer>();
        var viewportImage = viewportGo.AddComponent<Image>();
        viewportImage.color = Color.white;
        viewportGo.AddComponent<Mask>().showMaskGraphic = false;

        scrollRect.viewport = viewportRect;

        // Content container inside viewport
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewportGo.transform, false);

        var contentRect = contentGo.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
        contentVlg.childControlWidth = true;
        contentVlg.childControlHeight = true;
        contentVlg.childForceExpandWidth = true;
        contentVlg.childForceExpandHeight = false;
        contentVlg.spacing = 0f;

        var contentCsf = contentGo.AddComponent<ContentSizeFitter>();
        contentCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.content = contentRect;

        // Vertical scrollbar
        DebugUIManager.CreateScrollbar(scrollGo.transform, scrollRect);

        listContainer = contentGo.transform;
        searchInput.listContainer = listContainer;
    }

    static GameObject BuildRowPrefab()
    {
        var go = new GameObject("CPUTextureRowPrefab", typeof(RectTransform));
        go.SetActive(false);
        GameObject.DontDestroyOnLoad(go);

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;
        hlg.spacing = 8f;

        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 24f;

        // Preview button (first)
        var btn = DebugUIManager.CreateButton<CPUTexturePreviewButton>(go.transform, "Preview");
        var btnLe = btn.GetComponent<LayoutElement>();
        if (btnLe == null)
            btnLe = btn.gameObject.AddComponent<LayoutElement>();
        btnLe.preferredWidth = 75f;
        btnLe.preferredHeight = 19f;
        btnLe.minHeight = -1f;
        btnLe.flexibleWidth = 0f;

        // Path label (flexible width to fill remaining space)
        var label = DebugUIManager.CreateLabel(go.transform, "");
        label.fontStyle = FontStyles.Normal;
        label.enableWordWrapping = true;
        var labelLe = label.GetComponent<LayoutElement>();
        if (labelLe == null)
            labelLe = label.gameObject.AddComponent<LayoutElement>();
        labelLe.flexibleWidth = 1f;

        var item = go.AddComponent<CPUTexturePreviewItem>();
        item.button = btn;
        item.label = label;

        return go;
    }

    void OnEnable()
    {
        var alive = new List<(string path, CPUTextureHandle handle)>();
        foreach (var (path, weak) in TextureLoader.cpuTextures)
        {
            if (weak.TryGetTarget(out var handle))
                alive.Add((path, handle));
        }

        alive.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

        foreach (var (_, handle) in alive)
            CreateItem(handle);
    }

    void OnDisable()
    {
        foreach (var item in listContainer.GetComponentsInChildren<CPUTexturePreviewItem>(true))
        {
            item.gameObject.SetActive(true);
            Destroy(item.gameObject);
        }
    }

    CPUTexturePreviewItem CreateItem(CPUTextureHandle handle)
    {
        var go = Instantiate(rowPrefab, listContainer, false);
        go.SetActive(true);
        go.name = $"CPUTextureRow({handle.Path})";

        var item = go.GetComponent<CPUTexturePreviewItem>();
        item.Initialize(handle);

        searchInput.ApplyFilter(item);

        return item;
    }
}

internal class CPUTexturePreviewItem : MonoBehaviour
{
    CPUTextureHandle handle;
    public CPUTexturePreviewButton button;
    public TextMeshProUGUI label;

    internal string Path => handle?.Path;

    internal void Initialize(CPUTextureHandle handle)
    {
        this.handle = handle;
        label.text = handle.Path;
        button.path = handle.Path;
    }

    void OnDestroy()
    {
        handle = null;
    }
}

internal class CPUTexturePreviewButton : DebugScreenButton
{
    internal string path;

    protected override void OnClick()
    {
        if (
            TextureLoader.cpuTextures.TryGetValue(path, out var weak)
            && weak.TryGetTarget(out var handle)
        )
        {
            try
            {
                var texture = handle.GetTexture().CompileToTexture();
                TexturePreviewPopup.Create(texture, owned: true);
            }
            catch (Exception e)
            {
                Debug.LogError($"[KSPTextureLoader] Failed to compile CPU texture '{path}'");
                Debug.LogException(e);
            }
        }
    }
}
