using System;
using System.Collections.Generic;
using Nitrox.Model.Core;
using Nitrox.Model.Packets;
using NitroxClient.Communication.Abstract;
using NitroxClient.GameLogic;
using NitroxClient.MonoBehaviours;

namespace NitroxClient.ModApi;

// DIMA001: this is a static API called by third-party mods through reflection; there is no constructor to inject services into,
// and services must be resolved from the current multiplayer session's lifetime scope at call time.
#pragma warning disable DIMA001

/// <summary>A message received from the same mod on another player's game.</summary>
public sealed class ModMessageInfo
{
    public string ModId { get; }
    public string Channel { get; }
    public byte[] Payload { get; }

    /// <summary>Session id of the sending player (stable while they are connected).</summary>
    public ushort SenderPlayerId { get; }

    /// <summary>Name of the sending player, or empty if they are not (or no longer) known.</summary>
    public string SenderName { get; }

    internal ModMessageInfo(ModMessage message, string senderName)
    {
        ModId = message.ModId;
        Channel = message.Channel;
        Payload = message.Payload;
        SenderPlayerId = message.SenderPlayerId;
        SenderName = senderName ?? "";
    }
}

/// <summary>
///     Public entry point for BepInEx mods that want to work in Nitrod multiplayer.
/// </summary>
/// <remarks>
///     <para>
///         Nitrod is not a BepInEx plugin, so mods should not reference NitroxClient.dll directly. Look the type up at runtime
///         (<c>Type.GetType("NitroxClient.ModApi.NitrodModApi, NitroxClient")</c>) and call it through reflection or delegates;
///         see docs/NITROD-MOD-API.md for a ready-made bridge class. When the type isn't found, the game runs without Nitrod.
///     </para>
///     <para>
///         Messages are relayed by the server and delivered on Unity's main thread, in order per sender. Payloads are limited to
///         <see cref="ModMessage.MAX_PAYLOAD_BYTES" /> bytes; the server also limits how many messages a player can send per
///         second. Nitrod never interprets payloads: validate everything you receive, it comes from another player's game.
///     </para>
/// </remarks>
public static class NitrodModApi
{
    /// <summary>Incremented on breaking changes to this API.</summary>
    public const int API_VERSION = 1;

    private static readonly Dictionary<(string ModId, string Channel), Action<ModMessageInfo>> handlers = [];
    private static readonly object handlersLock = new();

    /// <summary>Raised when a multiplayer world starts loading.</summary>
    public static event Action MultiplayerStarting;

    /// <summary>Raised after leaving a multiplayer session (back in the main menu).</summary>
    public static event Action MultiplayerEnded;

    static NitrodModApi()
    {
        Multiplayer.OnBeforeMultiplayerStart += () => Raise(MultiplayerStarting, nameof(MultiplayerStarting));
        Multiplayer.OnAfterMultiplayerEnd += () => Raise(MultiplayerEnded, nameof(MultiplayerEnded));
    }

    /// <summary>True while connected to a server (including while the world is loading).</summary>
    public static bool IsMultiplayer => Multiplayer.Active;

    /// <summary>True once the local player has joined the session and messages can be sent.</summary>
    public static bool IsInSession => Multiplayer.Joined;

    public static string LocalPlayerName => IsMultiplayer ? NitroxServiceLocator.LocateService<LocalPlayer>().PlayerName : "";

    public static ushort LocalPlayerId => IsInSession && NitroxServiceLocator.LocateService<LocalPlayer>().SessionId is { } id ? id : (ushort)0;

    /// <summary>Names of the other players currently in the session, by player id.</summary>
    public static IReadOnlyDictionary<ushort, string> GetRemotePlayers()
    {
        Dictionary<ushort, string> players = [];
        if (!IsInSession)
        {
            return players;
        }
        foreach (RemotePlayer player in NitroxServiceLocator.LocateService<PlayerManager>().GetAll())
        {
            players[player.SessionId] = player.PlayerName;
        }
        return players;
    }

    /// <summary>Registers (or replaces) the handler for messages of <paramref name="modId" /> on <paramref name="channel" />.</summary>
    public static void RegisterHandler(string modId, string channel, Action<ModMessageInfo> handler)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(channel) || handler == null)
        {
            throw new ArgumentException("modId, channel and handler are required.");
        }
        lock (handlersLock)
        {
            handlers[(modId, channel)] = handler;
        }
    }

    public static bool UnregisterHandler(string modId, string channel)
    {
        lock (handlersLock)
        {
            return handlers.Remove((modId, channel));
        }
    }

    /// <summary>Sends a message to the same mod on other players' games. Returns false when not in a session or the message is invalid.</summary>
    public static bool Send(string modId, string channel, byte[] payload, ModMessageTarget target = ModMessageTarget.OTHERS, ushort targetPlayerId = 0)
    {
        ModMessage message = new(modId, channel, payload, target, targetPlayerId, 0);
        if (!message.IsValid(out string reason))
        {
            Log.Warn($"Mod '{modId}' tried to send an invalid message: {reason}");
            return false;
        }
        return IsInSession && NitroxServiceLocator.LocateService<IPacketSender>().Send(message);
    }

    internal static void Dispatch(ModMessage message)
    {
        Action<ModMessageInfo> handler;
        lock (handlersLock)
        {
            if (!handlers.TryGetValue((message.ModId, message.Channel), out handler))
            {
                return; // The mod isn't installed here or doesn't listen on this channel.
            }
        }
        try
        {
            handler(new ModMessageInfo(message, FindPlayerName(message.SenderPlayerId)));
        }
        catch (Exception ex)
        {
            // A broken mod handler must not break Nitrod's packet processing.
            Log.Error(ex, $"Mod '{message.ModId}' failed to handle a message on channel '{message.Channel}'");
        }
    }

    private static string FindPlayerName(ushort playerId)
    {
        if (!IsMultiplayer)
        {
            return "";
        }
        try
        {
            return NitroxServiceLocator.LocateService<PlayerManager>().TryFind(playerId, out RemotePlayer sender) ? sender.PlayerName : "";
        }
        catch (Exception ex)
        {
            Log.Debug($"Unable to resolve mod message sender #{playerId}: {ex.Message}");
            return "";
        }
    }

    private static void Raise(Action action, string name)
    {
        if (action == null)
        {
            return;
        }
        foreach (Action subscriber in action.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"A mod's {name} handler failed");
            }
        }
    }
}
#pragma warning restore DIMA001
