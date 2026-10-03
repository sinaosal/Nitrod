using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Services;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels.Abstract;
using Nitrox.Model.Constants;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.Logger;
using Nitrox.Model.Platforms.OS.Shared;

namespace Nitrox.Launcher.ViewModels;

internal partial class UpdatesViewModel(NitroxWebsiteApiService nitroxWebsiteApi, DialogService dialogService, ServerService serverService, Func<Window> mainWindowProvider, BackupService backupService) : RoutableViewModelBase
{
    private readonly DialogService dialogService = dialogService;
    private readonly ServerService serverService = serverService;
    private readonly Func<Window> mainWindowProvider = mainWindowProvider;
    private readonly NitroxWebsiteApiService nitroxWebsiteApi = nitroxWebsiteApi;
    private readonly BackupService backupService = backupService;
    private CancellationTokenSource? downloadCts;

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    public partial string? DownloadStatus { get; set; }

    [ObservableProperty]
    public partial bool NewUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool ReleaseInstallerMissing { get; set; }

    [ObservableProperty]
    public partial AvaloniaList<NitroxChangelog> NitroxChangelogs { get; set; } = [];

    [ObservableProperty]
    public partial string? OfficialVersion { get; set; }

    [ObservableProperty]
    public partial bool UsingOfficialVersion { get; set; }

    [ObservableProperty]
    public partial AvaloniaList<BackupInfo> AvailableBackups { get; set; } = [];

    [ObservableProperty]
    public partial string? Version { get; set; }

    public bool ShowDownloadButton => NewUpdateAvailable && DownloadProgress <= 0;

    partial void OnNewUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(ShowDownloadButton));

    partial void OnDownloadProgressChanged(double value) => OnPropertyChanged(nameof(ShowDownloadButton));

    public async Task<bool> IsNitroxUpdateAvailableAsync()
    {
        try
        {
            Version currentVersion = NitroxEnvironment.Version;
            NitroxWebsiteApiService.NitroxRelease? latestRelease = await nitroxWebsiteApi.GetNitroxLatestVersionAsync();
            Version latestVersion = latestRelease?.Version ?? new Version(0, 0);

            bool releaseIsNewer = latestRelease?.IsNewerThan(NitroxEnvironment.DisplayVersion) == true;
            ReleaseInstallerMissing = releaseIsNewer && latestRelease?.CurrentPlatformInfo == null;
            NewUpdateAvailable = releaseIsNewer && latestRelease?.CurrentPlatformInfo != null;
            if (NewUpdateAvailable)
            {
                string versionMessage = $"A new version of the mod ({latestRelease!.DisplayVersion}) is available.";
                Log.Info(versionMessage);
                LauncherNotifier.Warning(versionMessage);
            }
            Version = NitroxEnvironment.DisplayVersion;
            OfficialVersion = latestRelease?.DisplayVersion ?? latestVersion.ToString();
            UsingOfficialVersion = NitroxEnvironment.IsReleaseMode && latestVersion >= currentVersion;
        }
        catch
        {
            NewUpdateAvailable = false;
            ReleaseInstallerMissing = false;
            UsingOfficialVersion = NitroxEnvironment.IsReleaseMode;
        }

        return NewUpdateAvailable || !UsingOfficialVersion;
    }

    internal override async Task ViewContentLoadAsync(CancellationToken cancellationToken = default)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                NitroxChangelogs.Clear();
                NitroxChangelogs.AddRange(await nitroxWebsiteApi.GetChangeLogsAsync(cancellationToken)! ?? []);

                // Load available backups
                RefreshBackups();
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    LauncherNotifier.Error("Failed to fetch Nitrod changelogs");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while trying to display Nitrod changelogs");
            }
        });
    }

    private static async Task<string> CreateUpdaterScriptAsync(string sourcePath, string destinationPath, string tempDir)
    {
        // Safety check: ensure destination path is rooted to prevent accidental file deletion
        if (!Path.IsPathRooted(destinationPath))
        {
            throw new ArgumentException("Destination path must be an absolute path", nameof(destinationPath));
        }

        string scriptPath;
        string scriptContent;
        string launcherFilePath = Path.Combine(destinationPath, UpdatePackage.GetLauncherFileName(sourcePath));

        if (OperatingSystem.IsWindows())
        {
            scriptPath = Path.Combine(tempDir, "update.bat");
            scriptContent = $"""
                             @echo off
                             echo Waiting for Nitrod Launcher to close...
                             :waitloop
                             tasklist /FI "PID eq {Environment.ProcessId}" /NH 2>NUL | findstr /R /C:" {Environment.ProcessId} ">NUL
                             if "%ERRORLEVEL%"=="0" (
                                 timeout /t 1 /nobreak >nul
                                 goto waitloop
                             )
                             echo Cleaning old installation...
                             for %%F in ("{destinationPath}\*.dll") do del /Q "%%F" 2>nul
                             for %%F in ("{destinationPath}\*.exe") do del /Q "%%F" 2>nul
                             for %%F in ("{destinationPath}\*.json") do del /Q "%%F" 2>nul
                             for %%F in ("{destinationPath}\*.config") do del /Q "%%F" 2>nul
                             for %%F in ("{destinationPath}\*.txt") do del /Q "%%F" 2>nul
                             if exist "{destinationPath}\lib" rmdir /S /Q "{destinationPath}\lib" 2>nul
                             if exist "{destinationPath}\runtimes" rmdir /S /Q "{destinationPath}\runtimes" 2>nul
                             if exist "{destinationPath}\Resources" rmdir /S /Q "{destinationPath}\Resources" 2>nul
                             echo Installing update...
                             xcopy /E /Y /I "{sourcePath}\*" "{destinationPath}\"
                             if errorlevel 1 (
                                 echo Update failed! Press any key to exit...
                                 pause >nul
                                 exit /b 1
                             )
                             if not exist "{launcherFilePath}" (
                                 echo Update failed: launcher is missing.
                                 exit /b 1
                             )
                             echo Starting Nitrod Launcher...
                             start "" /D "{destinationPath}" "{launcherFilePath}"
                             exit
                             """;
        }
        else
        {
            scriptPath = Path.Combine(tempDir, "update.sh");
            scriptContent = $"""
                             #!/bin/bash
                             echo "Waiting for Nitrod Launcher to close..."
                             while kill -0 {Environment.ProcessId} 2>/dev/null; do
                                 sleep 1
                             done
                             echo "Cleaning old installation..."
                             rm -f "{destinationPath}"/*.dll 2>/dev/null
                             rm -f "{destinationPath}"/*.exe 2>/dev/null
                             rm -f "{destinationPath}"/*.json 2>/dev/null
                             rm -f "{destinationPath}"/*.config 2>/dev/null
                             rm -f "{destinationPath}"/*.txt 2>/dev/null
                             rm -rf "{destinationPath}/lib" 2>/dev/null
                             rm -rf "{destinationPath}/runtimes" 2>/dev/null
                             rm -rf "{destinationPath}/Resources" 2>/dev/null
                             echo "Installing update..."
                             cp -rf "{sourcePath}/"* "{destinationPath}/" || exit 1
                             cd "{destinationPath}" || exit 1
                             echo "Starting Nitrod Launcher..."
                             chmod +x "{launcherFilePath}"
                             nohup "{launcherFilePath}" >/dev/null 2>&1 &
                             """;
        }

        await File.WriteAllTextAsync(scriptPath, scriptContent);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return scriptPath;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate), AllowConcurrentExecutions = false)]
    private async Task DownloadUpdate()
    {
        if (await nitroxWebsiteApi.GetNitroxLatestVersionAsync() is not { CurrentPlatformInfo: {} downloadInfo } latestRelease)
        {
            LauncherNotifier.Error("No update information available for your platform. Please refresh and try again.");
            return;
        }
        DialogBoxViewModel confirmResult = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = $"Download and install Nitrod {latestRelease.DisplayVersion} ({downloadInfo.FileSizeMegaBytes:F1} MB)?";
            if (NitroxEnvironment.IsReleaseMode)
            {
                model.Description = "This will overwrite your current Nitrod installation and restart Nitrod after the update is complete.\nPlease check if this update is compatible with your current save file before continuing.";
            }
            else
            {
                model.Description = "Development build detected. Please use git pull to update your local repository.";
            }
            model.Description += "\n\nDo you want to continue?";
            model.ButtonOptions = ButtonOptions.YesNo;
        });
        if (!confirmResult)
        {
            return;
        }

        DownloadProgress = 0;
        DownloadStatus = "Creating backup...";

        // Create backup before updating
        string? backupPath = await backupService.CreateBackupAsync(
            includeSaves: true,
            progress: new Progress<(int Progress, string Status)>(p =>
            {
                DownloadProgress = p.Progress * 0.1; // Backup is 10% of total progress
                DownloadStatus = p.Status;
            })
        );

        if (backupPath == null)
        {
            DialogBoxViewModel backupFailedResult = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
            {
                model.Title = "Backup failed";
                model.Description = "Failed to create a backup of your current installation. Do you want to continue with the update anyway?";
                model.ButtonOptions = ButtonOptions.YesNo;
            });
            if (!backupFailedResult)
            {
                DownloadProgress = 0;
                DownloadStatus = null;
                return;
            }
        }
        else
        {
            LauncherNotifier.Success($"Backup created: {Path.GetFileName(backupPath)}");
        }

        DownloadStatus = "Starting download...";
        using (downloadCts = new CancellationTokenSource())
        {
            try
            {
                string currentDir = NitroxUser.LauncherPath ?? AppDomain.CurrentDomain.BaseDirectory;
                string tempDir = Path.Combine(Path.GetTempPath(), $"NitroxUpdate {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                string zipPath = Path.Combine(tempDir, $"Nitrod_{latestRelease.DisplayVersion}.zip");
                string extractPath = Path.Combine(tempDir, "extract");

                Directory.CreateDirectory(tempDir);

                // Download the update
                DownloadStatus = "Downloading...";
                using (HttpFileService.FileDownloader? downloader = await nitroxWebsiteApi.GetLatestNitroxAsync(downloadInfo.DownloadUrl, downloadCts.Token))
                {
                    if (downloader == null)
                    {
                        return;
                    }
                    await foreach (long bytesRead in downloader.DownloadToFileInStepsAsync(zipPath))
                    {
                        if (downloader.SizeFromServer < 1)
                        {
                            continue;
                        }
                        DownloadProgress = (double)bytesRead / downloader.SizeFromServer * 100;
                        DownloadStatus = $"Downloading... {bytesRead / 1024.0 / 1024.0:F1} / {downloader.SizeFromServer / 1024.0 / 1024.0:F1} MB";
                    }
                }

                if (!string.IsNullOrEmpty(downloadInfo.Sha256Hash))
                {
                    DownloadStatus = "Verifying download...";
                    DownloadProgress = 100;
                    string downloadedHash = Convert.ToHexStringLower(await Hashing.GetSha256(zipPath));
                    if (!string.Equals(downloadedHash, downloadInfo.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"Download verification failed. Expected SHA-256: {downloadInfo.Sha256Hash}, got: {downloadedHash}");
                    }
                }
                else if (!string.IsNullOrEmpty(downloadInfo.Md5Hash))
                {
                    DownloadStatus = "Verifying download...";
                    DownloadProgress = 100;
                    string downloadedHash = await Hashing.ComputeMd5HashAsync(zipPath);
                    if (!string.Equals(downloadedHash, downloadInfo.Md5Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"Download verification failed. Expected hash: {downloadInfo.Md5Hash}, got: {downloadedHash}");
                    }
                }

                // Extract the update
                DownloadStatus = "Extracting...";
                TryDeleteDirectory(extractPath, true);
                await ZipFile.ExtractToDirectoryAsync(zipPath, extractPath);

                // Find the Nitrox folder inside the extracted content
                string nitroxFolder = UpdatePackage.FindLauncherDirectory(extractPath);

                // Create the updater batch script
                string scriptFilePath = await CreateUpdaterScriptAsync(nitroxFolder, currentDir, tempDir);

                DownloadStatus = "Installing update...";
                LauncherNotifier.Success("Update downloaded successfully. Restarting to apply update...");

                // Start the updater script and exit
                using Process? script = UpdatePackage.StartInstaller(scriptFilePath);
                if (script == null)
                {
                    throw new Exception("Failed to start the update installer. The launcher will remain open.");
                }
                mainWindowProvider().CloseByCode();
            }
            catch (OperationCanceledException)
            {
                DownloadProgress = 0;
                DownloadStatus = "Download cancelled";
                LauncherNotifier.Info("Update download cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download or install update");
                DownloadProgress = 0;
                DownloadStatus = "Download failed";
                await dialogService.ShowErrorAsync(ex, "Update Failed", "Failed to download or install the update.");
            }
        }
    }

    private bool CanDownloadUpdate() => !CanCancelDownload() && serverService.Servers.All(s => !s.IsOnline);

    internal async Task PromptForAvailableUpdateAsync()
    {
        if (NewUpdateAvailable && CanDownloadUpdate())
        {
            await DownloadUpdate();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        downloadCts?.Cancel();
    }

    private bool CanCancelDownload() => downloadCts is { IsCancellationRequested: false };

    [RelayCommand]
    private void RefreshBackups()
    {
        AvailableBackups.Clear();
        AvailableBackups.AddRange(backupService.GetAvailableBackups());
    }

    [RelayCommand]
    private async Task DeleteBackup(BackupInfo? backup)
    {
        if (backup == null)
        {
            return;
        }

        DialogBoxViewModel confirmResult = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = "Delete backup?";
            model.Description = $"Are you sure you want to delete the backup '{backup.FileName}'?\nThis action cannot be undone.";
            model.ButtonOptions = ButtonOptions.YesNo;
        });

        if (confirmResult && backupService.DeleteBackup(backup.FilePath))
        {
            AvailableBackups.Remove(backup);
            LauncherNotifier.Success("Backup deleted");
        }
    }

    [RelayCommand]
    private async Task RestoreBackup(BackupInfo? backup)
    {
        if (backup == null)
        {
            return;
        }

        DialogBoxViewModel confirmResult = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = "Restore backup?";
            model.Description = $"This will restore Nitrox to version {backup.Version} and overwrite your current installation.";
            if (backup.IncludesSaves)
            {
                model.Description += "\n\nThis backup includes save files which will also be restored, potentially overwriting your current saves.";
            }
            model.Description += "\n\nThe launcher will close and restart after the restore is complete.\n\nDo you want to continue?";
            model.ButtonOptions = ButtonOptions.YesNo;
        });

        if (!confirmResult)
        {
            return;
        }

        string? scriptPath = await backupService.CreateRestoreScriptAsync(backup.FilePath);
        if (scriptPath == null)
        {
            LauncherNotifier.Error("Failed to create restore script");
            return;
        }

        LauncherNotifier.Success("Restoring backup... The launcher will restart.");

        // Start the restore script and exit
        using Process? script = UpdatePackage.StartInstaller(scriptPath);
        if (script == null)
        {
            LauncherNotifier.Error("Failed to start the backup installer. The launcher will remain open.");
            return;
        }
        mainWindowProvider().CloseByCode();
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        string backupsDir = BackupService.BackupsDirectory;
        Directory.CreateDirectory(backupsDir);

        try
        {
            OpenPath(backupsDir);
        }
        catch (Exception ex)
        {
            LauncherNotifier.Error($"Failed to open backups folder: {ex.Message}");
        }
    }
}
