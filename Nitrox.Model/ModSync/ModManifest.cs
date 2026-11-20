using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using BinaryPack.Attributes;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Describes the BepInEx mods installed in a game directory: which plugins exist (GUID, version, dependencies) and the exact
///     files (path, size, SHA-256) that make them up. A host sends its manifest to joining players so they can detect missing or
///     different mods and download exactly the files that differ.
/// </summary>
/// <remarks>
///     All types in this file are serialized with BinaryPack (inside packets) and System.Text.Json (on disk), so they use
///     get-only properties initialized through a constructor whose parameter names match the property names.
/// </remarks>
[Serializable]
public sealed class ModManifest
{
    public const int CURRENT_FORMAT_VERSION = 1;

    /// <summary>Format of this manifest. Bumped on incompatible changes so older clients can ignore newer manifests.</summary>
    public int FormatVersion { get; }

    /// <summary>Game build reported by the game files (e.g. the plastic_status build number), or empty if unknown.</summary>
    public string GameVersion { get; }

    /// <summary>Version of BepInEx found in BepInEx/core, or empty if BepInEx is not installed.</summary>
    public string LoaderVersion { get; }

    public List<ModPackage> Packages { get; }

    /// <summary>
    ///     GUIDs of plugins that are installed but not part of <see cref="Packages" /> (hidden loader helpers, excluded packages).
    ///     They still satisfy dependencies.
    /// </summary>
    public List<string> ProvidedPluginGuids { get; }

    public ModManifest(int formatVersion, string gameVersion, string loaderVersion, List<ModPackage> packages, List<string> providedPluginGuids)
    {
        FormatVersion = formatVersion;
        GameVersion = gameVersion ?? "";
        LoaderVersion = loaderVersion ?? "";
        Packages = packages ?? [];
        ProvidedPluginGuids = providedPluginGuids ?? [];
    }

    public static ModManifest Empty(string gameVersion = "", string loaderVersion = "") => new(CURRENT_FORMAT_VERSION, gameVersion, loaderVersion, [], []);

    [IgnoredMember]
    [JsonIgnore]
    public bool IsLoaderInstalled => LoaderVersion.Length > 0;

    [IgnoredMember]
    [JsonIgnore]
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (ModPackage package in Packages)
            {
                total += package.TotalSize;
            }
            return total;
        }
    }

    public override string ToString() => $"[{nameof(ModManifest)} Game: {GameVersion}, BepInEx: {LoaderVersion}, Packages: {Packages.Count}]";
}

/// <summary>
///     A unit of installation: one top-level folder (or loose file) under BepInEx/plugins or BepInEx/patchers, as produced by
///     extracting a typical mod archive. A package can contain zero or more BepInEx plugins.
/// </summary>
[Serializable]
public sealed class ModPackage
{
    /// <summary>
    ///     Location relative to the BepInEx folder using forward slashes, e.g. <c>plugins/Nautilus</c> or <c>plugins/MyMod.dll</c>.
    ///     Synchronized files are installed at the same location as on the host.
    /// </summary>
    public string Id { get; }

    public string DisplayName { get; }

    public ModRequirement Requirement { get; }

    public List<ModPluginInfo> Plugins { get; }

    public List<ModFile> Files { get; }

    public ModPackage(string id, string displayName, ModRequirement requirement, List<ModPluginInfo> plugins, List<ModFile> files)
    {
        Id = id ?? "";
        DisplayName = displayName ?? "";
        Requirement = requirement;
        Plugins = plugins ?? [];
        Files = files ?? [];
    }

    [IgnoredMember]
    [JsonIgnore]
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (ModFile file in Files)
            {
                total += file.Size;
            }
            return total;
        }
    }

    /// <summary>The version of the first plugin, which is how most mods present themselves. Empty if the package has no plugin.</summary>
    [IgnoredMember]
    [JsonIgnore]
    public string PrimaryVersion => Plugins.Count > 0 ? Plugins[0].Version : "";

    public ModPackage WithRequirement(ModRequirement requirement) => new(Id, DisplayName, requirement, Plugins, Files);

    public override string ToString() => $"[{nameof(ModPackage)} {Id} ({Requirement}), Plugins: {Plugins.Count}, Files: {Files.Count}]";
}

/// <summary>Metadata of a BepInEx plugin, read from its [BepInPlugin], [BepInDependency] and [BepInIncompatibility] attributes.</summary>
[Serializable]
public sealed class ModPluginInfo
{
    public string Guid { get; }
    public string Name { get; }
    public string Version { get; }

    /// <summary>Path of the assembly declaring the plugin, relative to the BepInEx folder (forward slashes).</summary>
    public string AssemblyPath { get; }

    public List<ModDependencyInfo> Dependencies { get; }
    public List<string> Incompatibilities { get; }

    public ModPluginInfo(string guid, string name, string version, string assemblyPath, List<ModDependencyInfo> dependencies, List<string> incompatibilities)
    {
        Guid = guid ?? "";
        Name = name ?? "";
        Version = version ?? "";
        AssemblyPath = assemblyPath ?? "";
        Dependencies = dependencies ?? [];
        Incompatibilities = incompatibilities ?? [];
    }

    public override string ToString() => $"{Name} ({Guid}) {Version}";
}

[Serializable]
public sealed class ModDependencyInfo
{
    public string Guid { get; }

    /// <summary>Minimum version required, or empty if any version is accepted.</summary>
    public string MinimumVersion { get; }

    /// <summary>Hard dependencies prevent the plugin from loading when missing. Soft dependencies only change load order.</summary>
    public bool IsHard { get; }

    public ModDependencyInfo(string guid, string minimumVersion, bool isHard)
    {
        Guid = guid ?? "";
        MinimumVersion = minimumVersion ?? "";
        IsHard = isHard;
    }

    public override string ToString() => $"{Guid}{(MinimumVersion.Length > 0 ? $" >= {MinimumVersion}" : "")}{(IsHard ? "" : " (soft)")}";
}

[Serializable]
public sealed class ModFile
{
    /// <summary>Path relative to the BepInEx folder using forward slashes, e.g. <c>plugins/Nautilus/Nautilus.dll</c>.</summary>
    public string RelativePath { get; }

    public long Size { get; }

    /// <summary>Lower-case hex SHA-256 of the file content.</summary>
    public string Sha256 { get; }

    public ModFileKind Kind { get; }

    public ModFile(string relativePath, long size, string sha256, ModFileKind kind)
    {
        RelativePath = relativePath ?? "";
        Size = size;
        Sha256 = sha256 ?? "";
        Kind = kind;
    }

    public override string ToString() => $"{RelativePath} ({Size} bytes, {Kind})";
}

public enum ModFileKind
{
    /// <summary>Assets, configs, audio, text, etc. Never executed by the game or Nitrod.</summary>
    DATA,

    /// <summary>.NET assembly. This is what BepInEx plugins are: code that runs inside the game.</summary>
    MANAGED_ASSEMBLY,

    /// <summary>Native library (C/C++ DLL, .so, .dylib). Runs inside the game and can't be inspected like a .NET assembly.</summary>
    NATIVE_LIBRARY,

    /// <summary>Standalone programs and scripts (.exe, .bat, .ps1, ...). Never needed by BepInEx plugins.</summary>
    EXECUTABLE
}

public enum ModRequirement
{
    /// <summary>Players must have this package (same files) to join when the host enforces matching mods.</summary>
    REQUIRED,

    /// <summary>Offered to players, but they can join without it (e.g. client-side UI mods).</summary>
    OPTIONAL
}
