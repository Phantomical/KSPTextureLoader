using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KSPTextureLoader;

internal struct ImplicitBundle : IConfigNode
{
    public string prefix;
    public string bundle;

    public void Load(ConfigNode node)
    {
        node.TryGetValue(nameof(prefix), ref prefix);
        node.TryGetValue(nameof(bundle), ref bundle);
    }

    public void Save(ConfigNode node)
    {
        node.AddValue(nameof(prefix), prefix);
        node.AddValue(nameof(bundle), bundle);
    }
}

/// <summary>
/// The type used for the config file in GameData.
/// </summary>
internal class Config : IConfigNode
{
    public static readonly Config Instance = new();

    /// <summary>
    /// How many frames should we hold on to asset bundles for before they are
    /// unloaded.
    /// </summary>
    ///
    /// <remarks>
    /// You want this to be set so that it is longer than the time it takes to
    /// load all assets from asset bundles. When an asset bundle is unloaded it
    /// needs to sync with the loading thread which can take a while.
    /// </remarks>
    public int BundleUnloadDelay = 30;

    /// <summary>
    /// Controls the size of the buffer unity will use to buffer uploads
    /// happening in the background.
    /// </summary>
    public int AsyncUploadBufferSize = 128;

    /// <summary>
    /// Controls whether unity holds on to the persistent buffer when there are
    /// no pending asset bundle loads.
    /// </summary>
    public bool AsyncUploadPersistentBuffer = true;

    /// <summary>
    /// Whether to allow direct use of native rendering extensions to upload
    /// textures.
    /// </summary>
    public bool AllowNativeUploads = true;

    /// <summary>
    /// Whether to use Unity's AsyncReadManager to dispatch reads. If false
    /// then reads are done in a job.
    /// </summary>
    public bool UseAsyncReadManager = true;

    /// <summary>
    /// Implicit bundle declarations.
    /// </summary>
    ///
    /// <remarks>
    /// Changing this list doesn't change the actual data structure used to
    /// look these up. Use module manager to apply a patch, or save/load the
    /// whole config in order to apply an update.
    /// </remarks>
    public List<ImplicitBundle> AssetBundles = [];

    internal void Apply()
    {
        if (QualitySettings.asyncUploadBufferSize != AsyncUploadBufferSize)
            QualitySettings.asyncUploadBufferSize = Clamp(AsyncUploadBufferSize, 2, 2047);
        QualitySettings.asyncUploadPersistentBuffer = AsyncUploadPersistentBuffer;
    }

    public void Load(ConfigNode node)
    {
        node.TryGetValue(nameof(BundleUnloadDelay), ref BundleUnloadDelay);
        node.TryGetValue(nameof(AsyncUploadBufferSize), ref AsyncUploadBufferSize);
        node.TryGetValue(nameof(AsyncUploadPersistentBuffer), ref AsyncUploadPersistentBuffer);
        node.TryGetValue(nameof(AllowNativeUploads), ref AllowNativeUploads);

        var children = node.GetNodes("AssetBundle");
        var bundles = new List<ImplicitBundle>(children.Length);
        foreach (var child in children)
        {
            ImplicitBundle bundle = default;
            bundle.Load(child);

            if (string.IsNullOrEmpty(bundle.prefix))
            {
                Debug.LogError(
                    $"[KSPTextureLoader] Found ImplicitBundle node with an empty prefix"
                );
                continue;
            }

            if (string.IsNullOrEmpty(bundle.bundle))
            {
                Debug.LogError(
                    $"[KSPTextureLoader] ImplicitBundle with prefix {bundle.prefix} is missing a bundle path"
                );
                continue;
            }

            var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", bundle.bundle);
            if (!File.Exists(diskPath))
            {
                Debug.LogError($"[KSPTextureLoader] ImplicitBundle {bundle.bundle} does not exist");
                continue;
            }

            Debug.Log(
                $"[KSPTextureLoader] Loaded implicit bundle for prefix {bundle.prefix}: {bundle.bundle}"
            );

            bundles.Add(bundle);
        }

        AssetBundles = bundles;

        BuildBundlePrefixMap();
    }

    public void Save(ConfigNode node)
    {
        node.AddValue(nameof(BundleUnloadDelay), BundleUnloadDelay);
        node.AddValue(nameof(AsyncUploadBufferSize), AsyncUploadBufferSize);
        node.AddValue(nameof(AsyncUploadPersistentBuffer), AsyncUploadPersistentBuffer);
        node.AddValue(nameof(AllowNativeUploads), AllowNativeUploads);

        foreach (var bundle in AssetBundles)
            bundle.Save(node.AddNode("AssetBundle"));
    }

    struct PrefixEntry
    {
        public int start;
        public int end;
    }

    private string[] BundlePaths;
    private Dictionary<string, PrefixEntry> BundlePrefixMap = [];

    public static void ModuleManagerPostLoad()
    {
        var configs = GameDatabase.Instance.GetConfigNodes("KSPTextureLoader");
        if (configs?.Length >= 1)
            Instance.Load(configs[0]);

        Instance.Apply();
    }

    void BuildBundlePrefixMap()
    {
        var sorted = AssetBundles
            .Select(bundle =>
            {
                bundle.prefix = TextureLoader
                    .CanonicalizeResourcePath(bundle.prefix)
                    .TrimEnd('\\', '/');
                return bundle;
            })
            .OrderBy(bundle => bundle.prefix, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        var paths = new string[sorted.Count];
        var prefixMap = new Dictionary<string, PrefixEntry>(
            StringComparer.InvariantCultureIgnoreCase
        );

        string current = null;
        int start = -1;
        for (int i = 0; i < sorted.Count; ++i)
        {
            var ibundle = sorted[i];
            paths[i] = ibundle.bundle;

            if (string.Equals(current, ibundle.prefix, StringComparison.InvariantCultureIgnoreCase))
                continue;

            if (current is not null)
                prefixMap.Add(current, new PrefixEntry { start = start, end = i });

            current = ibundle.prefix;
            start = i;
        }

        if (current is not null)
            prefixMap.Add(current, new PrefixEntry { start = start, end = sorted.Count });

        BundlePaths = paths;
        BundlePrefixMap = prefixMap;
    }

    internal void GetImplicitBundlesForCanonicalPath(string key, List<string> bundles)
    {
        if (BundlePrefixMap is null || BundlePrefixMap.Count == 0)
            return;
        if (BundlePaths is null || BundlePaths.Length == 0)
            return;

        while (!string.IsNullOrEmpty(key))
        {
            var index = key.LastIndexOf('/');
            if (index == -1)
                break;
            key = key.Substring(0, index);
            if (BundlePrefixMap.TryGetValue(key, out var entry))
            {
                for (int i = entry.start; i < entry.end; ++i)
                    bundles.Add(BundlePaths[i]);
            }
        }
    }

    static int Clamp(int v, int lo, int hi)
    {
        if (v < lo)
            v = lo;
        if (v > hi)
            v = hi;
        return v;
    }
}
