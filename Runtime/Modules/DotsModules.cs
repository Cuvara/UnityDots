using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Modules
{
    /// <summary>
    /// Per-world record of which package modules are installed, who owns each, and how to take each
    /// one down. Every module bootstrap in the package registers itself here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>State lives in the world, never in a static.</b> Each installed module is one entity
    /// carrying a <see cref="DotsModuleRecord"/>. Disposing the world disposes the records, so a
    /// disposed session cannot leave a dangling <c>World</c> reference behind in this class — there
    /// is nowhere for one to live. Two worlds side by side each keep their own set; uninstalling in
    /// one cannot reach the other's.
    /// </para>
    /// <para>
    /// <b>Two kinds of teardown.</b> A module's own <c>Uninstall(world)</c> is a <i>temporary
    /// disable</i>: singletons and live GameObjects go, systems stay created and idle (they
    /// <c>RequireForUpdate</c> a singleton that is now absent), and a later <c>Install</c> resumes.
    /// <see cref="UninstallAll"/> followed by <c>world.Dispose()</c> is <i>permanent</i>: the
    /// systems' <c>OnDestroy</c> then releases their native containers. Call <see cref="UninstallAll"/>
    /// before disposing so managed views are recycled to their pools first — a disposed world cannot
    /// run the despawn system that would otherwise do it.
    /// </para>
    /// </remarks>
    public static class DotsModules
    {
        /// <summary>
        /// Records that <paramref name="name"/> is installed in <paramref name="world"/> under
        /// <paramref name="scope"/>. Idempotent by name: a repeat call bumps
        /// <see cref="DotsModuleRecord.InstallCount"/> and refreshes the uninstaller rather than
        /// adding a second record.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The module is already installed under a <i>different</i> scope. A module cannot be both
        /// root- and session-owned in one world, because the two teardown paths would then each
        /// believe the other still holds it.
        /// </exception>
        public static Entity Register(World world, string name, DotsModuleScope scope, Action<World> uninstall)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("module name must not be empty", nameof(name));
            if (uninstall == null) throw new ArgumentNullException(nameof(uninstall));

            var entityManager = world.EntityManager;
            if (TryFind(world, name, out var entity))
            {
                var record = entityManager.GetComponentObject<DotsModuleRecord>(entity);
                if (record.Scope != scope)
                {
                    throw new InvalidOperationException(
                        $"[Cuvara.DOTS] Module '{name}' is already installed in world '{world.Name}' as " +
                        $"{record.Scope}-scoped and cannot be re-installed as {scope}-scoped. Uninstall it " +
                        "first, or keep one owner per module per world.");
                }

                record.Uninstall = uninstall;
                record.InstallCount++;
                return entity;
            }

            entity = entityManager.CreateEntity();
            entityManager.AddComponentObject(entity, new DotsModuleRecord
            {
                Name = name,
                Scope = scope,
                Uninstall = uninstall,
                InstallCount = 1,
            });
#if UNITY_EDITOR
            entityManager.SetName(entity, "DotsModule:" + name);
#endif
            return entity;
        }

        /// <summary>Drops the record for <paramref name="name"/>. Safe when there is none.</summary>
        public static bool Unregister(World world, string name)
        {
            if (world == null || !world.IsCreated || string.IsNullOrEmpty(name)) return false;
            if (!TryFind(world, name, out var entity)) return false;

            world.EntityManager.DestroyEntity(entity);
            return true;
        }

        /// <summary>Whether a module of that name is currently recorded as installed.</summary>
        public static bool IsInstalled(World world, string name) =>
            world != null && world.IsCreated && TryFind(world, name, out _);

        /// <summary>The recorded scope of an installed module.</summary>
        public static bool TryGetScope(World world, string name, out DotsModuleScope scope)
        {
            scope = default;
            if (world == null || !world.IsCreated || !TryFind(world, name, out var entity)) return false;

            scope = world.EntityManager.GetComponentObject<DotsModuleRecord>(entity).Scope;
            return true;
        }

        /// <summary>Times <c>Install</c> has been called for the module since its last uninstall; 0 when absent.</summary>
        public static int InstallCount(World world, string name)
        {
            if (world == null || !world.IsCreated || !TryFind(world, name, out var entity)) return 0;
            return world.EntityManager.GetComponentObject<DotsModuleRecord>(entity).InstallCount;
        }

        /// <summary>Names of every installed module, in no particular order.</summary>
        public static List<string> Installed(World world)
        {
            var result = new List<string>();
            if (world == null || !world.IsCreated) return result;

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<DotsModuleRecord>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                result.Add(entityManager.GetComponentObject<DotsModuleRecord>(entities[i]).Name);
            }

            return result;
        }

        /// <summary>
        /// Uninstalls every module recorded under <paramref name="scope"/>, in reverse install order.
        /// </summary>
        /// <returns>How many modules were uninstalled.</returns>
        /// <remarks>
        /// Reverse order because later modules depend on earlier ones (camera on views, prediction
        /// on the netcode adapter), and a dependency must outlive its dependents' teardown. Each
        /// uninstaller is expected to remove its own record; any record it leaves is destroyed here
        /// so a failed or partial uninstall never reports the module as still installed.
        /// </remarks>
        public static int UninstallScope(World world, DotsModuleScope scope) => Uninstall(world, scope, all: false);

        /// <summary>Uninstalls every recorded module regardless of scope. Call before <c>world.Dispose()</c>.</summary>
        public static int UninstallAll(World world) => Uninstall(world, default, all: true);

        private static int Uninstall(World world, DotsModuleScope scope, bool all)
        {
            if (world == null || !world.IsCreated) return 0;

            var entityManager = world.EntityManager;
            var records = new List<(Entity entity, DotsModuleRecord record)>();
            using (var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<DotsModuleRecord>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var record = entityManager.GetComponentObject<DotsModuleRecord>(entities[i]);
                    if (all || record.Scope == scope) records.Add((entities[i], record));
                }
            }

            // Entity creation order is not guaranteed to be index order, but records are created
            // one per Register and Register is called from Install, so the entity index is the
            // install order in every case that does not recycle an index mid-session. Reverse it.
            records.Sort((a, b) => b.entity.Index.CompareTo(a.entity.Index));

            var count = 0;
            foreach (var (entity, record) in records)
            {
                record.Uninstall?.Invoke(world);
                if (world.IsCreated && entityManager.Exists(entity)) entityManager.DestroyEntity(entity);
                count++;
            }

            return count;
        }

        private static bool TryFind(World world, string name, out Entity entity)
        {
            entity = Entity.Null;
            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<DotsModuleRecord>());
            if (query.IsEmpty) return false;

            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                if (entityManager.GetComponentObject<DotsModuleRecord>(entities[i]).Name == name)
                {
                    entity = entities[i];
                    return true;
                }
            }

            return false;
        }

        // ---- Precondition helpers shared by the module bootstraps -------------------------------

        /// <summary>
        /// Throws unless exactly one entity in the world carries <typeparamref name="T"/>.
        /// </summary>
        /// <param name="module">The module doing the check, named in the error.</param>
        /// <param name="hint">What the consumer must call to satisfy the requirement.</param>
        public static void RequireSingleton<T>(World world, string module, string hint) where T : IComponentData
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            var count = query.CalculateEntityCount();
            if (count == 1) return;

            throw new InvalidOperationException(
                $"[Cuvara.DOTS] {module} requires exactly one {typeof(T).Name} singleton in world '{world.Name}' " +
                $"and found {count}. {hint}");
        }

        /// <summary>Throws unless a managed system of type <typeparamref name="T"/> exists in the world.</summary>
        public static T RequireSystem<T>(World world, string module, string hint) where T : ComponentSystemBase
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var system = world.GetExistingSystemManaged<T>();
            if (system != null) return system;

            throw new InvalidOperationException(
                $"[Cuvara.DOTS] {module} requires {typeof(T).Name} to exist in world '{world.Name}'. {hint}");
        }

        /// <summary>Throws when a configuration value is NaN or infinite.</summary>
        public static void RequireFinite(float value, string module, string field)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value)) return;

            throw new ArgumentException(
                $"[Cuvara.DOTS] {module}: {field} must be finite, was {value}.");
        }

        /// <summary>Throws when a configuration value is below <paramref name="minInclusive"/> or not finite.</summary>
        public static void RequireAtLeast(float value, float minInclusive, string module, string field)
        {
            RequireFinite(value, module, field);
            if (value >= minInclusive) return;

            throw new ArgumentException(
                $"[Cuvara.DOTS] {module}: {field} must be >= {minInclusive}, was {value}.");
        }
    }
}
