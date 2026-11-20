using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

public sealed class ModScanOptions
{
    /// <summary>
    ///     File name patterns (<c>*</c> and <c>?</c> wildcards, case-insensitive) that are never part of a manifest: leftover
    ///     install archives, logs, and per-user settings that mods write next to their DLL (Nautilus' <c>config.json</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludedFilePatterns = ["*.zip", "*.7z", "*.rar", "*.log", "*.tmp", "*.bak", "*.nitrodtmp", "config.json", "Thumbs.db", ".DS_Store", "desktop.ini"];

    public IReadOnlyCollection<string> ExcludedFilePatterns { get; set; } = DefaultExcludedFilePatterns;

    /// <summary>
    ///     Skip top-level entries whose name starts with a dot. The Subnautica BepInEx pack installs its own helper plugins in
    ///     <c>plugins/.tobey.bepinex.pack</c>; those belong to the loader, not to a mod.
    /// </summary>
    public bool ExcludeHiddenEntries { get; set; } = true;

    /// <summary>Packages players may skip. Matched against the package id, its folder/file name, or any plugin GUID.</summary>
    public IReadOnlyCollection<string> OptionalPackages { get; set; } = [];

    /// <summary>Packages left out of the manifest entirely (e.g. host-only tools). Same matching as <see cref="OptionalPackages" />.</summary>
    public IReadOnlyCollection<string> ExcludedPackages { get; set; } = [];

    /// <summary>Upper bound on files per manifest to keep packets and scans bounded.</summary>
    public int MaxFiles { get; set; } = 20_000;

    /// <summary>Splits a comma/semicolon/newline separated setting into entries.</summary>
    public static IReadOnlyCollection<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value!.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
}

/// <summary>
///     Builds a <see cref="ModManifest" /> from a game installation's BepInEx folder.
/// </summary>
public sealed class ModScanner
{
    private readonly IPluginMetadataReader metadataReader;
    private readonly ModFileHasher hasher;

    public ModScanner(IPluginMetadataReader metadataReader, ModFileHasher? hasher = null)
    {
        this.metadataReader = metadataReader;
        this.hasher = hasher ?? new ModFileHasher();
    }

    public static string GetBepInExRoot(string gameRoot) => Path.Combine(gameRoot, ModPathSafety.BEPINEX_FOLDER_NAME);

    public static string GetSyncRoot(string gameRoot) => Path.Combine(GetBepInExRoot(gameRoot), ModPathSafety.SYNC_FOLDER_NAME);

    public ModManifest Scan(string gameRoot, ModScanOptions? options = null)
    {
        options ??= new ModScanOptions();
        string bepInExRoot = GetBepInExRoot(gameRoot);
        string gameVersion = ReadGameVersion(gameRoot);
        string loaderVersion = ReadLoaderVersion(gameRoot);
        if (!Directory.Exists(bepInExRoot))
        {
            return ModManifest.Empty(gameVersion, loaderVersion);
        }

        Regex[] excludedFiles = options.ExcludedFilePatterns.Select(WildcardToRegex).ToArray();
        List<ModPackage> packages = [];
        List<string> providedPluginGuids = [];
        int fileCount = 0;
        foreach (string root in ModPathSafety.AllowedRoots)
        {
            string rootPath = Path.Combine(bepInExRoot, root);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(rootPath).OrderBy(e => e, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                string entryName = Path.GetFileName(entry);
                if (ModPathSafety.IsReparsePoint(entry))
                {
                    continue;
                }
                if (options.ExcludeHiddenEntries && entryName.StartsWith("."))
                {
                    providedPluginGuids.AddRange(ReadPluginGuids(bepInExRoot, entry));
                    continue;
                }
                bool isDirectory = Directory.Exists(entry);
                if (!isDirectory && IsExcludedFile(entryName, excludedFiles))
                {
                    continue;
                }

                string packageId = $"{root}/{entryName}";
                List<ModFile> files = [];
                List<ModPluginInfo> plugins = [];
                IEnumerable<string> packageFiles = isDirectory ? EnumerateFilesSafely(entry) : [entry];
                foreach (string file in packageFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsExcludedFile(Path.GetFileName(file), excludedFiles))
                    {
                        continue;
                    }
                    string relativePath = ModPathSafety.ToRelativePath(bepInExRoot, file);
                    if (!ModPathSafety.IsSafeRelativePath(relativePath, out string reason))
                    {
                        Log.Warn($"Skipping mod file '{relativePath}' because it can't be synchronized safely: {reason}");
                        continue;
                    }
                    if (++fileCount > options.MaxFiles)
                    {
                        Log.Warn($"Mod scan stopped after {options.MaxFiles} files.");
                        break;
                    }

                    try
                    {
                        long size = new FileInfo(file).Length;
                        ModFileKind kind = ModPathSafety.ClassifyFile(file);
                        files.Add(new ModFile(relativePath, size, hasher.GetSha256(file), kind));
                        if (kind == ModFileKind.MANAGED_ASSEMBLY)
                        {
                            plugins.AddRange(metadataReader.ReadPlugins(file, relativePath));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Unable to read mod file '{relativePath}': {ex.Message}");
                    }
                }

                if (files.Count == 0)
                {
                    continue;
                }
                string displayName = plugins.Count > 0 ? plugins[0].Name : isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
                ModPackage package = new(packageId, displayName, ModRequirement.REQUIRED, plugins, files);
                if (Matches(package, options.ExcludedPackages))
                {
                    providedPluginGuids.AddRange(plugins.Select(p => p.Guid));
                    continue;
                }
                if (Matches(package, options.OptionalPackages))
                {
                    package = package.WithRequirement(ModRequirement.OPTIONAL);
                }
                packages.Add(package);
            }
        }

        hasher.SaveCache();
        return new ModManifest(ModManifest.CURRENT_FORMAT_VERSION, gameVersion, loaderVersion, packages, providedPluginGuids.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private IEnumerable<string> ReadPluginGuids(string bepInExRoot, string entry)
    {
        IEnumerable<string> assemblies = Directory.Exists(entry) ? EnumerateFilesSafely(entry) : [entry];
        foreach (string assembly in assemblies.Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (ModPluginInfo plugin in metadataReader.ReadPlugins(assembly, ModPathSafety.ToRelativePath(bepInExRoot, assembly)))
            {
                yield return plugin.Guid;
            }
        }
    }

    /// <summary>True if any entry of <paramref name="filters" /> names this package by id, folder/file name or plugin GUID.</summary>
    public static bool Matches(ModPackage package, IReadOnlyCollection<string> filters)
    {
        if (filters.Count == 0)
        {
            return false;
        }
        string name = package.Id.Substring(package.Id.IndexOf('/') + 1);
        foreach (string filter in filters)
        {
            if (filter.Equals(package.Id, StringComparison.OrdinalIgnoreCase) || filter.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                package.Plugins.Any(p => p.Guid.Equals(filter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    public static string ReadGameVersion(string gameRoot)
    {
        try
        {
            string versionFile = Path.Combine(gameRoot, GameInfo.Subnautica.DataFolder, "StreamingAssets", "SNUnmanagedData", "plastic_status.ignore");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public static string ReadLoaderVersion(string gameRoot)
    {
        string bepInExDll = Path.Combine(GetBepInExRoot(gameRoot), "core", "BepInEx.dll");
        if (!File.Exists(bepInExDll))
        {
            return "";
        }
        try
        {
            // Reads the assembly manifest only; does not load the assembly.
            return AssemblyName.GetAssemblyName(bepInExDll).Version?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        Stack<string> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Unable to list mod folder '{current}': {ex.Message}");
                continue;
            }
            foreach (string file in files)
            {
                if (!ModPathSafety.IsReparsePoint(file))
                {
                    yield return file;
                }
            }
            foreach (string subDirectory in directories)
            {
                // Never follow links: they could point anywhere on disk (and loop forever).
                if (!ModPathSafety.IsReparsePoint(subDirectory))
                {
                    pending.Push(subDirectory);
                }
            }
        }
    }

    private static bool IsExcludedFile(string fileName, Regex[] patterns) => patterns.Any(p => p.IsMatch(fileName));

    private static Regex WildcardToRegex(string pattern) =>
        new($"^{Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".")}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
