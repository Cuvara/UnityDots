using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Turns the per-step stream of Unity.Physics collision and trigger events into
    /// enter/stay/exit events per entity pair. Pure managed state machine: no world, no jobs, so the
    /// semantics are tested directly and the collector system is a thin feeder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per step:</b> <see cref="BeginStep"/>, any number of <see cref="ReportCollision"/> /
    /// <see cref="ReportTrigger"/>, then <see cref="EndStep"/>. Reports within one step for the same
    /// pair are aggregated (see <see cref="EntityCollision"/>). At <see cref="EndStep"/>: a pair
    /// reported now and not last step is <see cref="PhysicsContactPhase.Enter"/>; reported both
    /// times, <see cref="PhysicsContactPhase.Stay"/>; last step only,
    /// <see cref="PhysicsContactPhase.Exit"/>. A destroyed entity simply stops being reported, so
    /// its pairs exit on the next step; <paramref name="exists"/> marks those exits with
    /// <c>AnyEntityDestroyed</c> so the consumer knows not to read the entity.
    /// </para>
    /// <para>
    /// <b>Output order is deterministic:</b> exits, then enters, then stays, each sorted by
    /// <see cref="PhysicsPairKey"/>. Exit-before-enter lets a consumer that keys state on the pair
    /// free the old slot before a same-index (different-version) pair claims a new one.
    /// </para>
    /// </remarks>
    public sealed class PhysicsContactTracker
    {
        private struct Accumulated
        {
            public float3 NormalSum;
            public float3 PositionSum;
            public float Impulse;
            public int Count;
        }

        private Dictionary<PhysicsPairKey, Accumulated> _collisionsNow = new Dictionary<PhysicsPairKey, Accumulated>();
        private Dictionary<PhysicsPairKey, Accumulated> _collisionsBefore = new Dictionary<PhysicsPairKey, Accumulated>();
        private HashSet<PhysicsPairKey> _triggersNow = new HashSet<PhysicsPairKey>();
        private HashSet<PhysicsPairKey> _triggersBefore = new HashSet<PhysicsPairKey>();
        private readonly List<PhysicsPairKey> _scratch = new List<PhysicsPairKey>();
        private bool _inStep;

        /// <summary>Collision pairs currently in contact (as of the last <see cref="EndStep"/>).</summary>
        public int ActiveCollisionPairs => _collisionsBefore.Count;

        /// <summary>Trigger pairs currently overlapping (as of the last <see cref="EndStep"/>).</summary>
        public int ActiveTriggerPairs => _triggersBefore.Count;

        /// <summary>Steps completed.</summary>
        public int StepCount { get; private set; }

        public void BeginStep()
        {
            if (_inStep) throw new InvalidOperationException("[Cuvara.DOTS] BeginStep called twice without EndStep.");
            _inStep = true;
            _collisionsNow.Clear();
            _triggersNow.Clear();
        }

        /// <summary>Reports one Unity.Physics collision event. <paramref name="normal"/> points from <paramref name="a"/> toward <paramref name="b"/>.</summary>
        public void ReportCollision(Entity a, Entity b, float3 normal, float3 position, float impulse)
        {
            if (!_inStep) throw new InvalidOperationException("[Cuvara.DOTS] ReportCollision outside BeginStep/EndStep.");
            if (a == b) return;

            var key = new PhysicsPairKey(a, b);
            if (key.Swapped) normal = -normal;

            _collisionsNow.TryGetValue(key, out var acc);
            acc.NormalSum += normal;
            acc.PositionSum += position;
            acc.Impulse += impulse;
            acc.Count++;
            _collisionsNow[key] = acc;
        }

        /// <summary>Reports one Unity.Physics trigger event.</summary>
        public void ReportTrigger(Entity a, Entity b)
        {
            if (!_inStep) throw new InvalidOperationException("[Cuvara.DOTS] ReportTrigger outside BeginStep/EndStep.");
            if (a == b) return;

            _triggersNow.Add(new PhysicsPairKey(a, b));
        }

        /// <summary>
        /// Resolves phases against the previous step and appends the events. The lists are not
        /// cleared here — the caller owns them.
        /// </summary>
        /// <param name="exists">
        /// Whether an entity still exists, for flagging exits. Null treats every entity as alive.
        /// </param>
        public void EndStep(List<EntityCollision> collisions, List<EntityTriggerEvent> triggers, Func<Entity, bool> exists = null)
        {
            if (!_inStep) throw new InvalidOperationException("[Cuvara.DOTS] EndStep without BeginStep.");
            _inStep = false;
            StepCount++;

            if (collisions != null)
            {
                // Exits: in before, not in now.
                _scratch.Clear();
                foreach (var pair in _collisionsBefore)
                {
                    if (!_collisionsNow.ContainsKey(pair.Key)) _scratch.Add(pair.Key);
                }

                _scratch.Sort();
                foreach (var key in _scratch)
                {
                    var last = _collisionsBefore[key];
                    collisions.Add(new EntityCollision(
                        key.A, key.B, PhysicsContactPhase.Exit,
                        float3.zero, last.Count > 0 ? last.PositionSum / last.Count : float3.zero, 0f, 0,
                        Destroyed(key, exists)));
                }

                // Enters, then stays.
                AppendCollisions(collisions, PhysicsContactPhase.Enter, present: false);
                AppendCollisions(collisions, PhysicsContactPhase.Stay, present: true);
            }

            if (triggers != null)
            {
                _scratch.Clear();
                foreach (var key in _triggersBefore)
                {
                    if (!_triggersNow.Contains(key)) _scratch.Add(key);
                }

                _scratch.Sort();
                foreach (var key in _scratch) triggers.Add(new EntityTriggerEvent(key.A, key.B, PhysicsContactPhase.Exit, Destroyed(key, exists)));

                AppendTriggers(triggers, PhysicsContactPhase.Enter, present: false);
                AppendTriggers(triggers, PhysicsContactPhase.Stay, present: true);
            }

            // Swap: now becomes before, the old before is reused as the next now.
            (_collisionsBefore, _collisionsNow) = (_collisionsNow, _collisionsBefore);
            (_triggersBefore, _triggersNow) = (_triggersNow, _triggersBefore);
        }

        /// <summary>
        /// Forgets every pair without emitting exits. For a world reset or reconnect where the
        /// consumer has already dropped its own per-pair state and an exit storm would be noise.
        /// </summary>
        public void Clear()
        {
            _collisionsNow.Clear();
            _collisionsBefore.Clear();
            _triggersNow.Clear();
            _triggersBefore.Clear();
            _inStep = false;
        }

        private void AppendCollisions(List<EntityCollision> output, PhysicsContactPhase phase, bool present)
        {
            _scratch.Clear();
            foreach (var pair in _collisionsNow)
            {
                if (_collisionsBefore.ContainsKey(pair.Key) == present) _scratch.Add(pair.Key);
            }

            _scratch.Sort();
            foreach (var key in _scratch)
            {
                var acc = _collisionsNow[key];
                var normal = math.lengthsq(acc.NormalSum) > 1e-12f ? math.normalize(acc.NormalSum) : float3.zero;
                output.Add(new EntityCollision(
                    key.A, key.B, phase, normal, acc.PositionSum / acc.Count, acc.Impulse, acc.Count, false));
            }
        }

        private void AppendTriggers(List<EntityTriggerEvent> output, PhysicsContactPhase phase, bool present)
        {
            _scratch.Clear();
            foreach (var key in _triggersNow)
            {
                if (_triggersBefore.Contains(key) == present) _scratch.Add(key);
            }

            _scratch.Sort();
            foreach (var key in _scratch) output.Add(new EntityTriggerEvent(key.A, key.B, phase, false));
        }

        private static bool Destroyed(PhysicsPairKey key, Func<Entity, bool> exists) =>
            exists != null && (!exists(key.A) || !exists(key.B));
    }
}
