using System.Reflection;
using NitroxClient.GameLogic;
using Nitrox.Model.DataStructures;

namespace NitroxPatcher.Patches.Dynamic;

public sealed partial class Vehicle_OnPilotModeBegin_Patch : NitroxPatch, IDynamicPatch
{
    private static readonly MethodInfo TARGET_METHOD = Reflect.Method((Vehicle t) => t.OnPilotModeBegin());

    public static void Prefix(Vehicle __instance)
    {
        Resolve<Vehicles>().BroadcastOnPilotModeChanged(__instance.gameObject, true);

        // Vanilla vehicles already acquire the exclusive lock in Vehicle_OnHandClick_Patch before entering.
        // Modded vehicles (e.g. VehicleFramework subs) can enter piloting mode through their own code paths, so make sure the
        // local player owns the simulation, without re-requesting (and spamming the server) when the lock is already held.
        if (__instance.TryGetIdOrWarn(out NitroxId id) && !Resolve<SimulationOwnership>().HasExclusiveLock(id))
        {
            Resolve<SimulationOwnership>().RequestSimulationLock(id, SimulationLockType.EXCLUSIVE);
        }
    }
}
