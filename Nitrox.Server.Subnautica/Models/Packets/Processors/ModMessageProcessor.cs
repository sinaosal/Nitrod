using System.Collections.Concurrent;
using Nitrox.Model.Core;
using Nitrox.Server.Subnautica.Models.Packets.Core;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

/// <summary>
///     Relays <see cref="ModMessage" />s between players for mods that implement their own multiplayer logic. The server stamps
///     the real sender and enforces size and rate limits; it never interprets the payload.
/// </summary>
internal sealed class ModMessageProcessor(ILogger<ModMessageProcessor> logger) : IAuthPacketProcessor<ModMessage>
{
    private const int MAX_MESSAGES_PER_SECOND = 120;
    private const int MAX_BYTES_PER_SECOND = 1024 * 1024;

    private readonly ILogger<ModMessageProcessor> logger = logger;
    private readonly ConcurrentDictionary<SessionId, RateWindow> rates = new();

    public async Task Process(AuthProcessorContext context, ModMessage packet)
    {
        if (!packet.IsValid(out string reason))
        {
            logger.ZLogWarningOnce($"Dropped mod message from {context.Sender.Name}: {reason}");
            return;
        }
        if (!rates.GetOrAdd(context.Sender.SessionId, _ => new RateWindow()).TryConsume(packet.Payload.Length))
        {
            logger.ZLogWarningOnce($"Dropping mod messages from {context.Sender.Name}: more than {MAX_MESSAGES_PER_SECOND} messages or {MAX_BYTES_PER_SECOND / 1024} KB per second (mod '{packet.ModId}')");
            return;
        }

        ModMessage relayed = packet.WithSender(context.Sender.SessionId);
        switch (packet.Target)
        {
            case ModMessageTarget.OTHERS:
                await context.SendToOthersAsync(relayed);
                break;
            case ModMessageTarget.EVERYONE:
                await context.SendToAllAsync(relayed);
                break;
            case ModMessageTarget.PLAYER:
                await context.SendAsync(relayed, packet.TargetPlayerId);
                break;
        }
    }

    private sealed class RateWindow
    {
        private readonly Lock windowLock = new();
        private long windowStart = Environment.TickCount64;
        private int messages;
        private long bytes;

        public bool TryConsume(int payloadBytes)
        {
            lock (windowLock)
            {
                long now = Environment.TickCount64;
                if (now - windowStart >= 1000)
                {
                    windowStart = now;
                    messages = 0;
                    bytes = 0;
                }
                if (messages + 1 > MAX_MESSAGES_PER_SECOND || bytes + payloadBytes > MAX_BYTES_PER_SECOND)
                {
                    return false;
                }
                messages++;
                bytes += payloadBytes;
                return true;
            }
        }
    }
}
