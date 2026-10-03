using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Nitrox.Launcher.Models.Utils;

internal static class BepInExModInstaller
{
    private const int MaximumEntryCount = 4096;
    private const long MaximumUncompressedSize = 512L * 1024 * 1024;

    public static bool IsBepInExInstalled(string gamePath) =>
        File.Exists(Path.Combine(gamePath, "BepInEx", "core", "BepInEx.dll"));

    public static int Install(string archivePath, string gamePath)
    {
        string gameRoot = Path.GetFullPath(gamePath);
        string pluginRoot = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx"));
        string pluginRootPrefix = pluginRoot + Path.DirectorySeparatorChar;
        string archiveName = Path.GetFileNameWithoutExtension(archivePath);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry[] fileEntries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();

        if (fileEntries.Length == 0 || fileEntries.Length > MaximumEntryCount)
        {
            throw new InvalidDataException("The ZIP is empty or contains too many files.");
        }

        List<(ZipArchiveEntry Entry, string Destination)> files = [];
        HashSet<string> destinationPaths = new(StringComparer.OrdinalIgnoreCase);
        long uncompressedSize = 0;
        bool containsPluginAssembly = false;

        foreach (ZipArchiveEntry entry in fileEntries)
        {
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException("The ZIP contains a symbolic link, which cannot be installed safely.");
            }

            uncompressedSize = checked(uncompressedSize + entry.Length);
            if (uncompressedSize > MaximumUncompressedSize)
            {
                throw new InvalidDataException("The ZIP expands beyond the 512 MB installation limit.");
            }

            string[] segments = GetSafeSegments(entry.FullName);
            string relativePath = GetPluginRelativePath(segments, archiveName);
            string destination = Path.GetFullPath(Path.Combine(pluginRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(pluginRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The ZIP contains a path outside the BepInEx plugin folders.");
            }

            if (!destinationPaths.Add(destination))
            {
                throw new InvalidDataException("The ZIP contains duplicate file paths.");
            }

            containsPluginAssembly |= Path.GetExtension(destination).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            files.Add((entry, destination));
        }

        if (!containsPluginAssembly)
        {
            throw new InvalidDataException("The ZIP does not contain a BepInEx plugin or patcher DLL.");
        }

        foreach ((ZipArchiveEntry entry, string destination) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = entry.Open();
            using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }

        return files.Count;
    }

    private static string[] GetSafeSegments(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (normalized.StartsWith('/') || segments.Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException("The ZIP contains an invalid or unsafe file path.");
        }
        return segments;
    }

    private static string GetPluginRelativePath(string[] segments, string archiveName)
    {
        int bepinexIndex = Array.FindIndex(segments, segment => segment.Equals("BepInEx", StringComparison.OrdinalIgnoreCase));
        if (bepinexIndex >= 0)
        {
            if (bepinexIndex + 2 >= segments.Length || !TryNormalizeCategory(segments[bepinexIndex + 1], out string category))
            {
                throw new InvalidDataException("Only files under BepInEx/plugins or BepInEx/patchers can be installed.");
            }

            return Path.Combine(category, Path.Combine(segments.Skip(bepinexIndex + 2).ToArray()));
        }

        if (TryNormalizeCategory(segments[0], out string rootCategory))
        {
            if (segments.Length < 2)
            {
                throw new InvalidDataException("The ZIP contains an incomplete BepInEx plugin path.");
            }
            return Path.Combine(rootCategory, Path.Combine(segments.Skip(1).ToArray()));
        }

        string safeArchiveName = string.Concat(archiveName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safeArchiveName))
        {
            safeArchiveName = "ImportedMod";
        }
        return segments.Length == 1
            ? Path.Combine("plugins", safeArchiveName, segments[0])
            : Path.Combine("plugins", Path.Combine(segments));
    }

    private static bool TryNormalizeCategory(string value, out string category)
    {
        if (value.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            category = "plugins";
            return true;
        }
        if (value.Equals("patchers", StringComparison.OrdinalIgnoreCase))
        {
            category = "patchers";
            return true;
        }
        category = "";
        return false;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}