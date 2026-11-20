using System;
using System.Collections.Generic;
using System.Linq;
using Nitrox.Model.ModSync;

namespace Nitrox.Launcher.Models.Design;

public enum ModHealth
{
    OK,
    WARNING,
    ERROR
}

/// <summary>An installed (or disabled) mod package as shown in the launcher.</summary>
internal sealed record ModPackageItem(
    string Id,
    string Name,
    string Version,
    string Location,
    string PluginIds,
    string Dependencies,
    string SizeText,
    int FileCount,
    bool IsEnabled,
    string SyncedFrom,
    ModHealth Health,
    string HealthText)
{
    public bool HasVersion => Version.Length > 0;
    public bool IsSynced => SyncedFrom.Length > 0;
    public bool HasDependencies => Dependencies.Length > 0;
    public bool HasIssue => Health != ModHealth.OK;
    public bool IsWarning => Health == ModHealth.WARNING;
    public bool IsError => Health == ModHealth.ERROR;

    public static ModPackageItem From(ModPackage package, IReadOnlyCollection<ModIssue> issues, string syncedFrom)
    {
        List<ModIssue> packageIssues = issues.Where(i => i.PackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        ModHealth health = packageIssues.Any(i => i.Severity == ModIssueSeverity.ERROR) ? ModHealth.ERROR : packageIssues.Count > 0 ? ModHealth.WARNING : ModHealth.OK;
        string dependencies = string.Join(", ", package.Plugins.SelectMany(p => p.Dependencies).Where(d => d.IsHard).Select(d => d.ToString()).Distinct());
        return new ModPackageItem(package.Id, package.DisplayName, package.PrimaryVersion, package.Id,
                                  string.Join(", ", package.Plugins.Select(p => p.Guid)), dependencies, ModSyncLimits.FormatSize(package.TotalSize), package.Files.Count, true,
                                  syncedFrom, health, packageIssues.Count > 0 ? packageIssues[0].Title : "Ready");
    }

    public static ModPackageItem Disabled(string id) =>
        new(id, id.Substring(id.IndexOf('/') + 1), "", id, "", "", "", 0, false, "", ModHealth.OK, "Disabled");
}

internal sealed record ModIssueItem(ModIssueSeverity Severity, string Title, string Detail, string Recommendation)
{
    public bool IsError => Severity == ModIssueSeverity.ERROR;
    public bool IsWarning => Severity == ModIssueSeverity.WARNING;
    public bool HasRecommendation => Recommendation.Length > 0;

    public static ModIssueItem From(ModIssue issue) => new(issue.Severity, issue.Title, issue.Detail, issue.Recommendation);
}

internal sealed record SyncRecordItem(string Id, string ServerName, string ServerAddress, string InstalledText, string Summary, bool Uninstalled)
{
    public bool CanUninstall => !Uninstalled;

    public static SyncRecordItem From(ModSyncRecord record) =>
        new(record.Id, record.ServerName, record.ServerAddress, record.InstalledUtc.ToLocalTime().ToString("g"),
            $"{record.Files.Count} file(s) installed, {record.Files.Count(f => f.BackupPath.Length > 0) + record.Removed.Count} backed up · {string.Join(", ", record.PackageIds.Select(id => id.Substring(id.IndexOf('/') + 1)))}",
            record.Uninstalled);
}
