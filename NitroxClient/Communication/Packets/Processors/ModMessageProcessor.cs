using Nitrox.Model.Packets;
using NitroxClient.Communication.Packets.Processors.Core;
using NitroxClient.ModApi;

namespace NitroxClient.Communication.Packets.Processors;

internal sealed class ModMessageProcessor : IClientPacketProcessor<ModMessage>
{
    public Task Process(ClientProcessorContext context, ModMessage packet)
    {
        NitrodModApi.Dispatch(packet);
        return Task.CompletedTask;
    }
}
