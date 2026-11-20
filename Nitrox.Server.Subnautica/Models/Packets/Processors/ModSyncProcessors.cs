using System.IO;
using Nitrox.Model.ModSync;
using Nitrox.Server.Subnautica.Models.Packets.Core;
using Nitrox.Server.Subnautica.Services;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

/// <summary>Answers mod manifest requests. Anonymous so players can check compatibility before they reserve a slot.</summary>
internal sealed class ModManifestRequestProcessor(ModSyncService modSyncService, IOptions<SubnauticaServerOptions> serverOptions, IOptions<ServerStartOptions> startOptions, ILogger<ModManifestRequestProcessor> logger)
    : IAnonPacketProcessor<ModManifestRequest>
{
    private readonly ModSyncService modSyncService = modSyncService;
    private readonly IOptions<SubnauticaServerOptions> serverOptions = serverOptions;
    private readonly IOptions<ServerStartOptions> startOptions = startOptions;
    private readonly ILogger<ModManifestRequestProcessor> logger = logger;

    public async Task Process(AnonProcessorContext context, ModManifestRequest packet)
    {
        SubnauticaServerOptions options = serverOptions.Value;
        ModManifest manifest = options.ModSyncEnabled ? await modSyncService.GetManifestAsync() : ModManifest.Empty();
        logger.ZLogDebug($"Sending mod manifest ({manifest.Packages.Count} package(s)) to session #{context.Sender.SessionId}");
        await context.ReplyAsync(new ModManifestResponse(options.ModSyncEnabled, options.ModSyncEnabled && options.ModSyncAllowDownloads, options.ModSyncRequireMatchingMods,
                                                         startOptions.Value.SaveName, manifest));
    }
}

/// <summary>
///     Serves parts of published mod files. Files are looked up by content hash, so a client can only read files the host
///     published in its manifest; paths sent by clients are never used.
/// </summary>
internal sealed class ModFileChunkRequestProcessor(ModSyncService modSyncService, IOptions<SubnauticaServerOptions> serverOptions, ILogger<ModFileChunkRequestProcessor> logger)
    : IAnonPacketProcessor<ModFileChunkRequest>
{
    private readonly ModSyncService modSyncService = modSyncService;
    private readonly IOptions<SubnauticaServerOptions> serverOptions = serverOptions;
    private readonly ILogger<ModFileChunkRequestProcessor> logger = logger;

    public async Task Process(AnonProcessorContext context, ModFileChunkRequest packet)
    {
        SubnauticaServerOptions options = serverOptions.Value;
        if (!options.ModSyncEnabled || !options.ModSyncAllowDownloads)
        {
            await ReplyErrorAsync("The host does not allow mod downloads from this server.");
            return;
        }
        if (!ModPathSafety.IsValidSha256(packet.Sha256) || packet.Offset < 0 || packet.Length <= 0 || packet.Length > ModDownloader.MAX_CHUNK_SIZE)
        {
            await ReplyErrorAsync("Invalid request.");
            return;
        }

        ModSyncService.PublishedFile? file = await modSyncService.FindFileAsync(packet.Sha256);
        if (file == null)
        {
            await ReplyErrorAsync("This file is not published by the host.");
            return;
        }
        if (packet.Offset >= file.Size)
        {
            await ReplyErrorAsync("Requested range is outside of the file.");
            return;
        }

        int count = (int)Math.Min(packet.Length, file.Size - packet.Offset);
        if (!await modSyncService.WaitForUploadBudgetAsync(context.Sender.SessionId, count))
        {
            await ReplyErrorAsync("Too many requests. Slow down and retry.");
            return;
        }

        byte[] data = new byte[count];
        try
        {
            await using FileStream stream = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (stream.Length != file.Size)
            {
                modSyncService.RequestRescan();
                await ReplyErrorAsync("The file changed on the host. Reconnect to get the current mod list.");
                return;
            }
            stream.Position = packet.Offset;
            await stream.ReadExactlyAsync(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning($"Mod sync: unable to read '{file.FullPath}': {ex.Message}");
            modSyncService.RequestRescan();
            await ReplyErrorAsync("The host could not read this file.");
            return;
        }

        await context.ReplyAsync(new ModFileChunkResponse(packet.Sha256, packet.Offset, file.Size, data, ""));

        Task ReplyErrorAsync(string error) => context.ReplyAsync(new ModFileChunkResponse(packet.Sha256, packet.Offset, 0, [], error));
    }
}
