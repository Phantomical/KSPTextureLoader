using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using KSPTextureLoader.Utils;
using UnityEngine;

namespace KSPTextureLoader.Async;

internal static class Report
{
    internal static SynchronizationContext OuterContext = null;

    // csharpier-ignore
    internal static void DumpDeadlockReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[KSPTextureLoader]: Main thread has been blocked waiting for a task completion for more than 30s. KSPTextureLoader may be deadlocked!");
        sb.AppendLine();
        sb.AppendLine("Current State");
        sb.AppendLine("============================================================");
        sb.AppendFormat("Memory Watermark: {0}\n", Config.Instance.MaxTextureLoadMemory * 1024 * 1024);
        sb.AppendFormat("Allocated Memory: {0}\n", AllocatorUtil.AllocMem);
        if (OuterContext is UnitySynchronizationContext unityContext)
            sb.AppendFormat("Unity Context Queue Length: {0}\n", unityContext.m_AsyncWorkQueue.Count);

        sb.AppendLine();
        sb.AppendLine("Active Texture Loads");
        sb.AppendLine("============================================================");
        foreach (var name in TextureLoader.Instance.PendingTextures.Keys)
            sb.AppendLine(name);

        Debug.LogError(sb.ToString());
    }
}
