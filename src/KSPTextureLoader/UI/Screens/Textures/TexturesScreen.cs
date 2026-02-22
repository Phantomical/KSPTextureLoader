using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI.Screens.Textures;

internal class TexturesScreenContent : MonoBehaviour
{
    [SerializeField]
    Transform listContainer;

    [SerializeField]
    TextureSearchInput searchInput;

    static GameObject rowPrefab;

    internal void BuildUI()
    {
        rowPrefab = BuildRowPrefab();

        var content = transform;

        DebugUIManager.CreateHeader(content, "Loaded Textures");

        // Search input field
        searchInput = DebugUIManager.CreateInput<TextureSearchInput>(content);
        searchInput.inputField.placeholder.GetComponent<TextMeshProUGUI>().text =
            "Search textures...";

        // Create a scroll view for the texture list.
        // The parent ContentTransform has no ScrollRect, so screens that need
        // scrolling must provide their own.
        var scrollGo = new GameObject("TextureListScroll", typeof(RectTransform));
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

        // Vertical scrollbar — clone from the KSP sidebar prefab
        DebugUIManager.CreateScrollbar(scrollGo.transform, scrollRect);

        listContainer = contentGo.transform;
        searchInput.listContainer = listContainer;
    }

    static GameObject BuildRowPrefab()
    {
        var go = new GameObject("TextureRowPrefab", typeof(RectTransform));
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
        var btn = DebugUIManager.CreateButton<TexturePreviewButton>(go.transform, "Preview");
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

        var item = go.AddComponent<TexturePreviewItem>();
        item.button = btn;
        item.label = label;

        return go;
    }

    void OnEnable()
    {
        var alive = new List<(string path, TextureHandleImpl handle)>();
        foreach (var (path, weak) in TextureLoader.textures)
        {
            if (weak.TryGetTarget(out var handle))
                alive.Add((path, handle));
        }

        alive.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

        foreach (var (_, handle) in alive)
            CreateItem(handle);

        TextureHandleImpl.HandleCreated.Add(OnHandleCreated);
    }

    void OnDisable()
    {
        TextureHandleImpl.HandleCreated.Remove(OnHandleCreated);

        // Destroy all items
        foreach (var item in listContainer.GetComponentsInChildren<TexturePreviewItem>(true))
        {
            item.gameObject.SetActive(true);
            Destroy(item.gameObject);
        }
    }

    void OnHandleCreated(TextureHandleImpl handle)
    {
        var item = CreateItem(handle);

        // Insert in sorted position by path
        int siblingIndex = 0;
        for (int i = 0; i < listContainer.childCount; i++)
        {
            var sibling = listContainer.GetChild(i).GetComponent<TexturePreviewItem>();
            if (sibling == null || sibling == item)
                continue;
            if (string.CompareOrdinal(handle.Path, sibling.Path) > 0)
                siblingIndex = i + 1;
        }

        item.transform.SetSiblingIndex(siblingIndex);
    }

    TexturePreviewItem CreateItem(TextureHandleImpl handle)
    {
        var go = Instantiate(rowPrefab, listContainer, false);
        go.SetActive(true);
        go.name = $"TextureRow({handle.Path})";

        var item = go.GetComponent<TexturePreviewItem>();
        item.Initialize(handle);

        searchInput.ApplyFilter(item);

        return item;
    }
}

internal class TexturePreviewItem : MonoBehaviour
{
    TextureHandleImpl handle;
    public TexturePreviewButton button;
    public TextMeshProUGUI label;

    internal string Path => handle?.Path;

    internal void Initialize(TextureHandleImpl handle)
    {
        this.handle = handle;
        label.text = handle.Path;
        button.path = handle.Path;
        TextureHandleImpl.HandleDestroyed.Add(OnHandleDestroyed);
    }

    void OnHandleDestroyed(TextureHandleImpl destroyed)
    {
        if (!ReferenceEquals(destroyed, handle))
            return;

        TextureHandleImpl.HandleDestroyed.Remove(OnHandleDestroyed);
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        TextureHandleImpl.HandleDestroyed.Remove(OnHandleDestroyed);
        handle = null;
    }
}

internal class TexturePreviewButton : DebugScreenButton
{
    internal string path;

    protected override void OnClick()
    {
        if (
            TextureLoader.textures.TryGetValue(path, out var weak)
            && weak.TryGetTarget(out var handleImpl)
        )
        {
            var textureHandle = new TextureHandle(handleImpl);
            TexturePreviewPopup.Create(textureHandle);
        }
    }
}
