using System;

namespace Nitrox.Model.Packets;

public enum ModMessageTarget
{
    /// <summary>Every other player in the session.</summary>
    OTHERS,

    /// <summary>Every player including the sender (useful when all clients must apply changes in the server's order).</summary>
    EVERYONE,

    /// <summary>One player, identified by <see cref="ModMessage.TargetPlayerId" />.</summary>
    PLAYER
}

/// <summary>
///     Opaque message between instances of a BepInEx mod on different players' games, relayed by the server. Nitrod doesn't
///     interpret the payload; this is how mods that need their own multiplayer logic synchronize state. See NitrodModApi.
/// </summary>
[Serializable]
public sealed class ModMessage : Packet
{
    public const int MAX_PAYLOAD_BYTES = 64 * 1024;
    public const int MAX_ID_LENGTH = 128;

    /// <summary>The sending mod, normally its BepInEx plugin GUID. Messages are only delivered to handlers registered for it.</summary>
    public string ModId { get; }

    /// <summary>Mod-defined message type, e.g. "infection-level".</summary>
    public string Channel { get; }

    public byte[] Payload { get; }

    public ModMessageTarget Target { get; }

    /// <summary>Receiving player when <see cref="Target" /> is <see cref="ModMessageTarget.PLAYER" />.</summary>
    public ushort TargetPlayerId { get; }

    /// <summary>Set by the server to the real sender; whatever a client puts here is overwritten.</summary>
    public ushort SenderPlayerId { get; }

    public ModMessage(string modId, string channel, byte[] payload, ModMessageTarget target, ushort targetPlayerId, ushort senderPlayerId)
    {
        ModId = modId ?? "";
        Channel = channel ?? "";
        Payload = payload ?? [];
        Target = target;
        TargetPlayerId = targetPlayerId;
        SenderPlayerId = senderPlayerId;
    }

    public ModMessage WithSender(ushort senderPlayerId) => new(ModId, Channel, Payload, Target, TargetPlayerId, senderPlayerId);

    public bool IsValid(out string reason)
    {
        if (ModId.Length is 0 or > MAX_ID_LENGTH || Channel.Length is 0 or > MAX_ID_LENGTH)
        {
            reason = $"mod id and channel must be 1-{MAX_ID_LENGTH} characters";
            return false;
        }
        if (Payload.Length > MAX_PAYLOAD_BYTES)
        {
            reason = $"payload is {Payload.Length} bytes, the limit is {MAX_PAYLOAD_BYTES}";
            return false;
        }
        if (!Enum.IsDefined(typeof(ModMessageTarget), Target))
        {
            reason = "unknown target";
            return false;
        }
        reason = "";
        return true;
    }

    public override string ToString() => $"[{nameof(ModMessage)} {ModId}/{Channel} {Payload.Length} bytes -> {Target}{(Target == ModMessageTarget.PLAYER ? $" #{TargetPlayerId}" : "")} from #{SenderPlayerId}]";
}
