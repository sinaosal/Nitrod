using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.ModSync;
using Nitrox.Model.Packets;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ModSync;
using UnityEngine;

namespace NitroxClient.ModSync;

/// <summary>
///     Runs between receiving the server policy and entering the player name: compares the host's mods with the local ones, explains
///     differences, and (with the player's consent) downloads the host's mod files. Downloads are verified and installed by the
///     launcher the next time the game starts, never into the running game.
/// </summary>
public static class ModSyncJoinFlow
{
    public const string REPORT_FILE_NAME = "nitrod-compatibility-report.txt";
    private static readonly TimeSpan manifestTimeout = TimeSpan.FromSeconds(10);
    private const int MAX_LISTED_ITEMS = 8;

    /// <param name="serverAddress">"ip:port", shown to the player and stored with the sync.</param>
    /// <param name="continueJoin">Continues the normal join (name/password input).</param>
    /// <param name="abortJoin">Disconnects and returns to the server list.</param>
    public static async Task RunAsync(string serverAddress, Action continueJoin, Action abortJoin)
    {
        MainMenuModSyncPanel panel = MainMenuModSyncPanel.Instance;
        if (!panel)
        {
            Log.Warn("Mod sync panel is not available; skipping the mod compatibility check.");
            continueJoin();
            return;
        }

        // Resolve before touching the UI: the client belongs to this join attempt's lifetime scope.
        // DIMA001: static flow started from the static JoinServerBackend; there is no constructor to inject into.
#pragma warning disable DIMA001
        ModSyncClient client = NitroxServiceLocator.LocateService<ModSyncClient>();
#pragma warning restore DIMA001
        panel.ShowChecking(serverAddress);
        ModManifestResponse response = await client.RequestManifestAsync(manifestTimeout);
        if (response == null)
        {
            Log.Warn($"{serverAddress} did not answer the mod list request (it may run an older version); joining without a mod check.");
            continueJoin();
            return;
        }
        if (!response.SyncEnabled)
        {
            Log.Info("The host disabled mod sharing; joining without a mod check.");
            continueJoin();
            return;
        }

        string serverName = response.ServerName.Length > 0 ? response.ServerName : serverAddress;
        string gameRoot = Path.GetDirectoryName(Application.dataPath);
        ModSyncStore store = new(gameRoot);
        ChainloaderPluginReader pluginReader = new(); // Read BepInEx state on the main thread.
        ModManifest local = await Task.Run(() => new ModScanner(pluginReader, new ModFileHasher(store.HashCacheFilePath)).Scan(gameRoot));
        ModCompatibilityReport report = ModCompatibilityChecker.Compare(response.Manifest, local, KnownIncompatibilityList.Load(NitroxDirectory.ConfigPath));
        WriteReport(report, serverName, serverAddress);

        new Decision(panel, client, store, report, response, serverName, serverAddress, continueJoin, abortJoin).Start();
    }

    /// <summary>Writes the latest report next to the logs so the launcher's Diagnostics page can show it.</summary>
    private static void WriteReport(ModCompatibilityReport report, string serverName, string serverAddress)
    {
        string text = $"Server: {serverName} ({serverAddress}){Environment.NewLine}Checked: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{report.ToText()}";
        Log.Info($"Mod compatibility check:{Environment.NewLine}{text}");
        try
        {
            File.WriteAllText(Path.Combine(Log.LogDirectory, REPORT_FILE_NAME), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Unable to write the compatibility report: {ex.Message}");
        }
    }

    private sealed class Decision(
        MainMenuModSyncPanel panel,
        ModSyncClient client,
        ModSyncStore store,
        ModCompatibilityReport report,
        ModManifestResponse response,
        string serverName,
        string serverAddress,
        Action continueJoin,
        Action abortJoin)
    {
        private bool approvedNativeCode;
        private bool approvedExecutables;

        private ModSyncPlan Plan => report.CreatePlan(includeOptional: false, serverName, serverAddress);
        private bool MayJoinAnyway => !response.RequireMatchingMods;

        public void Start()
        {
            List<ModIssue> blocking = report.BlockingIssuesAfterSync.ToList();
            if (!report.RequiresSync && blocking.Count == 0)
            {
                foreach (ModIssue warning in report.Issues.Where(i => i.Severity == ModIssueSeverity.WARNING).Take(3))
                {
                    Log.InGame($"{warning.Title}. {warning.Detail}");
                }
                continueJoin();
                return;
            }

            if (store.HasPending && IsPendingFor(Plan))
            {
                ShowRestartRequired();
                return;
            }

            if (blocking.Count > 0)
            {
                ShowBlocking(blocking);
                return;
            }
            ShowSyncOffer();
        }

        private void ShowBlocking(List<ModIssue> blocking)
        {
            StringBuilder body = new();
            body.AppendLine(Language.main.Get("Nitrox_ModSync_BlockingIntro"));
            AppendIssues(body, blocking, "#FF8A65");
            AppendIssues(body, report.Issues.Where(i => i.Severity == ModIssueSeverity.ERROR && i.ResolvedBySync), "#FFFFFF");
            List<(string, Action, bool)> options = [];
            if (MayJoinAnyway)
            {
                options.Add((Language.main.Get("Nitrox_ModSync_JoinAnyway"), continueJoin, false));
            }
            options.Add((Language.main.Get("Nitrox_ModSync_Back"), abortJoin, true));
            panel.ShowMessage(Language.main.Get("Nitrox_ModSync_ProblemTitle"), body.ToString(), options.ToArray());
        }

        private void ShowSyncOffer()
        {
            ModSyncPlan plan = Plan;
            StringBuilder body = new();
            body.AppendLine(Language.main.Get("Nitrox_ModSync_Intro").Replace("{SERVER}", $"<b>{serverName}</b>"));
            foreach (PackageComparison package in report.Packages.Where(p => p.IsRequired && p.Status != PackageSyncStatus.UP_TO_DATE).Take(MAX_LISTED_ITEMS))
            {
                body.AppendLine($"• <b>{package.HostPackage.DisplayName}</b> {package.HostPackage.PrimaryVersion} <color=#FFFFFFAA>{Describe(package)}</color>");
            }
            int more = report.Packages.Count(p => p.IsRequired && p.Status != PackageSyncStatus.UP_TO_DATE) - MAX_LISTED_ITEMS;
            if (more > 0)
            {
                body.AppendLine($"<color=#FFFFFFAA>+{more}</color>");
            }
            AppendIssues(body, report.Issues.Where(i => i.Severity == ModIssueSeverity.WARNING), "#FFD54F");

            List<(string, Action, bool)> options = [];
            if (response.DownloadsAllowed)
            {
                body.AppendLine();
                body.Append(Language.main.Get("Nitrox_ModSync_DownloadExplanation").Replace("{SIZE}", ModSyncLimits.FormatSize(plan.DownloadSize)));
                options.Add((Language.main.Get("Nitrox_ModSync_Download").Replace("{SIZE}", ModSyncLimits.FormatSize(plan.DownloadSize)), RequestDownload, false));
            }
            else
            {
                body.AppendLine();
                body.Append(Language.main.Get("Nitrox_ModSync_DownloadsDisabled"));
            }
            if (MayJoinAnyway)
            {
                options.Add((Language.main.Get("Nitrox_ModSync_JoinWithout"), continueJoin, false));
            }
            options.Add((Language.main.Get("Nitrox_Cancel"), abortJoin, true));
            panel.ShowMessage(Language.main.Get("Nitrox_ModSync_Title"), body.ToString(), options.ToArray());
        }

        private void RequestDownload()
        {
            ModSyncPlan plan = Plan;
            // Native code and executables need a separate, explicit approval that names the files.
            if (plan.ContainsNativeCode && !approvedNativeCode || plan.ContainsExecutables && !approvedExecutables)
            {
                StringBuilder body = new();
                body.AppendLine(Language.main.Get("Nitrox_ModSync_NativeWarning"));
                foreach (ModFile file in plan.Downloads.Where(d => d.Kind is ModFileKind.NATIVE_LIBRARY or ModFileKind.EXECUTABLE).Take(MAX_LISTED_ITEMS))
                {
                    body.AppendLine($"• <color=#FFD54F>{file.RelativePath}</color> ({(file.Kind == ModFileKind.EXECUTABLE ? Language.main.Get("Nitrox_ModSync_KindExecutable") : Language.main.Get("Nitrox_ModSync_KindNative"))})");
                }
                panel.ShowMessage(Language.main.Get("Nitrox_ModSync_NativeTitle"), body.ToString(),
                                  (Language.main.Get("Nitrox_ModSync_TrustAndDownload"), () =>
                                  {
                                      approvedNativeCode = true;
                                      approvedExecutables = true;
                                      RequestDownload();
                                  }, false),
                                  (Language.main.Get("Nitrox_ModSync_Back"), ShowSyncOffer, true));
                return;
            }
            _ = DownloadAsync(plan).ContinueWithHandleError(ex => ShowError(ex.Message));
        }

        private async Task DownloadAsync(ModSyncPlan plan)
        {
            ModSyncLimits limits = new() { AllowNativeLibraries = approvedNativeCode, AllowExecutables = approvedExecutables };
            List<string> problems = limits.Validate(plan);
            if (problems.Count > 0)
            {
                ShowError(string.Join("\n", problems.Take(MAX_LISTED_ITEMS)));
                return;
            }

            using CancellationTokenSource cancellation = new();
            string title = Language.main.Get("Nitrox_ModSync_DownloadingTitle").Replace("{SERVER}", serverName);
            Progress<ModDownloadProgress> progress = new(p => panel.ShowProgress(title, p, cancellation.Cancel));
            try
            {
                Dictionary<string, string> localFiles = ModSyncStore.IndexLocalFiles(store.BepInExRoot, report.Local);
                await new ModDownloader(store.StagingRoot, client, localFilesByHash: localFiles).DownloadAsync(plan.Downloads, progress, cancellation.Token);
                store.SavePending(plan, limits);
            }
            catch (OperationCanceledException)
            {
                panel.ShowMessage(Language.main.Get("Nitrox_ModSync_CancelledTitle"), Language.main.Get("Nitrox_ModSync_Cancelled"),
                                  (Language.main.Get("Nitrox_ModSync_Resume"), RequestDownload, false),
                                  (Language.main.Get("Nitrox_ModSync_Back"), abortJoin, true));
                return;
            }
            catch (Exception ex) when (ex is ModDownloadException or ModInstallException or IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Mod download failed");
                ShowError(ex.Message);
                return;
            }

            Log.Info($"Downloaded and verified {plan.Downloads.Count} mod file(s) ({ModSyncLimits.FormatSize(plan.DownloadSize)}) from {serverAddress}. They will be installed on the next launch.");
            ShowRestartRequired();
        }

        private void ShowRestartRequired()
        {
            panel.ShowMessage(Language.main.Get("Nitrox_ModSync_RestartTitle"), Language.main.Get("Nitrox_ModSync_Restart"),
                              (Language.main.Get("Nitrox_ModSync_QuitGame"), Application.Quit, false),
                              (Language.main.Get("Nitrox_ModSync_Back"), abortJoin, true));
        }

        private void ShowError(string message)
        {
            panel.ShowMessage(Language.main.Get("Nitrox_ModSync_FailedTitle"),
                              $"{Language.main.Get("Nitrox_ModSync_Failed")}\n\n<color=#FF8A65>{message}</color>",
                              (Language.main.Get("Nitrox_ModSync_Retry"), RequestDownload, false),
                              (Language.main.Get("Nitrox_ModSync_Back"), abortJoin, true));
        }

        private bool IsPendingFor(ModSyncPlan plan)
        {
            PendingModSync pending = store.LoadPending();
            if (pending == null || plan.Downloads.Count == 0)
            {
                return false;
            }
            HashSet<string> staged = new(pending.Files.Select(f => $"{f.RelativePath}|{f.Sha256}"), StringComparer.OrdinalIgnoreCase);
            return plan.Downloads.All(d => staged.Contains($"{d.RelativePath}|{d.Sha256}"));
        }

        private static void AppendIssues(StringBuilder body, IEnumerable<ModIssue> issues, string color)
        {
            foreach (ModIssue issue in issues.Take(MAX_LISTED_ITEMS))
            {
                body.AppendLine($"• <color={color}>{issue.Title}</color>: <size=90%>{issue.Detail}</size>{(issue.Recommendation.Length > 0 ? $" <color=#FFFFFFAA><size=90%>{issue.Recommendation}</size></color>" : "")}");
            }
        }

        private static string Describe(PackageComparison package) => package.Status switch
        {
            PackageSyncStatus.MISSING => Language.main.Get("Nitrox_ModSync_StatusMissing"),
            PackageSyncStatus.DIFFERENT_VERSION => Language.main.Get("Nitrox_ModSync_StatusVersion").Replace("{VERSION}", package.LocalPackage?.PrimaryVersion is { Length: > 0 } v ? v : "?"),
            _ => Language.main.Get("Nitrox_ModSync_StatusFiles")
        };
    }
}
