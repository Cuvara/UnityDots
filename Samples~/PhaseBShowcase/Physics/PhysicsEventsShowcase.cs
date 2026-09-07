using System;
using System.Collections.Generic;
using System.Text;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Physics;
using Cuvara.DOTS.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UIElements;

// UnityEngine and Unity.Physics both define Material and Collider, so neither simple name is
// written anywhere below: collider blobs are reached through `var` and materials through the
// PhysicsBodyFactory helpers. Every other Unity.Physics type used here is unambiguous.

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Scene 4 (plan item D07): <see cref="ColliderLibrary"/> bodies, the physics event pipeline
    /// installed as a module, the enter/stay/exit stream with canonical pair ordering and
    /// aggregated contact counts, destruction mid-contact, install/uninstall cycles returning to
    /// baseline, and the one-integrator guard.
    /// </summary>
    /// <remarks>
    /// This whole assembly is gated on <c>com.unity.physics</c> exactly as <c>Cuvara.DOTS.Physics</c>
    /// is, so in a project without the package the sample imports and simply contains no code.
    /// <para>
    /// It runs in the default world, because that is the world with a real physics pipeline; a
    /// hand-made world has no <c>PhysicsSystemGroup</c> and the collector would idle forever.
    /// </para>
    /// </remarks>
    public sealed class PhysicsEventsShowcase : MonoBehaviour
    {
        private static readonly float3 BallSpawn = new float3(0f, 6f, 0f);
        private static readonly float3 GroundSize = new float3(8f, 1f, 8f);
        private static readonly float3 ZoneCentre = new float3(3f, 1.5f, 0f);

        private World _world;
        private ColliderLibrary _library;

        private Entity _ground = Entity.Null;
        private Entity _zone = Entity.Null;
        private readonly List<Entity> _balls = new List<Entity>();
        private Entity _physicsMover = Entity.Null;
        private Entity _directMover = Entity.Null;

        private int _lastRenderedStep = -1;
        private int _enterCount;
        private int _stayCount;
        private int _exitCount;

        private ShowcaseUi.RollingLog _log;
        private Label _stateLabel;
        private readonly StringBuilder _state = new StringBuilder();

        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null)
            {
                Debug.LogError("[PhaseBShowcase] No default world; physics cannot step.");
                enabled = false;
                return;
            }

            _library = new ColliderLibrary();

            var root = ShowcaseUi.Root(this);
            if (root == null) { enabled = false; return; }

            _stateLabel = ShowcaseUi.Label(root, "state");
            _log = new ShowcaseUi.RollingLog(ShowcaseUi.Label(root, "log"), 18);

            ShowcaseUi.OnClick(root, "install-events", InstallEvents);
            ShowcaseUi.OnClick(root, "uninstall-events", UninstallEvents);
            ShowcaseUi.OnClick(root, "cycle-modules", CycleModules);

            ShowcaseUi.OnClick(root, "build-stage", BuildStage);
            ShowcaseUi.OnClick(root, "drop-ball", () => DropBall(BallSpawn));
            ShowcaseUi.OnClick(root, "drop-into-zone", () => DropBall(ZoneCentre + new float3(0f, 4f, 0f)));
            ShowcaseUi.OnClick(root, "destroy-mid-contact", DestroyMidContact);
            ShowcaseUi.OnClick(root, "clear-bodies", ClearBodies);

            ShowcaseUi.OnClick(root, "spawn-physics-mover", SpawnPhysicsMover);
            ShowcaseUi.OnClick(root, "spawn-direct-mover", SpawnDirectMover);
            ShowcaseUi.OnClick(root, "check-integrators", CheckIntegrators);
            ShowcaseUi.OnClick(root, "break-guard", BreakGuard);

            InstallEvents();
            BuildStage();
            _log.Add("Ready. Drop a ball onto the box, or into the trigger zone.");
        }

        private void OnDestroy()
        {
            ClearBodies();

            if (_world != null && _world.IsCreated)
            {
                // Modules before the library: uninstalling stops the collector touching bodies, and
                // the library frees blobs regardless of outstanding leases.
                //
                // Scope, not UninstallAll: this is the default world, and both modules this scene
                // installs are Session-scoped. A blanket uninstall would tear down whatever else
                // the project had installed here.
                DotsModules.UninstallScope(_world, DotsModuleScope.Session);
            }

            _library?.Dispose();
        }

        // -------------------------------------------------------------- modules

        private void InstallEvents()
        {
            if (_world == null || !_world.IsCreated) return;

            try
            {
                // requirePhysicsPipeline: true turns the "no PhysicsSimulationGroup" warning into a
                // throw. In a sample a silently idle collector is worse than a loud failure.
                PhysicsEventsBootstrap.Install(_world, DotsModuleScope.Session, requirePhysicsPipeline: true);
                PhysicsMovementBootstrap.Install(_world, DotsModuleScope.Session, requirePhysicsPipeline: true);
                _log.Add($"Installed. modules=[{string.Join(", ", DotsModules.Installed(_world))}] " +
                         $"InstallCount(PhysicsEvents)={DotsModules.InstallCount(_world, PhysicsEventsBootstrap.ModuleName)}");
            }
            catch (InvalidOperationException e)
            {
                _log.Add("Install refused: " + e.Message);
            }
        }

        private void UninstallEvents()
        {
            if (_world == null || !_world.IsCreated) return;

            var count = DotsModules.UninstallScope(_world, DotsModuleScope.Session);
            _log.Add($"UninstallScope(Session) removed {count}. " +
                     $"events installed={PhysicsEventsBootstrap.IsInstalled(_world)} " +
                     $"movement installed={PhysicsMovementBootstrap.IsInstalled(_world)}");
        }

        /// <summary>
        /// Five install/uninstall cycles with the bodies torn down, so the collider library is
        /// visibly back to zero blobs and zero leases — the check that a module really let go.
        /// </summary>
        private void CycleModules()
        {
            ClearBodies();

            for (var i = 0; i < 5; i++)
            {
                InstallEvents();
                UninstallEvents();
            }

            _log.Add($"5 install/uninstall cycles done. modules={DotsModules.Installed(_world).Count} " +
                     $"blobs={_library.Count} leases={_library.TotalLeases} — baseline is 0/0/0.");

            InstallEvents();
        }

        // ---------------------------------------------------------------- bodies

        private Entity NewBodyEntity(float3 at)
        {
            var manager = _world.EntityManager;
            var entity = manager.CreateEntity();
            manager.AddComponentData(entity, LocalTransform.FromPosition(at));
            return entity;
        }

        /// <summary>Static box to land on plus a static trigger zone to fall through.</summary>
        private void BuildStage()
        {
            var manager = _world.EntityManager;

            if (_ground == Entity.Null || !manager.Exists(_ground))
            {
                _ground = NewBodyEntity(new float3(0f, -0.5f, 0f));
                PhysicsBodyFactory.AddStaticBody(manager, _ground, _library, ColliderShape.Box, GroundSize);
            }

            if (_zone == Entity.Null || !manager.Exists(_zone))
            {
                // A trigger only fires against a dynamic or kinematic body; two statics never do.
                _zone = NewBodyEntity(ZoneCentre);
                PhysicsBodyFactory.AddStaticBody(manager, _zone, _library, ColliderShape.Sphere,
                    new float3(2f), null, PhysicsBodyFactory.TriggerMaterial());
            }

            _log.Add($"Stage built: static box + trigger zone. blobs={_library.Count} leases={_library.TotalLeases}");
        }

        private void DropBall(float3 at)
        {
            var manager = _world.EntityManager;
            var ball = NewBodyEntity(at);

            // CollisionEventMaterial so the pair raises collision events rather than only colliding.
            PhysicsBodyFactory.AddDynamicBody(manager, ball, _library, ColliderShape.Sphere,
                new float3(0.5f), 1f, null, PhysicsBodyFactory.CollisionEventMaterial());

            _balls.Add(ball);
            _log.Add($"Dropped a ball as {Describe(ball)}. blobs={_library.Count} leases={_library.TotalLeases}");
        }

        /// <summary>
        /// Destroys the newest ball while it is (probably) touching something. The tracker turns
        /// that into an Exit carrying AnyEntityDestroyed, and the freed index is then handed to the
        /// next entity with a higher version — a different entity, so a fresh pair and a fresh Enter.
        /// </summary>
        private void DestroyMidContact()
        {
            if (_balls.Count == 0) { _log.Add("Drop a ball first."); return; }

            var victim = _balls[_balls.Count - 1];
            _balls.RemoveAt(_balls.Count - 1);
            var describedVictim = Describe(victim);

            ReleaseAndDestroy(victim);
            _log.Add($"Destroyed {describedVictim} mid-contact -> expect Exit with AnyEntityDestroyed=true.");

            // Immediately claim the freed slot so index reuse is visible in the log.
            var replacement = NewBodyEntity(BallSpawn);
            PhysicsBodyFactory.AddDynamicBody(_world.EntityManager, replacement, _library, ColliderShape.Sphere,
                new float3(0.5f), 1f, null, PhysicsBodyFactory.CollisionEventMaterial());
            _balls.Add(replacement);
            _log.Add($"Replacement is {Describe(replacement)} — same index, higher version means a different entity.");
        }

        /// <summary>
        /// Returns the entity's collider lease before destroying it. Skipping this leaks a blob:
        /// the library counts leases, it does not watch entities.
        /// </summary>
        private void ReleaseAndDestroy(Entity entity)
        {
            var manager = _world.EntityManager;
            if (!manager.Exists(entity)) return;

            if (manager.HasComponent<PhysicsCollider>(entity))
            {
                var blob = manager.GetComponentData<PhysicsCollider>(entity).Value;
                if (_library.Owns(blob)) _library.Release(blob);
            }

            manager.DestroyEntity(entity);
        }

        private void ClearBodies()
        {
            if (_world == null || !_world.IsCreated) return;

            foreach (var ball in _balls) ReleaseAndDestroy(ball);
            _balls.Clear();

            ReleaseAndDestroy(_ground);
            ReleaseAndDestroy(_zone);
            ReleaseAndDestroy(_physicsMover);
            ReleaseAndDestroy(_directMover);
            _ground = _zone = _physicsMover = _directMover = Entity.Null;

            _log?.Add($"Bodies cleared. blobs={_library.Count} leases={_library.TotalLeases}");
        }

        // ------------------------------------------------------- single integrator

        /// <summary>
        /// A physics-driven mover: a kinematic body, so <c>PhysicsBodyFactory</c> tags it
        /// <see cref="PhysicsDrivenMovement"/> and the bridge writes MoveData.Velocity into
        /// PhysicsVelocity. Unity.Physics integrates it; the package's own movers skip it.
        /// </summary>
        private void SpawnPhysicsMover()
        {
            var manager = _world.EntityManager;
            ReleaseAndDestroy(_physicsMover);

            _physicsMover = NewBodyEntity(new float3(-4f, 1f, 3f));
            PhysicsBodyFactory.AddKinematicBody(manager, _physicsMover, _library, ColliderShape.Capsule,
                new float3(0.5f, 2f, 0f));
            manager.AddComponentData(_physicsMover, new MoveData
            {
                Velocity = new float3(1.5f, 0f, 0f),
                BoundsMin = new float3(-10f, -10f, -10f),
                BoundsMax = new float3(10f, 10f, 10f),
            });

            _log.Add($"Physics-driven mover {Describe(_physicsMover)}: PhysicsVelocity + PhysicsDrivenMovement. " +
                     "The bridge writes velocity; Unity.Physics integrates.");
        }

        /// <summary>
        /// A direct mover: MoveData and no physics body at all, so the package's movement systems
        /// own its transform. Neither the velocity nor the tag is present, which also satisfies the
        /// guard — the rule is that exactly one integrator claims the entity.
        /// </summary>
        private void SpawnDirectMover()
        {
            var manager = _world.EntityManager;
            ReleaseAndDestroy(_directMover);

            _directMover = NewBodyEntity(new float3(-4f, 1f, -3f));
            manager.AddComponentData(_directMover, new MoveData
            {
                Velocity = new float3(1.5f, 0f, 0f),
                BoundsMin = new float3(-10f, -10f, -10f),
                BoundsMax = new float3(10f, 10f, 10f),
            });

            _log.Add($"Direct mover {Describe(_directMover)}: MoveData only, no body. " +
                     "MoveBounceSystem/MoveTowardSystem own this transform.");
        }

        private void CheckIntegrators()
        {
            var manager = _world.EntityManager;
            foreach (var pair in new[] { ("physics mover", _physicsMover), ("direct mover", _directMover) })
            {
                if (pair.Item2 == Entity.Null || !manager.Exists(pair.Item2))
                {
                    _log.Add($"{pair.Item1}: not spawned.");
                    continue;
                }

                try
                {
                    PhysicsBodyValidation.AssertSingleIntegrator(manager, pair.Item2);
                    _log.Add($"{pair.Item1}: exactly one integrator. OK.");
                }
                catch (InvalidOperationException e)
                {
                    _log.Add($"{pair.Item1}: GUARD TRIPPED — {e.Message}");
                }
            }
        }

        /// <summary>
        /// Strips the tag off a physics body, leaving it with a PhysicsVelocity that Unity.Physics
        /// integrates and no tag to keep the package's movers off it. That is the double-integration
        /// bug the guard exists to name; the tag is put back immediately afterwards.
        /// </summary>
        private void BreakGuard()
        {
            var manager = _world.EntityManager;
            if (_physicsMover == Entity.Null || !manager.Exists(_physicsMover))
            {
                _log.Add("Spawn the physics mover first.");
                return;
            }

            manager.RemoveComponent<PhysicsDrivenMovement>(_physicsMover);
            try
            {
                PhysicsBodyValidation.AssertSingleIntegrator(manager, _physicsMover);
                _log.Add("No exception — unexpected; the guard should have refused this entity.");
            }
            catch (InvalidOperationException e)
            {
                _log.Add("Guard tripped as intended: " + e.Message);
            }
            finally
            {
                manager.AddComponent<PhysicsDrivenMovement>(_physicsMover);
                _log.Add("Tag restored; the entity has one integrator again.");
            }
        }

        // -------------------------------------------------------------- reporting

        private string Describe(Entity entity) => $"Entity({entity.Index}:{entity.Version})";

        private void LateUpdate()
        {
            DrainEvents();
            RenderState();
        }

        /// <summary>
        /// Reads the buffer once per physics step. The buffer is rebuilt every step, so reading it
        /// per frame without the step guard would either double-count or miss events.
        /// </summary>
        private void DrainEvents()
        {
            if (_world == null || !_world.IsCreated) return;

            var buffer = PhysicsEventsBootstrap.Buffer(_world);
            if (buffer == null || buffer.Step == _lastRenderedStep) return;
            _lastRenderedStep = buffer.Step;

            for (var i = 0; i < buffer.Collisions.Count; i++)
            {
                var collision = buffer.Collisions[i];
                Count(collision.Phase);

                // The pair key is already canonical inside the event; recomputing it shows that
                // A is the lower index and whether the caller's order had to be swapped.
                var key = new PhysicsPairKey(collision.EntityA, collision.EntityB);
                _log.Add($"[{buffer.Step}] collision {collision.Phase} " +
                         $"A={Describe(collision.EntityA)} B={Describe(collision.EntityB)} " +
                         $"swapped={key.Swapped} contacts={collision.ContactCount} " +
                         $"impulse={collision.Impulse:0.###} destroyed={collision.AnyEntityDestroyed}");
            }

            for (var i = 0; i < buffer.Triggers.Count; i++)
            {
                var trigger = buffer.Triggers[i];
                Count(trigger.Phase);
                _log.Add($"[{buffer.Step}] trigger {trigger.Phase} " +
                         $"A={Describe(trigger.EntityA)} B={Describe(trigger.EntityB)} " +
                         $"destroyed={trigger.AnyEntityDestroyed}");
            }
        }

        private void Count(PhysicsContactPhase phase)
        {
            switch (phase)
            {
                case PhysicsContactPhase.Enter: _enterCount++; break;
                case PhysicsContactPhase.Stay: _stayCount++; break;
                case PhysicsContactPhase.Exit: _exitCount++; break;
            }
        }

        private void RenderState()
        {
            if (_stateLabel == null || _world == null || !_world.IsCreated) return;

            var buffer = PhysicsEventsBootstrap.Buffer(_world);
            var installed = DotsModules.Installed(_world);

            _state.Clear();
            _state.Append($"modules ({installed.Count}): {(installed.Count == 0 ? "-" : string.Join(", ", installed))}\n");
            _state.Append($"  PhysicsEvents installed={PhysicsEventsBootstrap.IsInstalled(_world)} " +
                          $"count={DotsModules.InstallCount(_world, PhysicsEventsBootstrap.ModuleName)}\n");
            _state.Append($"  PhysicsMovement installed={PhysicsMovementBootstrap.IsInstalled(_world)}\n");
            _state.Append($"\nColliderLibrary\n  blobs={_library.Count}  totalLeases={_library.TotalLeases}\n");
            _state.Append($"\nPhysicsEventBuffer\n  step={buffer?.Step ?? 0}  " +
                          $"collisions={buffer?.Collisions.Count ?? 0}  triggers={buffer?.Triggers.Count ?? 0}\n");
            _state.Append($"  phases seen: Enter={_enterCount} Stay={_stayCount} Exit={_exitCount}\n");
            _state.Append($"\nbodies: balls={_balls.Count} ground={_ground != Entity.Null} zone={_zone != Entity.Null}\n");

            _stateLabel.text = _state.ToString();
        }
    }
}
