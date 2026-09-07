using System.Collections.Generic;
using Unity.Entities;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Singleton holding this physics step's collision and trigger events. Rebuilt by
    /// <see cref="PhysicsEventCollectorSystem"/> every step; read by gameplay after
    /// <c>PhysicsSystemGroup</c> and before the next step.
    /// </summary>
    /// <remarks>
    /// Managed lists rather than native containers because the events already crossed the managed
    /// boundary in the collector (the tracker is managed) and the consumers of "who hit whom" are
    /// gameplay code on the main thread. A Bursted consumer should read Unity.Physics' own event
    /// streams directly; this buffer is the phase-resolved, pair-normalised view.
    /// </remarks>
    public sealed class PhysicsEventBuffer : IComponentData
    {
        public readonly List<EntityCollision> Collisions = new List<EntityCollision>();
        public readonly List<EntityTriggerEvent> Triggers = new List<EntityTriggerEvent>();

        /// <summary>Physics steps the collector has processed since install.</summary>
        public int Step;
    }
}
