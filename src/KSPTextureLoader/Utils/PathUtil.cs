using System.IO;

namespace KSPTextureLoader.Utils;

internal static class PathUtil
{
    static string gameDataDir;
    public static string GameDataDir =>
        gameDataDir ??= Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
}
