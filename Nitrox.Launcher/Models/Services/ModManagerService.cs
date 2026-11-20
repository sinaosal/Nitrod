using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.Logger;
using Nitrox.Model.ModSync;
using Nitrox.Model.Platforms.OS.Shared;

namespace Nitrox.Launcher.Models.Services;

/// <summary>
///     Launcher side of mod management: lists installed BepInEx mods, enables/disables them, checks local compatibility, and
///     installs or undoes mods synchronized from servers. Plugin metadata is read without loading any mod assembly.
/// </summary>
internal static class ModManagerService
{
    /// <summary>Disabled packages are moved here, next to plugins/patchers, so BepInEx doesn't see them.</summary>
    public const string DISABLED_FOLDER_NAME = "NitrodDisabled";

    public static string? CurrentGamePath => string.IsNullOrWhiteSpace(NitroxUser.GamePath) || !Directory.Exists(NitroxUser.GamePath) ? null : NitroxUser.GamePath;

    public static bool IsBepInExInstalled(string gamePath) => OperatingSystem.IsWindows() ? BepInExInstaller.IsInstalled(gamePath) : BepInExModInstaller.IsBepInExInstalled(gamePath);

    public static bool IsGameRunning() => ProcessEx.ProcessExists(GameInfo.Subnautica.ExeName);

    public static Task<ModManifest> ScanAsync(string gamePath) => Task.Run(() =>
    {
        ModSyncStore store = new(gamePath);
        return new ModScanner(AssemblyMetadataPluginReader.Instance, new ModFileHasher(store.HashCacheFilePath)).Scan(gamePath);
    });

    /// <summary>Dependency, incompatibility and known-problem findings for the local installation alone.</summary>
    public static IReadOnlyList<ModIssue> CheckLocal(ModManifest manifest)
    {
        ModCompatibilityReport report = ModCompatibilityChecker.Compare(manifest, manifest, KnownIncompatibilityList.Load(NitroxDirectory.ConfigPath));
        return report.Issues.Where(i => i.Kind is ModIssueKind.MISSING_DEPENDENCY or ModIssueKind.INCOMPATIBLE_MODS or ModIssueKind.KNOWN_INCOMPATIBILITY or ModIssueKind.DUPLICATE_PLUGIN).ToList();
    }

    public static IReadOnlyList<string> GetDisabledPackageIds(string gamePath)
    {
        string disabledRoot = Path.Combine(ModScanner.GetBepInExRoot(gamePath), DISABLED_FOLDER_NAME);
        List<string> result = [];
        foreach (string root in ModPathSafety.AllowedRoots)
        {
            string rootPath = Path.Combine(disabledRoot, root);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }
            result.AddRange(Directory.EnumerateFileSystemEntries(rootPath).Select(entry => $"{root}/{Path.GetFileName(entry)}"));
        }
        return result;
    }

    /// <summary>Moves a package out of BepInEx's plugin folders. Throws if the game is running or the id is unsafe.</summary>
    public static void DisablePackage(string gamePath, string packageId) => MovePackage(gamePath, packageId, enable: false);

    public static void EnablePackage(string gamePath, string packageId) => MovePackage(gamePath, packageId, enable: true);

    private static void MovePackage(string gamePath, string packageId, bool enable)
    {
        if (IsGameRunning())
        {
            throw new InvalidOperationException("Close Subnautica before enabling or disabling mods.");
        }
        string bepInEx = ModScanner.GetBepInExRoot(gamePath);
        string enabledPath = ModPathSafety.ResolveInside(bepInEx, packageId);
        string disabledPath = ModPathSafety.ResolveInside(Path.Combine(bepInEx, DISABLED_FOLDER_NAME), packageId);
        (string from, string to) = enable ? (disabledPath, enabledPath) : (enabledPath, disabledPath);
        if (File.Exists(to) || Directory.Exists(to))
        {
            throw new IOException($"'{packageId}' already exists in the {(enable ? "enabled" : "disabled")} mods.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else
        {
            throw new FileNotFoundException($"'{packageId}' was not found.");
        }
        Log.Info($"{(enable ? "Enabled" : "Disabled")} mod package '{packageId}'");
    }

    public static PendingModSync? LoadPending(string gamePath) => new ModSyncStore(gamePath).LoadPending();

    public static ModSyncLedger LoadLedger(string gamePath) => new ModSyncStore(gamePath).LoadLedger();

    public static Task<ModSyncRecord> ApplyPendingAsync(string gamePath)
    {
        if (IsGameRunning())
        {
            throw new ModInstallException("Close Subnautica first. Mods can't be installed while the game is running.");
        }
        return Task.Run(() => new ModSyncStore(gamePath).ApplyPending());
    }

    public static void DiscardPending(string gamePath) => new ModSyncStore(gamePath).DiscardPending();

    /// <summary>Undoes a synchronized install. Returns files that were left alone because they changed after the install.</summary>
    public static Task<List<string>> UninstallAsync(string gamePath, string recordId)
    {
        if (IsGameRunning())
        {
            throw new ModInstallException("Close Subnautica first. Mods can't be removed while the game is running.");
        }
        return Task.Run(() => new ModSyncStore(gamePath).Uninstall(recordId));
    }

    /// <summary>
    ///     Called right before the game starts: offers to install mods downloaded from a server while the game was running.
    ///     Never blocks the launch on failure.
    /// </summary>
    public static async Task OfferPendingInstallAsync(string gamePath, DialogService dialogService)
    {
        PendingModSync? pending;
        try
        {
            pending = LoadPending(gamePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unable to read pending mod sync");
            return;
        }
        if (pending == null)
        {
            return;
        }
        if (IsGameRunning())
        {
            LauncherNotifier.Warning($"Close Subnautica to install the mods downloaded from {pending.ServerName}.");
            return;
        }

        string packages = string.Join(Environment.NewLine, pending.PackageIds.Take(12).Select(id => $"  • {id.Substring(id.IndexOf('/') + 1)}"));
        DialogBoxViewModel? answer = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = $"Install mods from {pending.ServerName}?";
            model.Description = $"""
                                 {pending.Files.Count} file(s) ({ModSyncLimits.FormatSize(pending.TotalSize)}) were downloaded from {pending.ServerName} ({pending.ServerAddress}) on {pending.CreatedUtc.ToLocalTime():g} and verified with SHA-256.

                                 {packages}

                                 They will be installed into BepInEx before the game starts. Files they replace are backed up and you can undo this any time under Mods.
                                 """;
            model.ButtonOptions = ButtonOptions.YesNo;
        });
        if (answer?.SelectedOption != ButtonOptions.Yes)
        {
            LauncherNotifier.Info("Synchronized mods were not installed. You can install or discard them under Mods.");
            return;
        }

        try
        {
            ModSyncRecord record = await ApplyPendingAsync(gamePath);
            LauncherNotifier.Success($"Installed {record.Files.Count} file(s) from {record.ServerName}.");
        }
        catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error(ex, "Installing synchronized mods failed");
            await dialogService.ShowErrorAsync(ex, "Installing synchronized mods failed. Nothing was changed.");
        }
    }
}
