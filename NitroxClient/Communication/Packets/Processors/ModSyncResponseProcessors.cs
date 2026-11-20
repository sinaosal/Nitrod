using Nitrox.Model.Packets;
using NitroxClient.Communication.Packets.Processors.Core;
using NitroxClient.ModSync;

namespace NitroxClient.Communication.Packets.Processors;

internal sealed class ModManifestResponseProcessor(ModSyncClient modSyncClient) : IClientPacketProcessor<ModManifestResponse>
{
    private readonly ModSyncClient modSyncClient = modSyncClient;

    public Task Process(ClientProcessorContext context, ModManifestResponse packet)
    {
        modSyncClient.OnManifestResponse(packet);
        return Task.CompletedTask;
    }
}

internal sealed class ModFileChunkResponseProcessor(ModSyncClient modSyncClient) : IClientPacketProcessor<ModFileChunkResponse>
{
    private readonly ModSyncClient modSyncClient = modSyncClient;

    public Task Process(ClientProcessorContext context, ModFileChunkResponse packet)
    {
        modSyncClient.OnChunkResponse(packet);
        return Task.CompletedTask;
    }
}
