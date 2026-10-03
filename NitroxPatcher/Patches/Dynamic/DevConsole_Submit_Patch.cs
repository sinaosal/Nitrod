using System;
using System.Globalization;
using System.Reflection;
using Nitrox.Model.DataStructures.GameLogic;
using NitroxClient.Communication.Abstract;
using NitroxClient.GameLogic;
using Nitrox.Model.Subnautica.Packets;

namespace NitroxPatcher.Patches.Dynamic;

/// <summary>
/// Disables the DevConsole's submit functionality for players with insufficient permissions.
/// TODO: Implement https://github.com/SubnauticaNitrox/Nitrox/issues/1689
/// </summary>
public sealed partial class DevConsole_Submit_Patch : NitroxPatch, IDynamicPatch
{
    internal static readonly MethodInfo TARGET_METHOD = Reflect.Method((DevConsole t) => t.Submit(default));

    public static bool Prefix(ref string value)
    {
        Log.Info($"Used cheat command : '{value}'");
        Resolve<IPacketSender>().Send(new CheatCommand(value));

        // Allow submit if player has sufficient permissions
        if (Resolve<LocalPlayer>().Permissions >= Perms.MODERATOR)
        {
            string[] arguments = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (arguments.Length > 0 && arguments[0].Equals("chaos", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Length == 1)
                {
                    PropulsionCannon_GrabObject_Patch.ChaosModeEnabled = !PropulsionCannon_GrabObject_Patch.ChaosModeEnabled;
                }
                else if (arguments.Length == 2 && arguments[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    PropulsionCannon_GrabObject_Patch.ChaosModeEnabled = true;
                }
                else if (arguments.Length == 2 && arguments[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    PropulsionCannon_GrabObject_Patch.ChaosModeEnabled = false;
                }
                else
                {
                    Log.InGame("Usage: chaos [on|off]");
                    return false;
                }

                Log.InGame($"Chaos mode {(PropulsionCannon_GrabObject_Patch.ChaosModeEnabled ? "enabled" : "disabled")}");
                return false;
            }

            if (arguments.Length == 4 && arguments[0].Equals("spawn", StringComparison.OrdinalIgnoreCase) &&
                arguments[2].Equals("size", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(arguments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float size) &&
                !float.IsNaN(size) && !float.IsInfinity(size) && size is > 0 and <= 100)
            {
                SpawnConsoleCommand_SpawnAsync_Patch.SetNextSpawnScale(arguments[1].Trim('"', '\''), size);
                value = $"spawn {arguments[1]}";
            }

            return true;
        }

        Log.InGame(Language.main.Get("Nitrox_MissingPermission").Replace("{PERMISSION}", nameof(Perms.MODERATOR)));
        return false;
    }
}
