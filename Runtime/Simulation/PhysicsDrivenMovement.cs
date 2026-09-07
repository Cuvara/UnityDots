using Unity.Entities;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>
    /// Tag: this entity's <c>LocalTransform</c> is integrated by Unity.Physics, not by the package's
    /// own movement systems.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one-integrator rule.</b> <see cref="MoveBounceSystem"/> and <see cref="MoveTowardSystem"/>
    /// write <c>LocalTransform.Position</c> directly. <c>PhysicsMovementBridge</c> (optional
    /// physics assembly) copies <see cref="MoveData.Velocity"/> into <c>PhysicsVelocity</c> and lets
    /// Unity.Physics integrate. An entity reached by both moves twice per frame — once at render
    /// rate, once at the fixed step — and the symptom is a body that drifts through walls at roughly
    /// double speed with nothing logging. So the tag decides: the direct movers exclude it, the bridge
    /// requires it, and <c>PhysicsBodyFactory</c> adds it to every dynamic and kinematic body.
    /// </para>
    /// <para>
    /// Lives in the core so the movement systems can name it without a reference to Unity.Physics;
    /// it carries no physics type and is harmless in a project without physics.
    /// </para>
    /// </remarks>
    public struct PhysicsDrivenMovement : IComponentData
    {
    }
}
