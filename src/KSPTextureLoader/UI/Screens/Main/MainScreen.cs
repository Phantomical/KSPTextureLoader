using System.Text;
using KSP.UI.Screens.DebugToolbar;
using KSPTextureLoader.UI.Screens.Textures;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KSPTextureLoader.UI.Screens.Main;

internal class MainScreenContent : MonoBehaviour
{
    [SerializeField]
    TextMeshProUGUI loadedTexturesValue;

    [SerializeField]
    TextMeshProUGUI cpuTexturesValue;

    [SerializeField]
    TextMeshProUGUI assetBundlesValue;

    /// <summary>
    /// Builds the UI hierarchy. Called once during prefab creation.
    /// The prefab root is inactive, so Awake() does not fire yet.
    /// </summary>
    internal void BuildUI()
    {
        var content = transform;

        // Configuration section
        DebugUIManager.CreateHeader(content, "Configuration");

        DebugUIManager.CreateToggle<AllowNativeUploadsToggle>(
            content,
            "Allow Native Uploads",
            "Allow direct use of native rendering extensions to upload textures to the GPU."
        );
        DebugUIManager.CreateToggle<AsyncUploadPersistentBufferToggle>(
            content,
            "Async Upload Persistent Buffer",
            "Keep the upload buffer allocated when there are no pending asset bundle loads."
        );
        DebugUIManager.CreateToggle<UseAsyncReadManagerToggle>(
            content,
            "Use Async Read Manager",
            "Use Unity's AsyncReadManager for file reads. If disabled, reads are done in a job."
        );

        DebugUIManager.CreateLabeledButton<DebugModeCycleButton>(
            content,
            "Debug Mode",
            "...",
            "Log verbosity level. Info shows warnings only, Debug adds load events, Trace logs everything."
        );

        DebugUIManager.CreateLabeledInput<BundleDelayInput>(
            content,
            "Bundle Unload Delay",
            TextAnchor.MiddleCenter,
            "Frames to keep asset bundles loaded after use. Set higher than the time needed to load all assets from bundles."
        );
        DebugUIManager.CreateLabeledInput<BufferSizeInput>(
            content,
            "Async Upload Buffer Size (MB)",
            TextAnchor.MiddleCenter,
            "Size of the buffer Unity uses for background texture uploads."
        );
        DebugUIManager.CreateLabeledInput<MaxMemInput>(
            content,
            "Max Texture Load Memory (MB)",
            TextAnchor.MiddleCenter,
            "Maximum temporary memory for async texture loading. Does not include the textures themselves."
        );

        DebugUIManager.CreateSpacer(content);

        // Statistics section
        DebugUIManager.CreateHeader(content, "Statistics");
        loadedTexturesValue = DebugUIManager.CreateTableRow(content, "Loaded Textures", "...");
        cpuTexturesValue = DebugUIManager.CreateTableRow(content, "CPU Textures", "...");
        assetBundlesValue = DebugUIManager.CreateTableRow(content, "Asset Bundles", "...");

        DebugUIManager.CreateSpacer(content);

        // Dump tools section
        DebugUIManager.CreateHeader(content, "Dump Tools");

        var btnRow1 = DebugUIManager.CreateHorizontalLayout(content);
        var btnHlg1 = btnRow1.GetComponent<HorizontalLayoutGroup>();
        btnHlg1.childControlWidth = true;
        btnHlg1.childForceExpandWidth = true;

        var dumpHandleRefs = DebugUIManager.CreateButton<DumpHandleReferencesButton>(
            btnRow1.transform,
            "Dump Handle Refs"
        );
        DebugUIManager.AttachTooltip(
            dumpHandleRefs.gameObject,
            "Dump the scene paths to all objects which hold a reference to a texture handle to a file."
        );

        var btnRow2 = DebugUIManager.CreateHorizontalLayout(content);
        var btnHlg2 = btnRow2.GetComponent<HorizontalLayoutGroup>();
        btnHlg2.childControlWidth = true;
        btnHlg2.childForceExpandWidth = true;

        var dumpPrefab = DebugUIManager.CreateButton<DumpPrefabButton>(
            btnRow2.transform,
            "Dump Prefab"
        );
        DebugUIManager.AttachTooltip(
            dumpPrefab.gameObject,
            "Dump the KSP debug screen prefab hierarchy to a log file."
        );

        var dumpLayout = DebugUIManager.CreateButton<DumpLayoutButton>(
            btnRow2.transform,
            "Dump Layout"
        );
        DebugUIManager.AttachTooltip(
            dumpLayout.gameObject,
            "Dump the live layout of all debug screens and the parent chain to a log file."
        );

        var dumpTexLayout = DebugUIManager.CreateButton<DumpTexturesLayoutButton>(
            btnRow2.transform,
            "Dump Textures Layout"
        );
        DebugUIManager.AttachTooltip(
            dumpTexLayout.gameObject,
            "Dump the textures screen layout and parent chain to a log file."
        );
    }

    void Update()
    {
        var loader = TextureLoader.Instance;
        if (loader == null)
            return;

        int alive = 0;
        foreach (var (_, weak) in loader.textures)
        {
            if (weak.TryGetTarget(out _))
                alive++;
        }

        loadedTexturesValue.text = alive.ToString();
        cpuTexturesValue.text = loader.cpuTextures.Count.ToString();
        assetBundlesValue.text = loader.assetBundles.Count.ToString();
    }

    internal void DumpScreenPrefab()
    {
        var spawner = DebugScreenSpawner.Instance;
        if (spawner == null || spawner.screenPrefab == null)
        {
            Debug.Log("[KSPTextureLoader] DebugScreenSpawner.Instance or screenPrefab is null");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("DebugScreenSpawner.screenPrefab hierarchy:");
        DebugDumpHelper.DumpGameObject(sb, spawner.screenPrefab.gameObject, 0);

        foreach (var wrapper in spawner.debugScreens.screens)
        {
            if (wrapper.screen == null)
                continue;

            sb.AppendLine();
            sb.AppendLine($"Screen prefab \"{wrapper.name}\" (\"{wrapper.text}\"):");
            DebugDumpHelper.DumpGameObject(sb, wrapper.screen.gameObject, 0);
        }

        var path = DebugDumpHelper.WriteDumpLog("PrefabDump.log", sb);
        Debug.Log($"[KSPTextureLoader] Prefab dump written to {path}");
    }

    internal void DumpLiveLayout()
    {
        var spawner = DebugScreenSpawner.Instance;
        if (spawner == null)
        {
            Debug.Log("[KSPTextureLoader] DebugScreenSpawner.Instance is null");
            return;
        }

        var sb = new StringBuilder();

        // Dump the screen prefab template
        if (spawner.screenPrefab != null)
        {
            sb.AppendLine("screenPrefab layout:");
            DebugDumpHelper.DumpGameObjectLayout(sb, spawner.screenPrefab.gameObject, 0);
        }

        // Dump every registered screen prefab
        foreach (var wrapper in spawner.debugScreens.screens)
        {
            if (wrapper.screen == null)
                continue;

            sb.AppendLine();
            sb.AppendLine($"Screen \"{wrapper.name}\" (\"{wrapper.text}\") layout:");
            DebugDumpHelper.DumpGameObjectLayout(sb, wrapper.screen.gameObject, 0);
        }

        // Dump the parent chain of this live screen instance
        sb.AppendLine();
        sb.AppendLine("Parent chain:");
        var t = transform.parent;
        int i = 0;
        while (t != null && i < 10)
        {
            var rt = t as RectTransform;
            sb.Append($"  [{i}] \"{t.name}\"");
            if (rt != null)
                sb.Append(
                    $" rect={rt.rect} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} offsetMin={rt.offsetMin} offsetMax={rt.offsetMax} pivot={rt.pivot}"
                );
            var le = t.GetComponent<LayoutElement>();
            if (le != null)
                sb.Append(
                    $" LE(minW={le.minWidth} minH={le.minHeight} prefW={le.preferredWidth} prefH={le.preferredHeight} flexW={le.flexibleWidth} flexH={le.flexibleHeight})"
                );
            var vlg = t.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
                sb.Append(
                    $" VLG(ctrlW={vlg.childControlWidth} ctrlH={vlg.childControlHeight} expandW={vlg.childForceExpandWidth} expandH={vlg.childForceExpandHeight} spacing={vlg.spacing} pad={vlg.padding})"
                );
            var hlg = t.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
                sb.Append(
                    $" HLG(ctrlW={hlg.childControlWidth} ctrlH={hlg.childControlHeight} expandW={hlg.childForceExpandWidth} expandH={hlg.childForceExpandHeight} spacing={hlg.spacing} pad={hlg.padding})"
                );
            var csf = t.GetComponent<ContentSizeFitter>();
            if (csf != null)
                sb.Append($" CSF(horiz={csf.horizontalFit} vert={csf.verticalFit})");
            var scrollRect = t.GetComponent<ScrollRect>();
            if (scrollRect != null)
                sb.Append($" ScrollRect(horiz={scrollRect.horizontal} vert={scrollRect.vertical})");
            sb.AppendLine();
            t = t.parent;
            i++;
        }

        var path = DebugDumpHelper.WriteDumpLog("LayoutDump.log", sb);
        Debug.Log($"[KSPTextureLoader] Layout dump written to {path}");
    }
}

internal class DumpPrefabButton : DebugScreenButton
{
    MainScreenContent screen;

    protected override void SetupValues()
    {
        screen = GetComponentInParent<MainScreenContent>();
    }

    protected override void OnClick()
    {
        screen.DumpScreenPrefab();
    }
}

internal class DumpLayoutButton : DebugScreenButton
{
    MainScreenContent screen;

    protected override void SetupValues()
    {
        screen = GetComponentInParent<MainScreenContent>();
    }

    protected override void OnClick()
    {
        screen.DumpLiveLayout();
    }
}

internal class DumpTexturesLayoutButton : DebugScreenButton
{
    protected override void OnClick()
    {
        // Find all instances (active and inactive) by searching from the root
        var instances = Resources.FindObjectsOfTypeAll<TexturesScreenContent>();
        if (instances.Length == 0)
        {
            Debug.Log("[KSPTextureLoader] No TexturesScreenContent instance found");
            return;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < instances.Length; i++)
        {
            var instance = instances[i];
            sb.AppendLine(
                $"TexturesScreenContent [{i}] (active={instance.gameObject.activeInHierarchy}):"
            );
            DebugDumpHelper.DumpGameObjectLayout(sb, instance.gameObject, 1);

            // Dump parent chain
            sb.AppendLine("  Parent chain:");
            var t = instance.transform.parent;
            int depth = 0;
            while (t != null && depth < 10)
            {
                var rt = t as RectTransform;
                sb.Append($"    [{depth}] \"{t.name}\"");
                if (rt != null)
                    sb.Append(
                        $" rect={rt.rect} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} offsetMin={rt.offsetMin} offsetMax={rt.offsetMax} pivot={rt.pivot}"
                    );
                var le = t.GetComponent<LayoutElement>();
                if (le != null)
                    sb.Append(
                        $" LE(minW={le.minWidth} minH={le.minHeight} prefW={le.preferredWidth} prefH={le.preferredHeight} flexW={le.flexibleWidth} flexH={le.flexibleHeight})"
                    );
                sb.AppendLine();
                t = t.parent;
                depth++;
            }

            sb.AppendLine();
        }

        var path = DebugDumpHelper.WriteDumpLog("TexturesLayoutDump.log", sb);
        Debug.Log($"[KSPTextureLoader] Textures layout dump written to {path}");
    }
}
