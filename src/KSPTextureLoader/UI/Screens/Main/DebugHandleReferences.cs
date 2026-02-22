using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace KSPTextureLoader.UI.Screens.Main;

/// <summary>
/// Walks all GameObjects and their component fields to find which components hold references
/// to each live TextureHandle or CPUTextureHandle, and records the field path to each reference.
/// </summary>
internal class DebugHandleReferences
{
    const int MaxDepth = 64;
    const int MaxCollectionItems = 1024;

    internal static void Dump()
    {
        var loader = TextureLoader.Instance;
        if (loader == null)
        {
            Debug.Log("[KSPTextureLoader] TextureLoader.Instance is null");
            return;
        }

        Debug.Log("[KSPTextureLoader] Starting handle references dump");

        // Results: impl -> list of "field.path" strings
        var texHandleRefs = new Dictionary<TextureHandleImpl, List<string>>(
            ReferenceEqualityComparer.Instance
        );
        var cpuHandleRefs = new Dictionary<CPUTextureHandle, List<string>>(
            ReferenceEqualityComparer.Instance
        );

        // Pre-populate from loader so handles with no component references still appear
        foreach (var (_, weak) in TextureLoader.textures)
        {
            if (weak.TryGetTarget(out var impl) && !texHandleRefs.ContainsKey(impl))
                texHandleRefs[impl] = [];
        }
        foreach (var (_, weak) in TextureLoader.cpuTextures)
        {
            if (weak.TryGetTarget(out var cpu) && !cpuHandleRefs.ContainsKey(cpu))
                cpuHandleRefs[cpu] = [];
        }

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new List<string>();

        // Walk every component on every GameObject in the scene
        foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            var goPath = BuildGoPath(go);
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                seen.Clear();
                seen.Add(component);

                stack.Clear();
                stack.Add($"[{goPath}]/{component.GetType().Name}");

                WalkObject(component, stack, seen, texHandleRefs, cpuHandleRefs, 0);
            }
        }

        // Write output
        var sb = new StringBuilder();
        sb.AppendLine($"Handle references dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(
            $"TextureHandles: {texHandleRefs.Count}  CPUTextureHandles: {cpuHandleRefs.Count}"
        );
        sb.AppendLine();

        sb.AppendLine("=== TextureHandle ===");
        sb.AppendLine();
        foreach (var (impl, refs) in texHandleRefs.OrderBy(name => name.Key.Path))
        {
            sb.AppendLine(
                $"{impl.Path}  (RefCount={impl.RefCount}, IsComplete={impl.IsComplete}, IsError={impl.IsError})"
            );
            refs.Sort();

            if (refs.Count == 0)
                sb.AppendLine("  (no component references found)");
            else
                foreach (var refPath in refs)
                    sb.AppendLine($"  {refPath}");
            sb.AppendLine();
        }

        sb.AppendLine("=== CPUTextureHandle ===");
        sb.AppendLine();
        foreach (var (handle, refs) in cpuHandleRefs)
        {
            sb.AppendLine(
                $"{handle.Path}  (RefCount={handle.RefCount}, IsComplete={handle.IsComplete}, IsError={handle.IsError})"
            );
            if (refs.Count == 0)
                sb.AppendLine("  (no component references found)");
            else
                foreach (var refPath in refs)
                    sb.AppendLine($"  {refPath}");
            sb.AppendLine();
        }

        var path = DebugDumpHelper.WriteDumpLog("HandleReferencesDump.log", sb);
        Debug.Log($"[KSPTextureLoader] Handle references dump written to {path}");
    }

    static string BuildGoPath(GameObject go)
    {
        var parts = new List<string>();
        var t = go.transform;
        while (t != null)
        {
            parts.Add(t.name);
            t = t.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Walk all declared instance fields of <paramref name="obj"/> and its base types.</summary>
    static void WalkObject(
        object obj,
        List<string> path,
        HashSet<object> seen,
        Dictionary<TextureHandleImpl, List<string>> texHandleRefs,
        Dictionary<CPUTextureHandle, List<string>> cpuHandleRefs,
        int depth
    )
    {
        if (depth > MaxDepth)
            return;
        if (obj is null)
            return;

        // Track field names already walked so that base-class fields hidden by a
        // derived-class field of the same name are not visited a second time.
        var seenFieldNames = new HashSet<string>();

        var t = obj.GetType();
        while (
            t != null
            && t != typeof(object)
            && t != typeof(MonoBehaviour)
            && t != typeof(Behaviour)
            && t != typeof(Component)
            && t != typeof(UnityEngine.Object)
        )
        {
            foreach (
                var field in t.GetFields(
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                if (!seenFieldNames.Add(field.Name))
                    continue;

                object val;
                try
                {
                    val = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                path.Add(FormatFieldName(field.Name));
                WalkValue(val, path, seen, texHandleRefs, cpuHandleRefs, depth + 1);
                path.RemoveAt(path.Count - 1);
            }

            t = t.BaseType;
        }
    }

    /// <summary>Inspect a single value, recording handle references or recursing into it.</summary>
    static void WalkValue(
        object val,
        List<string> path,
        HashSet<object> seen,
        Dictionary<TextureHandleImpl, List<string>> texHandleRefs,
        Dictionary<CPUTextureHandle, List<string>> cpuHandleRefs,
        int depth
    )
    {
        if (val == null || depth > MaxDepth)
            return;

        // Record TextureHandle references
        if (val is TextureHandle th)
        {
            if (!texHandleRefs.TryGetValue(th.handle, out var handlerefs))
            {
                handlerefs = [];
                texHandleRefs.Add(th.handle, handlerefs);
            }
            handlerefs.Add(string.Join(".", path));
            return;
        }

        // Record CPUTextureHandle references
        if (val is CPUTextureHandle ch)
        {
            if (!cpuHandleRefs.TryGetValue(ch, out var handlerefs))
            {
                handlerefs = [];
                cpuHandleRefs.Add(ch, handlerefs);
            }
            handlerefs.Add(string.Join(".", path));
            return;
        }

        var valType = val.GetType();
        if (ShouldSkipType(valType))
            return;

        // Use seen set to prevent cycles; structs are value types so don't need tracking
        if (valType.IsClass)
        {
            if (!seen.Add(val))
                return;
        }

        // For collections iterate elements rather than walking internal fields
        if (val is IEnumerable enumerable)
        {
            int i = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    path.Add($"[{i++}]");
                    WalkValue(item, path, seen, texHandleRefs, cpuHandleRefs, depth + 1);
                    path.RemoveAt(path.Count - 1);
                    if (i >= MaxCollectionItems)
                        break;
                }
            }
            catch { }
            return;
        }

        WalkObject(val, path, seen, texHandleRefs, cpuHandleRefs, depth);
    }

    static string FormatFieldName(string name)
    {
        const string suffix = ">k__BackingField";
        if (name.StartsWith("<") && name.EndsWith(suffix))
            return name.Substring(1, name.Length - 1 - suffix.Length);
        return name;
    }

    static bool ShouldSkipType(Type type)
    {
        // Definite leaf types that cannot contain a TextureHandle
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum || type.IsPointer)
            return true;

        // Skip arrays in bulk instead of going element-by-element
        if (type.IsArray && ShouldSkipType(type.GetElementType()))
            return true;

        // Unmanaged types contain no references and as such we don't need to walk them.
        if (UnsafeUtility.IsUnmanaged(type))
            return true;

        // Delegates capture context but are not useful to walk
        if (typeof(Delegate).IsAssignableFrom(type))
            return true;

        // Unity Objects are scene roots; they will be visited separately as GameObjects
        if (typeof(GameObject).IsAssignableFrom(type))
            return true;

        // These get walked from their respective GameObjects, no need to walk references
        // to them.
        if (typeof(Component).IsAssignableFrom(type))
            return true;

        return false;
    }

    /// <summary>
    /// Identity-based equality comparer using RuntimeHelpers.GetHashCode,
    /// safe for use with objects that may override Equals or GetHashCode.
    /// </summary>
    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

internal class DumpHandleReferencesButton : DebugScreenButton
{
    protected override void OnClick()
    {
        DebugHandleReferences.Dump();
    }
}
