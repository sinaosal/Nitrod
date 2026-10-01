using System.IO;
using System.Runtime.InteropServices;

namespace Nitrox.Model.Platforms.Discovery;

public static class GameInstallationHelper
{
    public static string? GetGameDirectory(string path, GameInfo gameInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string gameDirectory = Path.GetFullPath(path);
        if (File.Exists(gameDirectory))
        {
            if (!Path.GetFileName(gameDirectory).Equals(gameInfo.ExeName, System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            gameDirectory = Path.GetDirectoryName(gameDirectory)!;
        }

        return HasGameExecutable(gameDirectory, gameInfo) ? gameDirectory : null;
    }

    public static bool HasGameExecutable(string path, GameInfo gameInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return File.Exists(Path.Combine(path, "MacOS", gameInfo.ExeName));
        }
        return File.Exists(Path.Combine(path, gameInfo.ExeName));
    }

    public static bool HasValidGameFolder(string path, GameInfo gameInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (!Directory.Exists(path))
        {
            return false;
        }
        if (!HasGameExecutable(path, gameInfo))
        {
            return false;
        }
        if (!Directory.Exists(Path.Combine(path, gameInfo.DataFolder, "Managed")))
        {
            return false;
        }

        return true;
    }
}
