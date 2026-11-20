using System.Linq.Expressions;

namespace Nitrox.Test.Client.ModApi;

/// <summary>
///     Reference implementation of the soft-dependency bridge documented in docs/NITROD-MOD-API.md. BepInEx mods copy this
///     class (it has no dependency on Nitrod) and talk to Nitrod through it. Kept in the test project so it is compiled and
///     tested against the real API.
/// </summary>
public static class NitrodBridge
{
    private static Type api;
    private static Type targetEnum;

    /// <summary>
    ///     True when Nitrod is loaded in this game. Nitrod loads after BepInEx plugins' Awake, so check this lazily
    ///     (e.g. when a world loads), not once at startup.
    /// </summary>
    public static bool IsAvailable => FindApi() != null;

    /// <summary>True when connected to a Nitrod session and messages can be sent.</summary>
    public static bool IsInSession => FindApi() is { } t && (bool)t.GetProperty("IsInSession")!.GetValue(null)!;

    /// <summary>Sends <paramref name="payload" /> to the same mod on all other players' games.</summary>
    public static bool SendToOthers(string modId, string channel, byte[] payload)
    {
        if (FindApi() is not { } t)
        {
            return false;
        }
        object others = Enum.ToObject(targetEnum!, 0); // ModMessageTarget.OTHERS
        return (bool)t.GetMethod("Send")!.Invoke(null, [modId, channel, payload, others, (ushort)0])!;
    }

    /// <summary>Handles messages of this mod on <paramref name="channel" />. The handler gets (sender name, sender id, payload).</summary>
    public static bool RegisterHandler(string modId, string channel, Action<string, ushort, byte[]> handler)
    {
        if (FindApi() is not { } t)
        {
            return false;
        }
        Type infoType = t.Assembly.GetType("NitroxClient.ModApi.ModMessageInfo")!;
        ParameterExpression info = Expression.Parameter(infoType, "info");
        Expression call = Expression.Invoke(Expression.Constant(handler),
                                            Expression.Property(info, "SenderName"),
                                            Expression.Property(info, "SenderPlayerId"),
                                            Expression.Property(info, "Payload"));
        Delegate typedHandler = Expression.Lambda(typeof(Action<>).MakeGenericType(infoType), call, info).Compile();
        t.GetMethod("RegisterHandler")!.Invoke(null, [modId, channel, typedHandler]);
        return true;
    }

    private static Type FindApi()
    {
        if (api != null)
        {
            return api;
        }
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            switch (assembly.GetName().Name)
            {
                case "NitroxClient":
                    api ??= assembly.GetType("NitroxClient.ModApi.NitrodModApi");
                    break;
                case "Nitrox.Model":
                    targetEnum ??= assembly.GetType("Nitrox.Model.Packets.ModMessageTarget");
                    break;
            }
        }
        return api != null && targetEnum != null ? api : null;
    }
}
