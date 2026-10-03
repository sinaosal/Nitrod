using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Services;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels.Abstract;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.Platforms.Discovery;
using Nitrox.Model.Platforms.Discovery.Models;
using Nitrox.Model.Platforms.OS.Shared;

namespace Nitrox.Launcher.ViewModels;

internal partial class OptionsViewModel(IKeyValueStore keyValueStore, StorageService storageService, DialogService dialogService, HttpFileService httpFileService) : RoutableViewModelBase
{
    private readonly IKeyValueStore keyValueStore = keyValueStore;
    private readonly StorageService storageService = storageService;
    private readonly DialogService dialogService = dialogService;
    private readonly BepInExInstaller bepInExInstaller = new(httpFileService);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetArgumentsCommand))]
    public partial string LaunchArgs { get; set; }

    [ObservableProperty]
    public partial string ProgramDataPath { get; set; }

    [ObservableProperty]
    public partial string ScreenshotsPath { get; set; }

    [ObservableProperty]
    public partial string SavesPath { get; set; }

    [ObservableProperty]
    public partial string LogsPath { get; set; }

    [ObservableProperty]
    public partial KnownGame SelectedGame { get; set; }

    [ObservableProperty]
    public partial bool ShowResetArgsBtn { get; set; }

    [ObservableProperty]
    public partial bool IsLightModeEnabled { get; set; }

    [ObservableProperty]
    public partial bool AllowMultipleGameInstances { get; set; }

    [ObservableProperty]
    public partial bool UseBigPictureMode { get; set; }

    [ObservableProperty]
    public partial bool IsDiscordEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsInReleaseMode { get; set; }

    [ObservableProperty]
    public partial bool LaunchDirectly { get; set; }

    private static string DefaultLaunchArg => "-vrmode none";
    private bool isResettingArgs;

    internal override async Task ViewContentLoadAsync(CancellationToken cancellationToken = default)
    {
        SelectedGame = new() { PathToGame = NitroxUser.GamePath, Platform = NitroxUser.GamePlatform?.Platform ?? Platform.NONE };
        LaunchDirectly = NitroxUser.PreferDirectLaunch;
        LaunchArgs = keyValueStore.GetLaunchArguments(GameInfo.Subnautica, DefaultLaunchArg);
        ProgramDataPath = NitroxDirectory.ConfigPath;
        ScreenshotsPath = NitroxDirectory.ScreenshotsPath;
        SavesPath = keyValueStore.GetSavesPath();
        LogsPath = Model.Logger.Log.LogDirectory;
        IsLightModeEnabled = keyValueStore.GetIsLightModeEnabled();
        AllowMultipleGameInstances = keyValueStore.GetIsMultipleGameInstancesAllowed();
        UseBigPictureMode = keyValueStore.GetUseBigPictureMode();
        IsDiscordEnabled = keyValueStore.GetIsDiscordEnabled();
        IsInReleaseMode = NitroxEnvironment.IsReleaseMode;
        await Task.Run(() => SetTargetedSubnauticaPath(SelectedGame.PathToGame), cancellationToken).ContinueWithHandleError(ex => LauncherNotifier.Error(ex.Message));
    }

    private static void SetTargetedSubnauticaPath(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        PirateDetection.TriggerOnDirectory(path);

        if (!FileSystem.Instance.IsWritable(path))
        {
            // Targeted game directory needs to be writable (Nitrox patches files into it)
            if (!FileSystem.Instance.SetFullAccessToCurrentUser(path))
            {
                LauncherNotifier.Error(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? "Restart Nitrod Launcher as admin to allow Nitrod to change permissions as needed. This is only needed once."
                        : $"Unable to set permissions on the directories at '{path}'. Please grant read and write permissions so Nitrod can work properly."
                );

                return;
            }
        }

        // Save game path as preferred for future sessions.
        NitroxUser.PreferredGamePath = path;
        NitroxUser.SetGamePathAndPlatform(path, null, NitroxUser.PreferDirectLaunch);
    }

    [RelayCommand]
    private async Task SetGamePath()
    {
        string selectedDirectory = await storageService.OpenFolderPickerAsync("Select Subnautica installation directory", SelectedGame.PathToGame);
        await ApplyGamePathAsync(selectedDirectory);
    }

    [RelayCommand]
    private async Task SetGameExecutable()
    {
        string selectedExecutable = (await storageService.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select {GameInfo.Subnautica.ExeName}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subnautica executable")
                {
                    Patterns = [GameInfo.Subnautica.ExeName]
                }
            ]
        })).FirstOrDefault()?.TryGetLocalPath() ?? "";

        await ApplyGamePathAsync(selectedExecutable, true);
    }

    [RelayCommand]
    private async Task InstallBepInExModAsync()
    {
        string gamePath = SelectedGame?.PathToGame ?? "";
        if (!Directory.Exists(gamePath))
        {
            LauncherNotifier.Error("Set the Subnautica installation path before installing a mod.");
            return;
        }

        string archivePath = (await storageService.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a BepInEx mod ZIP",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("BepInEx mod archive")
                {
                    Patterns = ["*.zip"]
                }
            ]
        })).FirstOrDefault()?.TryGetLocalPath() ?? "";

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        bool isBepInExInstalled = OperatingSystem.IsWindows()
            ? BepInExInstaller.IsInstalled(gamePath)
            : BepInExModInstaller.IsBepInExInstalled(gamePath);
        if (!isBepInExInstalled)
        {
            if (!OperatingSystem.IsWindows())
            {
                LauncherNotifier.Error("Install BepInEx for Subnautica before installing a mod archive.");
                return;
            }

            DialogBoxViewModel? loaderConfirmation = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
            {
                model.Title = "Install BepInEx first?";
                model.Description = "BepInEx is required to load this mod. Install the official BepInEx pack into the selected Subnautica folder now?";
                model.ButtonOptions = ButtonOptions.YesNo;
            });

            if (loaderConfirmation?.SelectedOption != ButtonOptions.Yes)
            {
                return;
            }

            try
            {
                await bepInExInstaller.InstallAsync(gamePath);
            }
            catch (Exception ex)
            {
                await dialogService.ShowErrorAsync(ex, "Failed to install BepInEx");
                return;
            }
        }

        DialogBoxViewModel? modConfirmation = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = "Install BepInEx mod?";
            model.Description = $"Install {Path.GetFileName(archivePath)} into the selected Subnautica installation? Existing files in BepInEx plugin folders may be replaced.";
            model.ButtonOptions = ButtonOptions.YesNo;
        });

        if (modConfirmation?.SelectedOption != ButtonOptions.Yes)
        {
            return;
        }

        try
        {
            int installedFiles = await Task.Run(() => BepInExModInstaller.Install(archivePath, gamePath));
            LauncherNotifier.Success($"Installed BepInEx mod archive ({installedFiles} files).");
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(ex, "Failed to install BepInEx mod");
        }
    }

    private async Task ApplyGamePathAsync(string selectedPath, bool enableDirectLaunch = false)
    {
        if (selectedPath == "")
        {
            return;
        }

        string? selectedDirectory = GameInstallationHelper.GetGameDirectory(selectedPath, GameInfo.Subnautica);
        if (selectedDirectory == null)
        {
            LauncherNotifier.Error($"Select a Subnautica installation directory or {GameInfo.Subnautica.ExeName}");
            return;
        }

        if (enableDirectLaunch)
        {
            LaunchDirectly = true;
        }

        if (!selectedDirectory.Equals(SelectedGame.PathToGame, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => SetTargetedSubnauticaPath(selectedDirectory));
            SelectedGame = new() { PathToGame = NitroxUser.GamePath, Platform = NitroxUser.GamePlatform?.Platform ?? Platform.NONE };
            LauncherNotifier.Success("Applied changes");
        }
    }

    [RelayCommand]
    private void ResetArguments(IInputElement? focusTargetAfterReset = null)
    {
        isResettingArgs = true;
        LaunchArgs = DefaultLaunchArg;
        SetArguments();
        isResettingArgs = false;

        focusTargetAfterReset?.Focus();
    }

    [RelayCommand(CanExecute = nameof(CanSetArguments))]
    private void SetArguments()
    {
        keyValueStore.SetLaunchArguments(GameInfo.Subnautica, LaunchArgs);
        SetArgumentsCommand.NotifyCanExecuteChanged();
    }

    private bool CanSetArguments()
    {
        ShowResetArgsBtn = LaunchArgs != DefaultLaunchArg;

        return LaunchArgs != keyValueStore.GetLaunchArguments(GameInfo.Subnautica, DefaultLaunchArg) && !isResettingArgs;
    }

    [RelayCommand]
    private void DisplaySteamOverlayNotification()
    {
        if (AllowMultipleGameInstances && SelectedGame.Platform == Platform.STEAM)
        {
            LauncherNotifier.Warning("Note: Enabling this option will disable Steam's in-game overlay. Disable this option to use Steam's overlay");
        }
    }

    [RelayCommand]
    private void OpenFolder(string? dir = null)
    {
        try
        {
            if (!OpenPath(dir))
            {
                LauncherNotifier.Error("Can't open. Directory does not exist.");
            }
        }
        catch (Exception ex)
        {
            LauncherNotifier.Error($"Failed to open folder: {ex.Message}");
        }
    }

    partial void OnIsLightModeEnabledChanged(bool value)
    {
        keyValueStore.SetIsLightModeEnabled(value);
        Dispatcher.UIThread.Invoke(() => Application.Current!.RequestedThemeVariant = value ? ThemeVariant.Light : ThemeVariant.Dark);
    }

    partial void OnAllowMultipleGameInstancesChanged(bool value)
    {
        if (value)
        {
            UseBigPictureMode = false;
        }
        keyValueStore.SetIsMultipleGameInstancesAllowed(value);
    }

    partial void OnUseBigPictureModeChanged(bool value)
    {
        if (value)
        {
            AllowMultipleGameInstances = false;
        }
        keyValueStore.SetBigPictureMode(value);
    }

    partial void OnIsDiscordEnabledChanged(bool value)
    {
        keyValueStore.SetIsDiscordEnabled(value);
    }

    partial void OnLaunchDirectlyChanged(bool value)
    {
        NitroxUser.PreferDirectLaunch = value;
        if (SelectedGame == null || string.IsNullOrWhiteSpace(SelectedGame.PathToGame))
        {
            return;
        }

        NitroxUser.SetGamePathAndPlatform(SelectedGame.PathToGame, null, value);
        SelectedGame = new() { PathToGame = NitroxUser.GamePath, Platform = NitroxUser.GamePlatform?.Platform ?? Platform.NONE };
    }
}
