using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Services;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels.Abstract;
using Nitrox.Model.Logger;
using Nitrox.Model.ModSync;

namespace Nitrox.Launcher.ViewModels;

/// <summary>Installed BepInEx mods, their health, and mods synchronized from servers.</summary>
internal partial class ModsViewModel(DialogService dialogService, StorageService storageService, HttpFileService httpFileService) : RoutableViewModelBase
{
    private readonly DialogService dialogService = dialogService;
    private readonly StorageService storageService = storageService;
    private readonly BepInExInstaller bepInExInstaller = new(httpFileService);

    [ObservableProperty]
    public partial AvaloniaList<ModPackageItem> Packages { get; set; } = [];

    [ObservableProperty]
    public partial AvaloniaList<ModIssueItem> Issues { get; set; } = [];

    [ObservableProperty]
    public partial AvaloniaList<SyncRecordItem> SyncedInstalls { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    public partial string PendingSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGameMissing), nameof(ShowLoaderMissing))]
    public partial bool HasGame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoaderMissing))]
    public partial bool IsBepInExInstalled { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasPending => PendingSummary.Length > 0;
    public bool ShowGameMissing => !HasGame;
    public bool ShowLoaderMissing => HasGame && !IsBepInExInstalled;
    public bool HasIssues => Issues.Count > 0;
    public bool HasSyncedInstalls => SyncedInstalls.Count > 0;

    internal override Task ViewContentLoadAsync(CancellationToken cancellationToken = default) => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        string? gamePath = ModManagerService.CurrentGamePath;
        HasGame = gamePath != null;
        if (gamePath == null)
        {
            Summary = "";
            Packages = [];
            Issues = [];
            SyncedInstalls = [];
            PendingSummary = "";
            NotifyCollections();
            return;
        }

        IsBusy = true;
        try
        {
            IsBepInExInstalled = ModManagerService.IsBepInExInstalled(gamePath);
            ModManifest manifest = await ModManagerService.ScanAsync(gamePath);
            IReadOnlyList<ModIssue> issues = ModManagerService.CheckLocal(manifest);
            ModSyncLedger ledger = ModManagerService.LoadLedger(gamePath);
            Dictionary<string, string> syncedFrom = new(StringComparer.OrdinalIgnoreCase);
            foreach (ModSyncRecord record in ledger.Records.Where(r => !r.Uninstalled))
            {
                foreach (string packageId in record.PackageIds)
                {
                    syncedFrom[packageId] = record.ServerName;
                }
            }

            List<ModPackageItem> items = manifest.Packages.Select(p => ModPackageItem.From(p, issues, syncedFrom.GetValueOrDefault(p.Id, ""))).ToList();
            items.AddRange(ModManagerService.GetDisabledPackageIds(gamePath).Select(ModPackageItem.Disabled));
            PendingModSync? pending = ModManagerService.LoadPending(gamePath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Packages = new AvaloniaList<ModPackageItem>(items);
                Issues = new AvaloniaList<ModIssueItem>(issues.Select(ModIssueItem.From));
                SyncedInstalls = new AvaloniaList<SyncRecordItem>(Enumerable.Reverse(ledger.Records).Select(SyncRecordItem.From));
                PendingSummary = pending == null ? "" : $"{pending.Files.Count} file(s), {ModSyncLimits.FormatSize(pending.TotalSize)} from {pending.ServerName} ({pending.ServerAddress}), downloaded {pending.CreatedUtc.ToLocalTime():g}";
                Summary = $"{manifest.Packages.Count} enabled · {ModSyncLimits.FormatSize(manifest.TotalSize)} · BepInEx {(manifest.IsLoaderInstalled ? manifest.LoaderVersion : "not installed")}";
                NotifyCollections();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to list mods");
            LauncherNotifier.Error($"Failed to list mods: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(ModPackageItem? item)
    {
        if (item == null || ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        try
        {
            if (item.IsEnabled)
            {
                ModManagerService.DisablePackage(gamePath, item.Id);
            }
            else
            {
                ModManagerService.EnablePackage(gamePath, item.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            LauncherNotifier.Error(ex.Message);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UninstallSyncAsync(SyncRecordItem? item)
    {
        if (item == null || ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        DialogBoxViewModel? answer = await dialogService.ShowAsync<DialogBoxViewModel>(model =>
        {
            model.Title = $"Undo mods synchronized from {item.ServerName}?";
            model.Description = "Files Nitrod installed are removed and the files they replaced are restored from the backup. Files you changed afterwards are left alone.";
            model.ButtonOptions = ButtonOptions.YesNo;
        });
        if (answer?.SelectedOption != ButtonOptions.Yes)
        {
            return;
        }
        try
        {
            List<string> skipped = await ModManagerService.UninstallAsync(gamePath, item.Id);
            if (skipped.Count > 0)
            {
                LauncherNotifier.Warning($"Undone, except {skipped.Count} file(s) that changed after the install (left as they are).");
            }
            else
            {
                LauncherNotifier.Success($"Removed the mods synchronized from {item.ServerName}.");
            }
        }
        catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await dialogService.ShowErrorAsync(ex, "Undoing the synchronized mods failed");
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InstallPendingAsync()
    {
        if (ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        await ModManagerService.OfferPendingInstallAsync(gamePath, dialogService);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DiscardPendingAsync()
    {
        if (ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        ModManagerService.DiscardPending(gamePath);
        LauncherNotifier.Info("Discarded the downloaded mods.");
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InstallBepInExAsync()
    {
        if (ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            OpenUri("github.com/toebeann/BepInEx.Subnautica");
            return;
        }
        IsBusy = true;
        try
        {
            await bepInExInstaller.InstallAsync(gamePath);
            LauncherNotifier.Success("BepInEx is installed and will load when Subnautica starts.");
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(ex, "Failed to install BepInEx");
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InstallFromZipAsync()
    {
        if (ModManagerService.CurrentGamePath is not { } gamePath)
        {
            return;
        }
        if (!ModManagerService.IsBepInExInstalled(gamePath))
        {
            LauncherNotifier.Error("Install BepInEx before installing mods.");
            return;
        }
        string archivePath = (await storageService.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a BepInEx mod ZIP",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("BepInEx mod archive") { Patterns = ["*.zip"] }]
        })).FirstOrDefault()?.TryGetLocalPath() ?? "";
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }
        try
        {
            int installedFiles = await Task.Run(() => BepInExModInstaller.Install(archivePath, gamePath));
            LauncherNotifier.Success($"Installed {Path.GetFileName(archivePath)} ({installedFiles} files).");
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(ex, "Failed to install the mod");
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        if (ModManagerService.CurrentGamePath is { } gamePath)
        {
            string plugins = Path.Combine(gamePath, "BepInEx", "plugins");
            OpenPath(Directory.Exists(plugins) ? plugins : gamePath);
        }
    }

    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasSyncedInstalls));
    }
}
