using System;
using System.Diagnostics;
using System.IO;
using Nitrox.Model.Platforms.OS.Shared;

namespace Nitrox.Launcher.Models.Utils;

internal static class UpdatePackage
{
    public static Process? StartInstaller(string scriptPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : scriptPath,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("call");
            startInfo.ArgumentList.Add(scriptPath);
        }

        return ProcessEx.StartProcessDetached(startInfo);
    }

    public static string FindLauncherDirectory(string extractPath)
    {
        if (TryGetLauncherFileName(extractPath) != null)
        {
            return extractPath;
        }

        string[] directories = Directory.GetDirectories(extractPath);
        if (directories.Length == 1 && TryGetLauncherFileName(directories[0]) != null)
        {
            return directories[0];
        }

        throw new InvalidDataException("The update ZIP does not contain a valid launcher installation.");
    }

    public static string GetLauncherFileName(string directory) => TryGetLauncherFileName(directory)
        ?? throw new InvalidDataException("The update package is missing its launcher executable or runtime configuration.");

    private static string? TryGetLauncherFileName(string directory)
    {
        foreach (string assemblyName in new[] { "Nitrod.Launcher", "Nitrox.Launcher" })
        {
            string fileName = assemblyName + (OperatingSystem.IsWindows() ? ".exe" : "");
            if (File.Exists(Path.Combine(directory, fileName)) &&
                File.Exists(Path.Combine(directory, $"{assemblyName}.dll")) &&
                File.Exists(Path.Combine(directory, $"{assemblyName}.runtimeconfig.json")))
            {
                return fileName;
            }
        }

        return null;
    }
}