using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Services;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels.Abstract;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.Logger;
using Nitrox.Model.ModSync;

namespace Nitrox.Launcher.ViewModels;

internal sealed record DiagnosticEntry(string Label, string Value);

internal sealed record LogFileItem(string Name, string FullPath, string ModifiedText);

/// <summary>Collects everything needed to understand why mods or multiplayer don't work, in one place that can be copied.</summary>
internal partial class DiagnosticsViewModel : RoutableViewModelBase
{
    // Written by the game client (see ModSyncJoinFlow and Patcher). Kept as literals: the launcher doesn't reference the client.
    private const string COMPATIBILITY_REPORT_FILE = "nitrod-compatibility-report.txt";
    private const string HARMONY_REPORT_FILE = "nitrod-harmony-overlaps.txt";
    private const int MAX_LOG_LINES = 150;

    [ObservableProperty]
    public partial AvaloniaList<DiagnosticEntry> Environment { get; set; } = [];

    [ObservableProperty]
    public partial AvaloniaList<ModIssueItem> ModIssues { get; set; } = [];

    [ObservableProperty]
    public partial AvaloniaList<string> LoaderErrors { get; set; } = [];

    [ObservableProperty]
    public partial AvaloniaList<LogFileItem> LogFiles { get; set; } = [];

    [ObservableProperty]
    public partial string CompatibilityReport { get; set; } = "";

    [ObservableProperty]
    public partial string HarmonyReport { get; set; } = "";

    public bool HasModIssues => ModIssues.Count > 0;
    public bool HasLoaderErrors => LoaderErrors.Count > 0;
    public bool HasCompatibilityReport => CompatibilityReport.Length > 0;
    public bool HasHarmonyReport => HarmonyReport.Length > 0;

    internal override Task ViewContentLoadAsync(CancellationToken cancellationToken = default) => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        string? gamePath = ModManagerService.CurrentGamePath;
        ModManifest? manifest = null;
        IReadOnlyList<ModIssue> issues = [];
        if (gamePath != null)
        {
            try
            {
                manifest = await ModManagerService.ScanAsync(gamePath);
                issues = ModManagerService.CheckLocal(manifest);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Diagnostics: failed to scan mods");
            }
        }

        List<DiagnosticEntry> environment =
        [
            new("Nitrod", $"{NitroxEnvironment.DisplayVersion} ({NitroxEnvironment.GitShortHash})"),
            new("Operating system", RuntimeInformation.OSDescription),
            new("Subnautica", gamePath ?? "Not found. Set it in Settings."),
            new("Store", NitroxUser.GamePlatform?.Name ?? "Standalone / unknown"),
            new("Game build", gamePath == null ? "-" : Display(ModScanner.ReadGameVersion(gamePath))),
            new("BepInEx", gamePath == null ? "-" : Display(ModScanner.ReadLoaderVersion(gamePath), "Not installed")),
            new("Enabled mods", manifest == null ? "-" : $"{manifest.Packages.Count} ({ModSyncLimits.FormatSize(manifest.TotalSize)})"),
            new("Logs folder", Log.LogDirectory),
            new("Config folder", NitroxDirectory.ConfigPath)
        ];

        List<string> loaderErrors = gamePath == null ? [] : ReadLoaderErrors(Path.Combine(gamePath, "BepInEx", "LogOutput.log"));
        List<LogFileItem> logFiles = [];
        try
        {
            logFiles = new DirectoryInfo(Log.LogDirectory).EnumerateFiles("*.log")
                                                          .OrderByDescending(f => f.LastWriteTimeUtc)
                                                          .Take(10)
                                                          .Select(f => new LogFileItem(f.Name, f.FullName, f.LastWriteTime.ToString("g")))
                                                          .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Diagnostics: unable to list logs: {ex.Message}");
        }
        string compatibilityReport = ReadTextOrEmpty(Path.Combine(Log.LogDirectory, COMPATIBILITY_REPORT_FILE));
        string harmonyReport = ReadTextOrEmpty(Path.Combine(Log.LogDirectory, HARMONY_REPORT_FILE));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Environment = new AvaloniaList<DiagnosticEntry>(environment);
            ModIssues = new AvaloniaList<ModIssueItem>(issues.Select(ModIssueItem.From));
            LoaderErrors = new AvaloniaList<string>(loaderErrors);
            LogFiles = new AvaloniaList<LogFileItem>(logFiles);
            CompatibilityReport = compatibilityReport;
            HarmonyReport = harmonyReport;
            OnPropertyChanged(nameof(HasModIssues));
            OnPropertyChanged(nameof(HasLoaderErrors));
            OnPropertyChanged(nameof(HasCompatibilityReport));
            OnPropertyChanged(nameof(HasHarmonyReport));
        });
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        StringBuilder text = new();
        text.AppendLine("=== Nitrod diagnostics ===");
        foreach (DiagnosticEntry entry in Environment)
        {
            text.AppendLine($"{entry.Label}: {entry.Value}");
        }
        AppendSection(text, "Mod problems", ModIssues.Select(i => $"[{i.Severity}] {i.Title}: {i.Detail}"));
        AppendSection(text, "BepInEx errors", LoaderErrors);
        AppendSection(text, "Last server compatibility check", CompatibilityReport.Length > 0 ? [CompatibilityReport] : []);
        AppendSection(text, "Harmony overlaps", HarmonyReport.Length > 0 ? [HarmonyReport] : []);
        await StaticCommands.CopyToClipboardCommand.ExecuteAsync(text.ToString());
        LauncherNotifier.Success("Diagnostics copied. They include folder paths; review before sharing.");

        static void AppendSection(StringBuilder builder, string title, IEnumerable<string> lines)
        {
            builder.AppendLine();
            builder.AppendLine($"=== {title} ===");
            bool any = false;
            foreach (string line in lines)
            {
                builder.AppendLine(line);
                any = true;
            }
            if (!any)
            {
                builder.AppendLine("(none)");
            }
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenPath(Log.LogDirectory);

    [RelayCommand]
    private void OpenBepInExLog()
    {
        if (ModManagerService.CurrentGamePath is { } gamePath)
        {
            string log = Path.Combine(gamePath, "BepInEx", "LogOutput.log");
            OpenPath(File.Exists(log) ? log : Path.Combine(gamePath, "BepInEx"));
        }
    }

    [RelayCommand]
    private void OpenLog(LogFileItem? item)
    {
        if (item != null)
        {
            OpenPath(item.FullPath);
        }
    }

    /// <summary>Errors from the last game session's BepInEx log: plugins that failed to load, missing dependencies, exceptions.</summary>
    internal static List<string> ReadLoaderErrors(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }
        try
        {
            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            List<string> result = [];
            HashSet<string> seen = [];
            while (reader.ReadLine() is { } line)
            {
                bool relevant = line.StartsWith("[Error", StringComparison.Ordinal) || line.StartsWith("[Fatal", StringComparison.Ordinal) ||
                                line.Contains("missing dependencies", StringComparison.OrdinalIgnoreCase) || line.Contains("incompatible with", StringComparison.OrdinalIgnoreCase);
                if (relevant && seen.Add(line))
                {
                    result.Add(line.Length > 400 ? $"{line[..400]}…" : line);
                    if (result.Count >= MAX_LOG_LINES)
                    {
                        break;
                    }
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"Unable to read {logPath}: {ex.Message}"];
        }
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Display(string value, string fallback = "Unknown") => value.Length > 0 ? value : fallback;
}
