using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nitrox.Launcher.Models;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Services;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Launcher.ViewModels.Abstract;
using Nitrox.Model.Core;
using Nitrox.Model.Helper;
using Nitrox.Model.Constants;
using Nitrox.Model.Logger;
using Nitrox.Model.Serialization;

namespace Nitrox.Launcher.ViewModels;

internal partial class ServersViewModel : RoutableViewModelBase
{
    private readonly IKeyValueStore keyValueStore;
    private readonly DialogService dialogService;
    private readonly ServerService serverService;
    private readonly ManageServerViewModel manageServerViewModel;
    [ObservableProperty]
    public partial AvaloniaList<ServerEntry>? Servers { get; set; }
    public ServersViewModel(IKeyValueStore keyValueStore, DialogService dialogService, ServerService serverService, ManageServerViewModel manageServerViewModel)
    {
        this.keyValueStore = keyValueStore;
        this.dialogService = dialogService;
        this.serverService = serverService;
        this.manageServerViewModel = manageServerViewModel;

        this.RegisterMessageListener<ServerStatusMessage, ServersViewModel>((message, model) =>
        {
            ServerEntry entry = model.Servers?.FirstOrDefault(s => s.LastProcessId == message.ProcessId);
            if (entry == null)
            {
                return;
            }
            entry.PlayerCount = message.PlayerCount;
            entry.IsOnline = message.IsOnline;
        });

        serverService.PropertyChanged += ServerServiceOnPropertyChanged;
    }

    private void ServerServiceOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(serverService.Servers))
        {
            Servers = [..serverService.Servers];
        }
    }

    internal override async Task ViewContentLoadAsync(CancellationToken cancellationToken = default)
    {
        Servers = [..await serverService.GetServersAsync()];
        LoadSavedServers();
    }

    // Saved servers: the same list the in-game Multiplayer menu shows (servers file in the Nitrod config folder).
    [ObservableProperty]
    public partial AvaloniaList<SavedServerItem> SavedServers { get; set; } = [];

    [ObservableProperty]
    public partial string NewServerName { get; set; } = "";

    [ObservableProperty]
    public partial string NewServerAddress { get; set; } = "";

    [ObservableProperty]
    public partial string NewServerPort { get; set; } = SubnauticaServerConstants.DEFAULT_PORT.ToString();

    private void LoadSavedServers()
    {
        try
        {
            SavedServers = new AvaloniaList<SavedServerItem>(ServerList.From().Entries.Select((e, i) => new SavedServerItem(i, e.Name, e.Address, e.Port)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Unable to read saved servers");
        }
    }

    [RelayCommand]
    private void AddSavedServer()
    {
        string name = NewServerName.Trim();
        string address = NewServerAddress.Trim();
        if (name.Length == 0 || address.Length == 0 || address.Contains('|') || name.Contains('|'))
        {
            LauncherNotifier.Error("Enter a name and an IP address or host name.");
            return;
        }
        if (!int.TryParse(NewServerPort, out int port) || port is < 1 or > ushort.MaxValue)
        {
            LauncherNotifier.Error("The port must be a number between 1 and 65535.");
            return;
        }
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown)
        {
            LauncherNotifier.Error($"'{address}' is not a valid IP address or host name.");
            return;
        }
        ServerList list = ServerList.From();
        list.Add(new ServerList.Entry(name, address, port));
        list.Save();
        ServerList.Refresh();
        NewServerName = "";
        NewServerAddress = "";
        NewServerPort = SubnauticaServerConstants.DEFAULT_PORT.ToString();
        LoadSavedServers();
        LauncherNotifier.Success($"Saved {name}. It appears in the in-game Multiplayer menu.");
    }

    [RelayCommand]
    private void RemoveSavedServer(SavedServerItem? item)
    {
        if (item == null)
        {
            return;
        }
        ServerList list = ServerList.From();
        int index = list.Entries.ToList().FindIndex(e => e.Name == item.Name && e.Address == item.Address && e.Port == item.Port);
        if (index >= 0)
        {
            list.RemoveAt(index);
            list.Save();
            ServerList.Refresh();
        }
        LoadSavedServers();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task CreateServerAsync()
    {
        CreateServerViewModel? result = await dialogService.ShowAsync<CreateServerViewModel>();
        if (!result)
        {
            return;
        }

        try
        {
            ServerEntry serverEntry = await Task.Run(() => ServerEntry.FromDirectoryAsync(Path.Join(keyValueStore.GetSavesPath(), result!.Name)));
            if (serverEntry == null)
            {
                throw new Exception("Failed to create save file");
            }
            // Don't add to servers list manually here, it will be added by file system watcher. Otherwise: possible duplicate entries by race-condition.
        }
        catch (Exception ex)
        {
            LauncherNotifier.Error($"Server create failed: {ex.Message}");
            Log.Error(ex);
        }
    }

    [RelayCommand]
    public async Task<bool> StartServerAsync(ServerEntry server) => await serverService.StartServerAsync(server);

    [RelayCommand]
    public async Task ManageServer(ServerEntry server)
    {
        if (server.IsOnline && server.IsEmbedded)
        {
            ChangeView(new EmbeddedServerViewModel(server));
            return;
        }
        if (server.Version != NitroxEnvironment.Version && !await serverService.ConfirmServerVersionAsync(server))
        {
            return;
        }

        await manageServerViewModel.RefreshAndLoadFromAsync(server);
        ChangeView(manageServerViewModel);
    }
}

internal sealed record SavedServerItem(int Index, string Name, string Address, int Port)
{
    public string Endpoint => $"{Address}:{Port}";
}
