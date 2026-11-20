using System;
using System.Collections.Generic;
using System.Linq;

namespace Nitrox.Model.ModSync;

public enum ModIssueSeverity
{
    INFO,
    WARNING,
    ERROR
}

public enum ModIssueKind
{
    MISSING_MOD,
    DIFFERENT_VERSION,
    DIFFERENT_FILES,
    EXTRA_MOD,
    MISSING_DEPENDENCY,
    INCOMPATIBLE_MODS,
    KNOWN_INCOMPATIBILITY,
    DUPLICATE_PLUGIN,
    GAME_VERSION_MISMATCH,
    LOADER_MISSING,
    LOADER_VERSION_MISMATCH,
    NATIVE_CODE,
    EXECUTABLE_FILE
}

/// <summary>A single finding of a compatibility check, written for players rather than developers.</summary>
public sealed class ModIssue
{
    public ModIssueSeverity Severity { get; }
    public ModIssueKind Kind { get; }

    /// <summary>Package the issue is about, or empty for global issues (game version, BepInEx).</summary>
    public string PackageId { get; }

    public string Title { get; }
    public string Detail { get; }
    public string Recommendation { get; }

    /// <summary>True if downloading the host's mods resolves this issue.</summary>
    public bool ResolvedBySync { get; }

    public ModIssue(ModIssueSeverity severity, ModIssueKind kind, string packageId, string title, string detail, string recommendation, bool resolvedBySync = false)
    {
        Severity = severity;
        Kind = kind;
        PackageId = packageId;
        Title = title;
        Detail = detail;
        Recommendation = recommendation;
        ResolvedBySync = resolvedBySync;
    }

    public override string ToString() => $"[{Severity}] {Title}: {Detail}{(Recommendation.Length > 0 ? $" ({Recommendation})" : "")}";
}

public enum PackageSyncStatus
{
    UP_TO_DATE,
    MISSING,
    DIFFERENT_VERSION,
    DIFFERENT_FILES
}

public sealed class PackageComparison
{
    public ModPackage HostPackage { get; }

    /// <summary>The matching local package (same location, or same plugin GUID elsewhere), if any.</summary>
    public ModPackage? LocalPackage { get; }

    public PackageSyncStatus Status { get; }

    /// <summary>Host files missing locally or with different content.</summary>
    public IReadOnlyList<ModFile> FilesToDownload { get; }

    /// <summary>Local files (relative to BepInEx) that must go: stale files not in the host package, or a copy of the mod installed elsewhere.</summary>
    public IReadOnlyList<string> FilesToRemove { get; }

    public bool IsRequired => HostPackage.Requirement == ModRequirement.REQUIRED;

    public long DownloadSize => FilesToDownload.Sum(f => f.Size);

    public PackageComparison(ModPackage hostPackage, ModPackage? localPackage, PackageSyncStatus status, IReadOnlyList<ModFile> filesToDownload, IReadOnlyList<string> filesToRemove)
    {
        HostPackage = hostPackage;
        LocalPackage = localPackage;
        Status = status;
        FilesToDownload = filesToDownload;
        FilesToRemove = filesToRemove;
    }
}

/// <summary>Entry of the curated list of plugins known not to work in multiplayer.</summary>
public sealed class KnownIncompatibility
{
    public string Guid { get; set; } = "";
    public string Reason { get; set; } = "";
    public ModIssueSeverity Severity { get; set; } = ModIssueSeverity.WARNING;
}

/// <summary>What needs to happen locally to match a host. Created from a <see cref="ModCompatibilityReport" />.</summary>
public sealed class ModSyncPlan
{
    public string ServerName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public List<string> PackageIds { get; set; } = [];
    public List<ModFile> Downloads { get; set; } = [];
    public List<string> Removals { get; set; } = [];

    public long DownloadSize => Downloads.Sum(d => d.Size);
    public int ManagedAssemblyCount => Downloads.Count(d => d.Kind == ModFileKind.MANAGED_ASSEMBLY);
    public bool ContainsNativeCode => Downloads.Any(d => d.Kind == ModFileKind.NATIVE_LIBRARY);
    public bool ContainsExecutables => Downloads.Any(d => d.Kind == ModFileKind.EXECUTABLE);
    public bool IsEmpty => Downloads.Count == 0 && Removals.Count == 0;
}

public sealed class ModCompatibilityReport
{
    public ModManifest Host { get; }
    public ModManifest Local { get; }
    public IReadOnlyList<PackageComparison> Packages { get; }
    public IReadOnlyList<ModPackage> ExtraLocalPackages { get; }
    public IReadOnlyList<ModIssue> Issues { get; }

    public ModCompatibilityReport(ModManifest host, ModManifest local, IReadOnlyList<PackageComparison> packages, IReadOnlyList<ModPackage> extraLocalPackages, IReadOnlyList<ModIssue> issues)
    {
        Host = host;
        Local = local;
        Packages = packages;
        ExtraLocalPackages = extraLocalPackages;
        Issues = issues;
    }

    /// <summary>A required host package is missing or differs locally.</summary>
    public bool RequiresSync => Packages.Any(p => p.IsRequired && p.Status != PackageSyncStatus.UP_TO_DATE);

    public bool HasOptionalUpdates => Packages.Any(p => !p.IsRequired && p.Status != PackageSyncStatus.UP_TO_DATE);

    /// <summary>Errors that downloading the host's mods would not fix (missing dependencies on the host, incompatible plugins, no BepInEx).</summary>
    public IEnumerable<ModIssue> BlockingIssuesAfterSync => Issues.Where(i => i.Severity == ModIssueSeverity.ERROR && !i.ResolvedBySync);

    public bool IsFullyCompatible => !RequiresSync && !Issues.Any(i => i.Severity == ModIssueSeverity.ERROR);

    public ModSyncPlan CreatePlan(bool includeOptional, string serverName = "", string serverAddress = "")
    {
        ModSyncPlan plan = new() { ServerName = serverName, ServerAddress = serverAddress };
        HashSet<string> downloadPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> removalPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageComparison package in Packages)
        {
            if (package.Status == PackageSyncStatus.UP_TO_DATE || (!package.IsRequired && !includeOptional))
            {
                continue;
            }
            plan.PackageIds.Add(package.HostPackage.Id);
            foreach (ModFile file in package.FilesToDownload)
            {
                if (downloadPaths.Add(file.RelativePath))
                {
                    plan.Downloads.Add(file);
                }
            }
            foreach (string removal in package.FilesToRemove)
            {
                if (!downloadPaths.Contains(removal) && removalPaths.Add(removal))
                {
                    plan.Removals.Add(removal);
                }
            }
        }
        // A file that is replaced by a download isn't "removed" (it is backed up as part of the replacement).
        plan.Removals.RemoveAll(downloadPaths.Contains);
        return plan;
    }

    public string ToText()
    {
        System.Text.StringBuilder builder = new();
        builder.AppendLine($"Host: game {Display(Host.GameVersion)}, BepInEx {Display(Host.LoaderVersion)}, {Host.Packages.Count} mod package(s)");
        builder.AppendLine($"Local: game {Display(Local.GameVersion)}, BepInEx {Display(Local.LoaderVersion)}, {Local.Packages.Count} mod package(s)");
        foreach (PackageComparison package in Packages)
        {
            builder.AppendLine($"  {package.HostPackage.DisplayName} [{package.HostPackage.Id}] {package.HostPackage.PrimaryVersion}: {package.Status}{(package.IsRequired ? "" : " (optional)")}");
        }
        foreach (ModPackage extra in ExtraLocalPackages)
        {
            builder.AppendLine($"  {extra.DisplayName} [{extra.Id}] {extra.PrimaryVersion}: only installed locally");
        }
        foreach (ModIssue issue in Issues)
        {
            builder.AppendLine(issue.ToString());
        }
        return builder.ToString();

        static string Display(string value) => value.Length > 0 ? value : "not installed/unknown";
    }
}

/// <summary>
///     Compares a host's mod manifest with the local one and explains the differences in terms players can act on.
/// </summary>
public static class ModCompatibilityChecker
{
    public static ModCompatibilityReport Compare(ModManifest host, ModManifest local, IReadOnlyCollection<KnownIncompatibility>? knownIncompatibilities = null)
    {
        knownIncompatibilities ??= [];
        List<ModIssue> issues = [];
        List<PackageComparison> comparisons = [];
        HashSet<ModPackage> matchedLocalPackages = [];

        Dictionary<string, ModPackage> localById = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModPackage> localByGuid = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in local.Packages)
        {
            localById[package.Id] = package;
            foreach (ModPluginInfo plugin in package.Plugins)
            {
                if (!localByGuid.ContainsKey(plugin.Guid))
                {
                    localByGuid[plugin.Guid] = package;
                }
            }
        }

        foreach (ModPackage hostPackage in host.Packages)
        {
            ModPackage? localPackage = localById.TryGetValue(hostPackage.Id, out ModPackage? byId) ? byId : null;
            localPackage ??= hostPackage.Plugins.Select(p => localByGuid.TryGetValue(p.Guid, out ModPackage? byGuid) ? byGuid : null).FirstOrDefault(p => p != null);
            if (localPackage != null)
            {
                matchedLocalPackages.Add(localPackage);
            }
            PackageComparison comparison = ComparePackage(hostPackage, localPackage);
            comparisons.Add(comparison);
            AddPackageIssues(comparison, issues);
        }

        List<ModPackage> extraLocalPackages = local.Packages.Where(p => !matchedLocalPackages.Contains(p)).ToList();
        foreach (ModPackage extra in extraLocalPackages)
        {
            issues.Add(new ModIssue(ModIssueSeverity.WARNING, ModIssueKind.EXTRA_MOD, extra.Id, $"{extra.DisplayName} is only installed on your side",
                                    "The server does not use this mod. Visual or UI mods are usually fine; mods that change gameplay can cause desync for you or other players.",
                                    "Disable it in the Nitrod launcher (Mods) if you run into problems."));
        }

        AddGlobalIssues(host, local, issues);
        AddDependencyIssues(comparisons, extraLocalPackages, local.ProvidedPluginGuids, knownIncompatibilities, issues);

        return new ModCompatibilityReport(host, local, comparisons, extraLocalPackages,
                                          issues.OrderByDescending(i => i.Severity).ThenBy(i => i.PackageId, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    ///     Programs and scripts (.exe, .bat, .ps1, ...) shipped inside mod folders are helper tools, never needed by BepInEx to load
    ///     the mod. They are not synchronized and don't count as differences, so players never have to approve them.
    /// </summary>
    private static IEnumerable<ModFile> SyncableFiles(ModPackage package) => package.Files.Where(f => f.Kind != ModFileKind.EXECUTABLE);

    private static PackageComparison ComparePackage(ModPackage hostPackage, ModPackage? localPackage)
    {
        List<ModFile> hostFiles = SyncableFiles(hostPackage).ToList();
        if (localPackage == null)
        {
            return new PackageComparison(hostPackage, null, PackageSyncStatus.MISSING, hostFiles, []);
        }

        List<ModFile> downloads = [];
        List<string> removals = [];
        bool sameLocation = localPackage.Id.Equals(hostPackage.Id, StringComparison.OrdinalIgnoreCase);
        if (sameLocation)
        {
            Dictionary<string, ModFile> localFiles = SyncableFiles(localPackage).ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
            HashSet<string> hostPaths = new(hostPackage.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (ModFile hostFile in hostFiles)
            {
                if (!localFiles.TryGetValue(hostFile.RelativePath, out ModFile? localFile) || !localFile.Sha256.Equals(hostFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    downloads.Add(hostFile);
                }
            }
            removals.AddRange(localFiles.Values.Where(f => !hostPaths.Contains(f.RelativePath)).Select(f => f.RelativePath));
        }
        else
        {
            // Same mod installed in a different folder: install the host's copy at the host's location and retire the local copy,
            // otherwise BepInEx would see the same plugin twice. Identical files are copied locally instead of downloaded.
            downloads.AddRange(hostFiles);
            removals.AddRange(localPackage.Files.Select(f => f.RelativePath));
        }

        PackageSyncStatus status;
        if (downloads.Count == 0 && removals.Count == 0)
        {
            status = PackageSyncStatus.UP_TO_DATE;
        }
        else
        {
            bool versionDiffers = hostPackage.Plugins.Any(hp => localPackage.Plugins.FirstOrDefault(lp => lp.Guid.Equals(hp.Guid, StringComparison.OrdinalIgnoreCase)) is not { } lp || lp.Version != hp.Version);
            status = versionDiffers ? PackageSyncStatus.DIFFERENT_VERSION : PackageSyncStatus.DIFFERENT_FILES;
        }
        return new PackageComparison(hostPackage, localPackage, status, downloads, removals);
    }

    private static void AddPackageIssues(PackageComparison comparison, List<ModIssue> issues)
    {
        ModPackage host = comparison.HostPackage;
        ModIssueSeverity severity = comparison.IsRequired ? ModIssueSeverity.ERROR : ModIssueSeverity.INFO;
        string optionalNote = comparison.IsRequired ? "" : " It is optional on this server.";
        switch (comparison.Status)
        {
            case PackageSyncStatus.MISSING:
                issues.Add(new ModIssue(severity, ModIssueKind.MISSING_MOD, host.Id, $"{host.DisplayName} is not installed",
                                        $"The server uses {host.DisplayName} {host.PrimaryVersion}.{optionalNote}", "Download it from the host.", true));
                break;
            case PackageSyncStatus.DIFFERENT_VERSION:
                string localVersion = comparison.LocalPackage!.PrimaryVersion;
                issues.Add(new ModIssue(severity, ModIssueKind.DIFFERENT_VERSION, host.Id, $"{host.DisplayName} version differs",
                                        $"The server has version {Display(host.PrimaryVersion)}, you have {Display(localVersion)}.{optionalNote}",
                                        "Download the host's version. Your current files are backed up and can be restored.", true));
                break;
            case PackageSyncStatus.DIFFERENT_FILES:
                issues.Add(new ModIssue(severity, ModIssueKind.DIFFERENT_FILES, host.Id, $"{host.DisplayName} files differ",
                                        $"Same version, but {comparison.FilesToDownload.Count} file(s) differ and {comparison.FilesToRemove.Count} extra file(s) exist locally.{optionalNote}",
                                        "Download the host's files. Your current files are backed up and can be restored.", true));
                break;
        }

        if (comparison.Status == PackageSyncStatus.UP_TO_DATE)
        {
            return;
        }
        if (comparison.FilesToDownload.Any(f => f.Kind == ModFileKind.NATIVE_LIBRARY))
        {
            issues.Add(new ModIssue(ModIssueSeverity.WARNING, ModIssueKind.NATIVE_CODE, host.Id, $"{host.DisplayName} contains native code",
                                    "Native libraries can't be inspected like normal mod DLLs.", "Only accept this if you trust the host and the mod."));
        }
        int skippedExecutables = host.Files.Count(f => f.Kind == ModFileKind.EXECUTABLE);
        if (skippedExecutables > 0)
        {
            issues.Add(new ModIssue(ModIssueSeverity.INFO, ModIssueKind.EXECUTABLE_FILE, host.Id, $"{host.DisplayName} includes {skippedExecutables} program(s) or script(s)",
                                    "They are helper tools that are not needed to play, so they are not downloaded.", ""));
        }
    }

    private static void AddGlobalIssues(ModManifest host, ModManifest local, List<ModIssue> issues)
    {
        if (host.GameVersion.Length > 0 && local.GameVersion.Length > 0 && host.GameVersion != local.GameVersion)
        {
            issues.Add(new ModIssue(ModIssueSeverity.WARNING, ModIssueKind.GAME_VERSION_MISMATCH, "", "Different Subnautica version",
                                    $"The server uses game build {host.GameVersion}, you have {local.GameVersion}. Mods and multiplayer sync can behave differently.",
                                    "Update Subnautica on both sides to the same version."));
        }
        if (host.Packages.Count > 0 && !local.IsLoaderInstalled)
        {
            issues.Add(new ModIssue(ModIssueSeverity.ERROR, ModIssueKind.LOADER_MISSING, "", "BepInEx is not installed",
                                    "The server uses BepInEx mods, but BepInEx (the mod loader) is not installed in your game folder.",
                                    "Install BepInEx from the Nitrod launcher (Mods > Install BepInEx), then join again."));
        }
        else if (host.IsLoaderInstalled && local.IsLoaderInstalled && host.LoaderVersion != local.LoaderVersion)
        {
            issues.Add(new ModIssue(ModIssueSeverity.INFO, ModIssueKind.LOADER_VERSION_MISMATCH, "", "Different BepInEx version",
                                    $"The server uses BepInEx {host.LoaderVersion}, you have {local.LoaderVersion}.", "Usually harmless. Reinstall BepInEx if mods fail to load."));
        }
    }

    private static void AddDependencyIssues(IReadOnlyList<PackageComparison> comparisons, IReadOnlyList<ModPackage> extraLocalPackages, IReadOnlyCollection<string> localProvidedPluginGuids,
                                            IReadOnlyCollection<KnownIncompatibility> knownIncompatibilities, List<ModIssue> issues)
    {
        // Plugins installed locally outside of synced packages (e.g. BepInEx pack helpers) satisfy dependencies too.
        HashSet<string> providedPluginGuids = new(localProvidedPluginGuids, StringComparer.OrdinalIgnoreCase);
        // The plugins that will be present after syncing all required packages: host packages plus local-only packages.
        List<(ModPackage Package, ModPluginInfo Plugin, bool FromHost)> plugins = [];
        foreach (PackageComparison comparison in comparisons)
        {
            ModPackage source = comparison.IsRequired || comparison.LocalPackage == null ? comparison.HostPackage : comparison.LocalPackage;
            if (!comparison.IsRequired && comparison.LocalPackage == null)
            {
                continue; // Optional and not installed: won't be present unless the player opts in.
            }
            plugins.AddRange(source.Plugins.Select(p => (source, p, true)));
        }
        plugins.AddRange(extraLocalPackages.SelectMany(package => package.Plugins.Select(p => (package, p, false))));

        Dictionary<string, (ModPackage Package, ModPluginInfo Plugin, bool FromHost)> byGuid = new(StringComparer.OrdinalIgnoreCase);
        foreach ((ModPackage Package, ModPluginInfo Plugin, bool FromHost) entry in plugins)
        {
            if (byGuid.TryGetValue(entry.Plugin.Guid, out (ModPackage Package, ModPluginInfo Plugin, bool FromHost) existing) && existing.Package != entry.Package)
            {
                issues.Add(new ModIssue(ModIssueSeverity.WARNING, ModIssueKind.DUPLICATE_PLUGIN, entry.Package.Id, $"{entry.Plugin.Name} is installed twice",
                                        $"Both {existing.Package.Id} and {entry.Package.Id} contain plugin {entry.Plugin.Guid}. BepInEx only loads one of them.",
                                        "Remove one of the copies."));
                continue;
            }
            byGuid[entry.Plugin.Guid] = entry;
        }

        foreach ((ModPackage package, ModPluginInfo plugin, bool fromHost) in byGuid.Values)
        {
            foreach (ModDependencyInfo dependency in plugin.Dependencies.Where(d => d.IsHard))
            {
                if (providedPluginGuids.Contains(dependency.Guid))
                {
                    continue;
                }
                if (!byGuid.TryGetValue(dependency.Guid, out (ModPackage Package, ModPluginInfo Plugin, bool FromHost) provider))
                {
                    issues.Add(new ModIssue(ModIssueSeverity.ERROR, ModIssueKind.MISSING_DEPENDENCY, package.Id, $"{plugin.Name} is missing a dependency",
                                            $"{plugin.Name} requires {dependency}, which {(fromHost ? "the server does not have either" : "is not installed")}.",
                                            fromHost ? "Ask the host to install it, or to remove the mod that needs it." : $"Install {dependency.Guid} or disable {plugin.Name}."));
                }
                else if (dependency.MinimumVersion.Length > 0 && CompareVersions(provider.Plugin.Version, dependency.MinimumVersion) < 0)
                {
                    issues.Add(new ModIssue(ModIssueSeverity.ERROR, ModIssueKind.MISSING_DEPENDENCY, package.Id, $"{plugin.Name} needs a newer {provider.Plugin.Name}",
                                            $"{plugin.Name} requires {dependency.Guid} {dependency.MinimumVersion} or newer, but {provider.Plugin.Version} is installed.",
                                            "Update the dependency (on the host if both of you use it)."));
                }
            }
            foreach (string incompatibleGuid in plugin.Incompatibilities)
            {
                if (byGuid.TryGetValue(incompatibleGuid, out (ModPackage Package, ModPluginInfo Plugin, bool FromHost) other))
                {
                    issues.Add(new ModIssue(ModIssueSeverity.ERROR, ModIssueKind.INCOMPATIBLE_MODS, package.Id, $"{plugin.Name} is incompatible with {other.Plugin.Name}",
                                            $"{plugin.Name} declares that it does not work together with {other.Plugin.Name} ({incompatibleGuid}).",
                                            "Remove one of them."));
                }
            }
        }

        foreach (KnownIncompatibility known in knownIncompatibilities)
        {
            if (byGuid.TryGetValue(known.Guid, out (ModPackage Package, ModPluginInfo Plugin, bool FromHost) match))
            {
                issues.Add(new ModIssue(known.Severity, ModIssueKind.KNOWN_INCOMPATIBILITY, match.Package.Id, $"{match.Plugin.Name} has known multiplayer problems",
                                        known.Reason, "Consider disabling it for multiplayer sessions."));
            }
        }
    }

    /// <summary>Compares dotted numeric versions ("1.0.0.50", "2.1.0-pre.3"). Non-numeric parts compare as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] left = ParseParts(a);
        int[] right = ParseParts(b);
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            int l = i < left.Length ? left[i] : 0;
            int r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }
        return 0;

        static int[] ParseParts(string version)
        {
            string numeric = (version ?? "").Split('-', '+', ' ')[0];
            return numeric.Split('.').Select(part => int.TryParse(part, out int value) ? value : 0).ToArray();
        }
    }

    private static string Display(string version) => version.Length > 0 ? version : "unknown";
}
