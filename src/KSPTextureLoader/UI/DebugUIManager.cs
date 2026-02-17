using KSP.UI;
using KSP.UI.Screens.DebugToolbar;
using KSP.UI.Screens.DebugToolbar.Screens;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI;

/// <summary>
/// Finds and caches UI prefab templates from the existing KSP debug menu screens
/// so that our custom debug screen uses the same visual theme.
/// </summary>
internal static class DebugUIManager
{
    // Cached prefab templates (cloned from existing screens, stripped of behaviour scripts)
    static GameObject _labelPrefab;
    static GameObject _buttonPrefab;
    static GameObject _togglePrefab;
    static GameObject _inputFieldPrefab;
    static GameObject _spacerPrefab;
    static GameObject _scrollbarPrefab;
    static GameObject _windowPrefab;

    static bool _initialized;

    /// <summary>
    /// Must be called after DebugScreenSpawner has been set up (e.g. from MainMenu).
    /// Searches the existing debug screen prefabs for representative UI elements and
    /// clones them as reusable templates.
    /// </summary>
    public static bool Initialize()
    {
        if (_initialized)
            return true;

        var spawner = DebugScreenSpawner.Instance;
        if (spawner == null)
        {
            Debug.LogWarning(
                "[KSPTextureLoader] DebugUIManager: DebugScreenSpawner.Instance is null"
            );
            return false;
        }

        var screens = spawner.debugScreens?.screens;
        if (screens == null)
        {
            Debug.LogWarning("[KSPTextureLoader] DebugUIManager: No debug screens found");
            return false;
        }

        // Find prefabs from known screens.
        // "Debugging" screen has toggles with DebugScreenToggle wrapper pattern.
        // "Debug" (console) screen has button and input field in its BottomBar.
        // "Database" screen has labels with LayoutElement wrapper.
        // "Physics" screen has buttons in a horizontal layout with LayoutElement.

        foreach (var wrapper in screens)
        {
            if (wrapper.screen == null)
                continue;

            var root = wrapper.screen.gameObject;

            switch (wrapper.name)
            {
                case "Debug":
                    FindConsolePrefabs(root);
                    break;
                case "Database":
                    FindDatabasePrefabs(root);
                    break;
                case "Debugging":
                    FindDebuggingPrefabs(root);
                    break;
            }
        }

        // Clone the scrollbar from the sidebar's scroll view in the screen prefab
        if (_scrollbarPrefab == null && spawner.screenPrefab != null)
        {
            var contentsScrollView = spawner.screenPrefab.transform.Find(
                "VerticalLayout/HorizontalLayout/Contents/Contents Scroll View"
            );
            var scrollbar = contentsScrollView?.Find("Scrollbar");
            if (scrollbar != null)
            {
                _scrollbarPrefab = ClonePrefab(scrollbar.gameObject, "DebugUI_ScrollbarPrefab");
            }
        }

        // Spacer is simple enough to create directly
        if (_spacerPrefab == null)
        {
            _spacerPrefab = new GameObject("DebugUI_SpacerPrefab", typeof(RectTransform));
            _spacerPrefab.SetActive(false);
            var layout = _spacerPrefab.AddComponent<LayoutElement>();
            layout.minHeight = 8f;
            layout.preferredHeight = 8f;
            Object.DontDestroyOnLoad(_spacerPrefab);
        }

        _initialized =
            _labelPrefab != null
            && _buttonPrefab != null
            && _togglePrefab != null
            && _inputFieldPrefab != null;

        if (!_initialized)
        {
            Debug.LogWarning(
                $"[KSPTextureLoader] DebugUIManager: Failed to find all prefabs. "
                    + $"label={_labelPrefab != null}, button={_buttonPrefab != null}, "
                    + $"toggle={_togglePrefab != null}, inputField={_inputFieldPrefab != null}"
            );
        }

        return _initialized;
    }

    /// <summary>
    /// From the "Debug" console screen: find the button and input field in "BottomBar".
    /// Structure: ScreenConsole > BottomBar (HorizontalLayoutGroup) > InputField, Button
    /// </summary>
    static void FindConsolePrefabs(GameObject root)
    {
        var bottomBar = root.transform.Find("BottomBar");
        if (bottomBar == null)
            return;

        // InputField: has TMP_InputField + Image + LayoutElement
        if (_inputFieldPrefab == null)
        {
            var inputFieldGo = bottomBar.Find("InputField");
            if (inputFieldGo != null)
            {
                _inputFieldPrefab = ClonePrefab(
                    inputFieldGo.gameObject,
                    "DebugUI_InputFieldPrefab"
                );
            }
        }

        // Button: has Image + Button + LayoutElement, child "Text" with TMP
        if (_buttonPrefab == null)
        {
            var buttonGo = bottomBar.Find("Button");
            if (buttonGo != null)
            {
                _buttonPrefab = ClonePrefab(buttonGo.gameObject, "DebugUI_ButtonPrefab");
            }
        }
    }

    /// <summary>
    /// From the "Database" screen: find a label template.
    /// Structure: ScreenDatabaseOverview > TotalLabel (LayoutElement) > Text (TMP)
    /// </summary>
    static void FindDatabasePrefabs(GameObject root)
    {
        if (_labelPrefab != null)
            return;

        // Look for the "TotalLabel" child which has a LayoutElement + child Text with TMP
        var totalLabel = root.transform.Find("TotalLabel");
        if (totalLabel != null)
        {
            _labelPrefab = ClonePrefab(totalLabel.gameObject, "DebugUI_LabelPrefab");
        }
    }

    /// <summary>
    /// From the "Debugging" screen: find a toggle template.
    /// Structure: Debugging > PrintErrorsToScreen (LayoutElement + DebugScreenToggle)
    ///   > Toggle (Toggle) > Background (Image > Checkmark (Image)), Label (TMP)
    /// </summary>
    static void FindDebuggingPrefabs(GameObject root)
    {
        if (_togglePrefab != null)
            return;

        // "PrintErrorsToScreen" is the first toggle, a clean example
        var toggleWrapper = root.transform.Find("PrintErrorsToScreen");
        if (toggleWrapper != null)
        {
            _togglePrefab = ClonePrefab(toggleWrapper.gameObject, "DebugUI_TogglePrefab");

            // Remove the DebugScreenToggle script from the clone since it's KSP-specific behaviour
            var debugToggle = _togglePrefab.GetComponent<DebugScreenToggle>();
            if (debugToggle != null)
                Object.DestroyImmediate(debugToggle);
        }
    }

    /// <summary>
    /// The toggle prefab's inner "Toggle" child has a fixed width (e.g. 120px).
    /// Stretch it to fill the wrapper so the checkbox and label use the full width.
    /// </summary>
    static void StretchToggleChild(GameObject wrapper)
    {
        var innerToggle = wrapper.GetComponentInChildren<Toggle>(true);
        if (innerToggle == null)
            return;

        var rt = innerToggle.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static GameObject ClonePrefab(GameObject source, string name)
    {
        var clone = Object.Instantiate(source);
        clone.name = name;
        clone.SetActive(false);
        Object.DontDestroyOnLoad(clone);
        return clone;
    }

    // -- Window prefab --

    /// <summary>
    /// Clones the debug screen prefab and strips out debug-specific parts,
    /// producing a reusable window prefab with title bar, close button, and resize handles.
    /// Must be called after Initialize().
    /// </summary>
    public static void BuildWindowPrefab()
    {
        if (_windowPrefab != null)
            return;

        var spawner = DebugScreenSpawner.Instance;
        if (spawner?.screenPrefab == null)
        {
            Debug.LogWarning(
                "[KSPTextureLoader] DebugUIManager: Cannot build window prefab, screenPrefab is null"
            );
            return;
        }

        // Instantiate then immediately remove debug-specific components.
        // DebugScreen.Awake registers into a static list and hooks events,
        // but DestroyImmediate triggers OnDestroy which cleans all of that up.
        var go = Object.Instantiate(spawner.screenPrefab.gameObject);
        go.name = "DebugUI_WindowPrefab";
        go.SetActive(false);
        Object.DontDestroyOnLoad(go);

        // Remove debug-specific components from root
        var debugScreen = go.GetComponent<DebugScreen>();
        if (debugScreen != null)
            Object.DestroyImmediate(debugScreen);

        var addScreens = go.GetComponent<AddDebugScreens>();
        if (addScreens != null)
            Object.DestroyImmediate(addScreens);

        // Remove the root UIWindowArea (whole-window move; title bar has its own)
        var rootWindowArea = go.GetComponent<UIWindowArea>();
        if (rootWindowArea != null)
            Object.DestroyImmediate(rootWindowArea);

        // Remove ToggleContentsButton from title bar
        var titleBar = go.transform.Find("VerticalLayout/TitleBar");
        if (titleBar != null)
        {
            var toggleBtn = titleBar.Find("ToggleContentsButton");
            if (toggleBtn != null)
                Object.DestroyImmediate(toggleBtn.gameObject);

            // Use ellipsis for long title text instead of wrapping.
            // TMP wrapping inside a HorizontalLayoutGroup causes height
            // miscalculation (TMP reports preferred height at an intermediate
            // width, inflating the title bar).
            var titleText = titleBar.Find("TitleText");
            if (titleText != null)
            {
                var tmp = titleText.GetComponent<TMPro.TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                }
            }
        }

        // Destroy the entire HorizontalLayout (sidebar + content area)
        var horizontalLayout = go.transform.Find("VerticalLayout/HorizontalLayout");
        if (horizontalLayout != null)
            Object.DestroyImmediate(horizontalLayout.gameObject);

        // The original VerticalLayoutGroup has childForceExpandHeight=true which
        // causes the title bar to stretch vertically. Disable it so only the content
        // area (with flexibleHeight=1) expands.
        var verticalLayout = go.transform.Find("VerticalLayout");
        var vlg = verticalLayout.GetComponent<VerticalLayoutGroup>();
        if (vlg != null)
            vlg.childForceExpandHeight = false;

        // Create a new content area child of VerticalLayout
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(verticalLayout, false);

        var contentRect = contentGo.GetComponent<RectTransform>();
        contentRect.anchorMin = Vector2.zero;
        contentRect.anchorMax = Vector2.one;
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        var contentLayout = contentGo.AddComponent<LayoutElement>();
        contentLayout.flexibleHeight = 1f;
        contentLayout.flexibleWidth = 1f;

        var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
        contentVlg.childAlignment = TextAnchor.UpperLeft;
        contentVlg.childControlWidth = true;
        contentVlg.childControlHeight = true;
        contentVlg.childForceExpandWidth = true;
        contentVlg.childForceExpandHeight = false;
        contentVlg.padding = new RectOffset(8, 8, 8, 8);
        contentVlg.spacing = 4f;

        _windowPrefab = go;
    }

    /// <summary>
    /// Instantiates a window from the cached prefab, activates it, and returns the components.
    /// </summary>
    public static (GameObject window, Transform contentArea, Button closeButton) InstantiateWindow(
        string title,
        Transform parent,
        Vector2 size
    )
    {
        var result = InstantiateWindowPrefab(title, parent, size);
        if (result.window != null)
            result.window.SetActive(true);
        return result;
    }

    /// <summary>
    /// Instantiates a window from the cached prefab but leaves it inactive,
    /// so the caller can further configure it before activation.
    /// </summary>
    public static (
        GameObject window,
        Transform contentArea,
        Button closeButton
    ) InstantiateWindowPrefab(string title, Transform parent, Vector2 size)
    {
        if (_windowPrefab == null)
        {
            Debug.LogError("[KSPTextureLoader] DebugUIManager: Window prefab not built");
            return (null, null, null);
        }

        var go = Object.Instantiate(_windowPrefab, parent, false);
        go.name = $"Window_{title}";

        // Set title text
        var titleTextTransform = go.transform.Find("VerticalLayout/TitleBar/TitleText");
        if (titleTextTransform != null)
        {
            var tmp = titleTextTransform.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
                tmp.text = title;
        }

        // Get close button and clear inherited listeners
        var exitButtonTransform = go.transform.Find("VerticalLayout/TitleBar/ExitButton");
        Button closeButton = exitButtonTransform?.GetComponent<Button>();
        if (closeButton != null)
            closeButton.onClick.RemoveAllListeners();

        // Configure UIWindow sizing
        var uiWindow = go.GetComponent<UIWindow>();
        if (uiWindow != null)
        {
            uiWindow.minSize = new Vector2(200f, 150f);
            uiWindow.maxSizeIsScreen = true;
        }

        // Set initial size and center on screen
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = Vector2.zero;

        // Lock game input while the mouse is over the window
        var inputLock = go.AddComponent<DialogMouseEnterControlLock>();
        inputLock.Setup(ControlTypes.ALLBUTCAMERAS, $"KSPTextureLoader_{go.GetInstanceID()}");

        var contentArea = go.transform.Find("VerticalLayout/Content");

        return (go, contentArea, closeButton);
    }

    // -- Public factory methods --

    public static RectTransform CreateScreenPrefab<T>(string name)
        where T : MonoBehaviour
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.SetActive(false);
        Object.DontDestroyOnLoad(go);
        go.AddComponent<T>();

        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4f;
        vlg.padding = new RectOffset(8, 8, 8, 8);

        return rect;
    }

    public static TextMeshProUGUI CreateLabel(Transform parent, string text)
    {
        var go = Object.Instantiate(_labelPrefab, parent, false);
        go.SetActive(true);
        go.name = "Label";

        // Override inherited layout constraints so labels fill the available width
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
        }

        // The cloned "Text" child has fixed 200px width anchored left.
        // Stretch it to fill the parent instead.
        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        var textRect = tmp.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        tmp.text = text;

        return tmp;
    }

    public static TextMeshProUGUI CreateHeader(Transform parent, string text)
    {
        var tmp = CreateLabel(parent, text);
        tmp.fontStyle = FontStyles.Bold;
        tmp.fontSize *= 1.2f;
        return tmp;
    }

    public static Button CreateButton(
        Transform parent,
        string text,
        UnityEngine.Events.UnityAction onClick
    )
    {
        var go = Object.Instantiate(_buttonPrefab, parent, false);
        go.SetActive(true);
        go.name = "Button";

        SetupButtonPrefab(go);

        var btn = go.GetComponent<Button>();
        btn.onClick.RemoveAllListeners();
        if (onClick != null)
            btn.onClick.AddListener(onClick);

        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
            tmp.text = text;

        return btn;
    }

    public static T CreateButton<T>(Transform parent, string text)
        where T : DebugScreenButton
    {
        var go = Object.Instantiate(_buttonPrefab, parent, false);
        go.name = "Button";

        SetupButtonPrefab(go);

        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
            tmp.text = text;

        // Add the DebugScreenButton subclass before activating
        var component = go.AddComponent<T>();
        component.button = go.GetComponent<Button>();

        // Activating triggers Awake() which calls SetupValues() and hooks OnClick
        go.SetActive(true);

        return component;
    }

    static void SetupButtonPrefab(GameObject go)
    {
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
            layout.minHeight = 30f;
        }
    }

    public static T CreateToggle<T>(Transform parent, string label)
        where T : DebugScreenToggle
    {
        var go = Object.Instantiate(_togglePrefab, parent, false);
        go.name = "Toggle";

        // Override inherited layout constraints so toggles fill the available width
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
        }

        // The inner "Toggle" child has a fixed width — stretch it to fill the wrapper
        StretchToggleChild(go);

        // Add the custom DebugScreenToggle subclass before activating
        var component = go.AddComponent<T>();

        // Wire up the toggle and label references
        component.toggle = go.GetComponentInChildren<Toggle>();
        var labelTransform = component.toggle?.transform.Find("Label");
        if (labelTransform != null)
            component.toggleText = labelTransform.GetComponent<TextMeshProUGUI>();
        component.text = label;

        // Activating triggers Awake() which calls SetupValues() and hooks OnToggleChanged
        go.SetActive(true);

        return component;
    }

    public static Toggle CreateToggle(Transform parent, string label)
    {
        var go = Object.Instantiate(_togglePrefab, parent, false);
        go.name = "Toggle";

        // Override inherited layout constraints so toggles fill the available width
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
        }

        // The inner "Toggle" child has a fixed width — stretch it to fill the wrapper
        StretchToggleChild(go);

        go.SetActive(true);

        // The toggle is on a child named "Toggle"
        var toggle = go.GetComponentInChildren<Toggle>();

        // Set the label text
        var labelTransform = toggle?.transform.Find("Label");
        if (labelTransform != null)
        {
            var tmp = labelTransform.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
                tmp.text = label;
        }

        return toggle;
    }

    public static T CreateInput<T>(Transform parent)
        where T : DebugScreenInput
    {
        var go = Object.Instantiate(_inputFieldPrefab, parent, false);
        go.name = "InputField";

        SetupInputFieldPrefab(go);

        // Add the DebugScreenInput subclass before activating
        var component = go.AddComponent<T>();
        component.inputField = go.GetComponent<TMP_InputField>();

        // Activating triggers Awake() which calls SetupValues() and hooks OnEndEdit
        go.SetActive(true);

        return component;
    }

    public static TMP_InputField CreateInputField(Transform parent)
    {
        var go = Object.Instantiate(_inputFieldPrefab, parent, false);
        go.SetActive(true);
        go.name = "InputField";

        SetupInputFieldPrefab(go);

        var input = go.GetComponent<TMP_InputField>();
        input.text = "";

        return input;
    }

    static void SetupInputFieldPrefab(GameObject go)
    {
        // Override inherited layout constraints so input fields fill the available width
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
            layout.minHeight = 30f;
        }

        // Left-align the input text
        var input = go.GetComponent<TMP_InputField>();
        if (input?.textComponent != null)
            input.textComponent.alignment = TextAlignmentOptions.Left;
    }

    public static void CreateSpacer(Transform parent, float height = 8f)
    {
        var go = Object.Instantiate(_spacerPrefab, parent, false);
        go.SetActive(true);
        go.name = "Spacer";

        var layout = go.GetComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
    }

    public static GameObject CreateHorizontalLayout(Transform parent, float spacing = 8f)
    {
        var go = new GameObject("HorizontalLayout", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = false;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = 30f;

        return go;
    }

    /// <summary>
    /// Clones the scrollbar prefab from the KSP debug screen sidebar.
    /// The clone is parented to the given transform and wired up to the ScrollRect.
    /// </summary>
    public static Scrollbar CreateScrollbar(Transform parent, ScrollRect scrollRect)
    {
        var go = Object.Instantiate(_scrollbarPrefab, parent, false);
        go.SetActive(true);
        go.name = "Scrollbar";

        var scrollbar = go.GetComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        scrollRect.verticalScrollbar = scrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect
            .ScrollbarVisibility
            .AutoHideAndExpandViewport;
        scrollRect.verticalScrollbarSpacing = 0f;

        return scrollbar;
    }

    /// <summary>
    /// Creates a table row with a label on the left and a value label on the right.
    /// Returns the value TextMeshProUGUI so it can be updated at runtime.
    /// </summary>
    public static TextMeshProUGUI CreateTableRow(
        Transform parent,
        string name,
        string initialValue = ""
    )
    {
        var row = new GameObject("Row", typeof(RectTransform));
        row.transform.SetParent(parent, false);

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = false;

        var rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.minHeight = 20f;

        // Name label (left half)
        var nameTmp = CreateLabel(row.transform, name);
        nameTmp.fontStyle = FontStyles.Normal;

        // Value label (right half)
        var valueTmp = CreateLabel(row.transform, initialValue);
        valueTmp.fontStyle = FontStyles.Normal;
        valueTmp.alignment = TextAlignmentOptions.MidlineRight;

        return valueTmp;
    }

    /// <summary>
    /// Creates a horizontal row with a label on the left and an input field on the right,
    /// each taking roughly half the available width.
    /// </summary>
    public static T CreateLabeledInput<T>(
        Transform parent,
        string label,
        TextAnchor alignment = TextAnchor.MiddleLeft
    )
        where T : DebugScreenInput
    {
        var row = new GameObject("Row", typeof(RectTransform));
        row.transform.SetParent(parent, false);

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childAlignment = alignment;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = false;

        var rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.minHeight = 30f;

        // Label (left half)
        CreateLabel(row.transform, label);

        // Input field (right half)
        return CreateInput<T>(row.transform);
    }

    /// <summary>
    /// Creates a horizontal row with a label on the left and a button on the right,
    /// each taking roughly half the available width.
    /// </summary>
    public static T CreateLabeledButton<T>(Transform parent, string label, string buttonText)
        where T : DebugScreenButton
    {
        var row = new GameObject("Row", typeof(RectTransform));
        row.transform.SetParent(parent, false);

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = false;

        var rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.minHeight = 30f;

        // Label (left half)
        CreateLabel(row.transform, label);

        // Button (right half)
        return CreateButton<T>(row.transform, buttonText);
    }
}
