# Making a BepInEx mod work in Nitrod multiplayer

Nitrod synchronizes vanilla Subnautica systems. If your mod has its own state (custom events, counters, machine settings, spawned objects) that every player must see, send it with **`NitrodModApi`**: small messages that the server relays to the same mod on other players' games.

## 1. Don't reference NitroxClient.dll

Nitrod is not a BepInEx plugin. It loads *after* your plugin's `Awake`, and players without Nitrod must still be able to run your mod. Copy this bridge into your mod. It only uses reflection and has no dependency on Nitrod. It is also compiled and tested in Nitrod's test suite (`Nitrox.Test/Client/ModApi/NitrodBridge.cs`).

```csharp
using System;
using System.Linq.Expressions;
using System.Reflection;

public static class NitrodBridge
{
    private static Type api;
    private static Type targetEnum;

    /// <summary>True when Nitrod is loaded. Check lazily (e.g. when a world loads), not in Awake.</summary>
    public static bool IsAvailable => FindApi() != null;

    public static bool IsInSession => FindApi() is { } t && (bool)t.GetProperty("IsInSession").GetValue(null);

    public static bool SendToOthers(string modId, string channel, byte[] payload)
    {
        if (FindApi() is not { } t) return false;
        object others = Enum.ToObject(targetEnum, 0); // ModMessageTarget.OTHERS
        return (bool)t.GetMethod("Send").Invoke(null, new object[] { modId, channel, payload, others, (ushort)0 });
    }

    /// <summary>handler(senderName, senderId, payload) runs on Unity's main thread.</summary>
    public static bool RegisterHandler(string modId, string channel, Action<string, ushort, byte[]> handler)
    {
        if (FindApi() is not { } t) return false;
        Type infoType = t.Assembly.GetType("NitroxClient.ModApi.ModMessageInfo");
        ParameterExpression info = Expression.Parameter(infoType, "info");
        Expression call = Expression.Invoke(Expression.Constant(handler),
                                            Expression.Property(info, "SenderName"),
                                            Expression.Property(info, "SenderPlayerId"),
                                            Expression.Property(info, "Payload"));
        Delegate typed = Expression.Lambda(typeof(Action<>).MakeGenericType(infoType), call, info).Compile();
        t.GetMethod("RegisterHandler").Invoke(null, new object[] { modId, channel, typed });
        return true;
    }

    private static Type FindApi()
    {
        if (api != null) return api;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            switch (assembly.GetName().Name)
            {
                case "NitroxClient": api ??= assembly.GetType("NitroxClient.ModApi.NitrodModApi"); break;
                case "Nitrox.Model": targetEnum ??= assembly.GetType("Nitrox.Model.Packets.ModMessageTarget"); break;
            }
        }
        return api != null && targetEnum != null ? api : null;
    }
}
```

## 2. Send and receive

```csharp
// When the world has loaded (e.g. in a Player.Start postfix):
if (NitrodBridge.IsAvailable)
{
    NitrodBridge.RegisterHandler(MyPlugin.GUID, "infection-level", (sender, senderId, payload) =>
    {
        float level = BitConverter.ToSingle(payload, 0); // Validate: this comes from another player's game.
        if (!float.IsNaN(level) && level >= 0 && level <= 1)
        {
            InfectionManager.SetLevelFromNetwork(level);
        }
    });
}

// When your state changes locally:
if (NitrodBridge.IsInSession)
{
    NitrodBridge.SendToOthers(MyPlugin.GUID, "infection-level", BitConverter.GetBytes(level));
}
```

## API reference (`NitroxClient.ModApi.NitrodModApi`)

| Member | Description |
|---|---|
| `API_VERSION` | `1`; incremented on breaking changes |
| `IsMultiplayer` | Connected to a server (including while loading) |
| `IsInSession` | Joined; messages can be sent |
| `LocalPlayerName`, `LocalPlayerId` | The local player |
| `GetRemotePlayers()` | Other players by id |
| `RegisterHandler(modId, channel, Action<ModMessageInfo>)` / `UnregisterHandler` | One handler per mod and channel |
| `Send(modId, channel, payload, target = OTHERS, targetPlayerId = 0)` | `OTHERS`, `EVERYONE` (including you, in the server's order) or `PLAYER` |
| `MultiplayerStarting`, `MultiplayerEnded` | Session lifecycle events |

Limits: payloads up to 64 KB, mod id and channel up to 128 characters, and the server drops messages from a player who sends more than 120 messages or 1 MB per second. The server sets `SenderPlayerId`, so senders can't impersonate each other. Exceptions in your handler are logged and don't affect Nitrod.

## Patterns

- **Authority:** pick one player to own a piece of state (e.g. the one who triggered it) and send `EVERYONE` so all games, including the sender's, apply changes in the same order.
- **Late joiners:** messages are not stored. When `MultiplayerStarting` fires, send a request message and let an existing player answer with the current state using `PLAYER` and the requester's id.
- **Randomness:** generate random results on one player and send the result, not the seed.
- **Spawned objects:** prefer spawning through vanilla paths Nitrox already syncs (e.g. `CraftData.InstantiateFromPrefab` on the simulation owner). Objects you create yourself are not shared.

## Checking your mod

After a multiplayer session, the launcher's **Diagnostics › Patch overlaps** lists game methods your mod and Nitrod both patch. A prefix that returns `false`, or a transpiler, on one of those methods is the most likely reason something stops syncing.
