using System;
using Nitrox.Model.ModSync;

namespace Nitrox.Model.Packets;

/// <summary>Asks the server for its mod manifest. Allowed before joining (anonymous), so players can check compatibility first.</summary>
[Serializable]
public sealed class ModManifestRequest : Packet;

[Serializable]
public sealed class ModManifestResponse : Packet
{
    /// <summary>False if the host disabled mod sharing. <see cref="Manifest" /> is then empty.</summary>
    public bool SyncEnabled { get; }

    /// <summary>True if players may download the host's mod files from the server.</summary>
    public bool DownloadsAllowed { get; }

    /// <summary>True if players are expected to have the same required mods to join.</summary>
    public bool RequireMatchingMods { get; }

    /// <summary>Name of the world the server is hosting, shown in consent dialogs.</summary>
    public string ServerName { get; }

    public ModManifest Manifest { get; }

    public ModManifestResponse(bool syncEnabled, bool downloadsAllowed, bool requireMatchingMods, string serverName, ModManifest manifest)
    {
        SyncEnabled = syncEnabled;
        DownloadsAllowed = downloadsAllowed;
        RequireMatchingMods = requireMatchingMods;
        ServerName = serverName ?? "";
        Manifest = manifest ?? ModManifest.Empty();
    }
}

/// <summary>Requests part of a host mod file, identified by its content hash (never by path) so only published files can be read.</summary>
[Serializable]
public sealed class ModFileChunkRequest : Packet
{
    public string Sha256 { get; }
    public long Offset { get; }
    public int Length { get; }

    public ModFileChunkRequest(string sha256, long offset, int length)
    {
        Sha256 = sha256;
        Offset = offset;
        Length = length;
        UdpChannel = GetChannel(offset);
    }

    private const int MOD_SYNC_CHANNEL_COUNT = UdpChannelId.MOD_SYNC_3 - UdpChannelId.MOD_SYNC_0 + 1;

    /// <summary>Spreads consecutive chunks of a file over the mod sync channels.</summary>
    internal static UdpChannelId GetChannel(long offset) => (UdpChannelId)((int)UdpChannelId.MOD_SYNC_0 + (int)(offset / ModDownloader.DEFAULT_CHUNK_SIZE % MOD_SYNC_CHANNEL_COUNT));
}

[Serializable]
public sealed class ModFileChunkResponse : Packet
{
    public string Sha256 { get; }
    public long Offset { get; }
    public long TotalSize { get; }
    public byte[] Data { get; }

    /// <summary>Empty on success, otherwise the reason the request was refused.</summary>
    public string Error { get; }

    public ModFileChunkResponse(string sha256, long offset, long totalSize, byte[] data, string error)
    {
        Sha256 = sha256;
        Offset = offset;
        TotalSize = totalSize;
        Data = data ?? [];
        Error = error ?? "";
        UdpChannel = ModFileChunkRequest.GetChannel(offset);
    }

    public override string ToString() => $"[{nameof(ModFileChunkResponse)}: {Sha256} @{Offset} ({Data.Length}/{TotalSize} bytes){(Error.Length > 0 ? $" Error: {Error}" : "")}]";
}
