using NitroxClient.GameLogic;
using Nitrox.Model.Subnautica.Packets;
using UnityEngine;

namespace NitroxClient.MonoBehaviours.Vehicles;

/// <summary>
///     Movement replicator for vehicles Nitrox has no dedicated replicator for (e.g. vehicles added by mods such as
///     VehicleFramework). It only replicates the transform; vehicle-specific effects (sounds, animations) are not synced.
/// </summary>
/// <remarks>
///     The rigidbody is made kinematic while replicating so the vehicle's own physics (gravity, drag, WorldForces) can't fight
///     the interpolated transform the base class writes every frame.
/// </remarks>
public sealed class GenericVehicleMovementReplicator : VehicleMovementReplicator
{
    private bool madeKinematic;

    public new void Start()
    {
        base.Start();

        // base.Start() destroys this component (and leaves objectId unset) when the object has no NitroxId.
        if (objectId == null || !TryGetComponent(out Rigidbody body))
        {
            return;
        }

        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.isKinematic = true;
        madeKinematic = true;
    }

    public new void OnDestroy()
    {
        // Only undo what this component changed, and leave docked vehicles to the game's own docking logic.
        if (madeKinematic && TryGetComponent(out Rigidbody body) && !(TryGetComponent(out Vehicle vehicle) && vehicle.docked))
        {
            body.isKinematic = false;
        }

        base.OnDestroy();
    }

    public override void Enter(RemotePlayer remotePlayer)
    {
    }

    public override void Exit()
    {
    }

    public override void ApplyNewMovementData(MovementData newMovementData)
    {
    }
}
