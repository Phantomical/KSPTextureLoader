using System.Security.Cryptography;
using System.Text;

namespace KSPTextureUtils.Bundle;

internal static class CabName
{
    /// <summary>
    /// Produce a stable, unique <c>CAB-&lt;32 hex&gt;</c> archive name from a set of
    /// seed strings (e.g. the bundle name and its texture names). Deterministic so
    /// rebuilding the same inputs yields the same CAB, but distinct across bundles
    /// so two of them can be mounted simultaneously without an archive-path clash.
    /// </summary>
    public static string ForBundle(string bundleName, IEnumerable<string> textureNames)
    {
        var sb = new StringBuilder(bundleName);
        sb.Append('\n');
        foreach (var n in textureNames)
            sb.Append(n).Append('\n');

        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder("CAB-", 36);
        foreach (byte b in hash)
            hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
