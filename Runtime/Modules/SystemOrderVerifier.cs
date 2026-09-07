using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Modules
{
    /// <summary>
    /// Walks a world's system-group tree and checks that the order Entities actually produced
    /// satisfies every <c>[UpdateInGroup]</c>, <c>[UpdateAfter]</c> and <c>[UpdateBefore]</c> the
    /// systems declare — recursively, subgroups included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why attributes alone are not enough.</b> An attribute is a request. Entities honours it
    /// only when the system was added to the group the attribute names, only after that group was
    /// sorted, and only between members of the same batch (<c>OrderFirst</c>/<c>OrderLast</c>
    /// members are sorted apart and any relation to a normal member is dropped with a warning that
    /// is easy to miss). Every one of those failure modes has happened in this package's own
    /// bootstraps — a system added to the parent instead of the declared subgroup, a group left
    /// unsorted after a manual add — and none of them throws. This class turns them into a list of
    /// strings a test can assert is empty.
    /// </para>
    /// <para>
    /// Reads the group's <c>GetAllSystems</c> — the master update list — so what is verified is the
    /// order that will run, not the order that was requested.
    /// </para>
    /// </remarks>
    public static class SystemOrderVerifier
    {
        /// <summary>
        /// Verifies the three Unity root groups and everything under them. Empty result means every
        /// declared relation holds.
        /// </summary>
        public static List<string> Verify(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var violations = new List<string>();
            VerifyGroup(world, world.GetExistingSystemManaged<InitializationSystemGroup>(), violations);
            VerifyGroup(world, world.GetExistingSystemManaged<SimulationSystemGroup>(), violations);
            VerifyGroup(world, world.GetExistingSystemManaged<PresentationSystemGroup>(), violations);
            return violations;
        }

        /// <summary>Verifies one group and its subgroups.</summary>
        public static List<string> Verify(World world, ComponentSystemGroup root)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (root == null) throw new ArgumentNullException(nameof(root));

            var violations = new List<string>();
            VerifyGroup(world, root, violations);
            return violations;
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> listing every violation, if any.</summary>
        public static void Assert(World world)
        {
            var violations = Verify(world);
            if (violations.Count == 0) return;

            throw new InvalidOperationException(
                $"[Cuvara.DOTS] System ordering in world '{world.Name}' violates declared attributes:\n  " +
                string.Join("\n  ", violations));
        }

        /// <summary>
        /// Update-ordered member types of <paramref name="group"/>, as Entities will run them. For
        /// tests that want to assert a specific sequence rather than only the declared relations.
        /// </summary>
        public static List<Type> MembersInUpdateOrder(World world, ComponentSystemGroup group)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (group == null) throw new ArgumentNullException(nameof(group));

            var result = new List<Type>();
            using var handles = group.GetAllSystems(Allocator.Temp);
            for (var i = 0; i < handles.Length; i++)
            {
                result.Add(TypeOf(world, handles[i]));
            }

            return result;
        }

        private static Dictionary<int, Type> s_typeBySystemTypeIndex;

        /// <summary>
        /// Handle → managed <see cref="Type"/>. Entities exposes the handle's
        /// <c>SystemTypeIndex</c> but keeps the reverse lookup internal, so the map is built once
        /// from every loaded system type. Cheap enough for install-time and test use, which is all
        /// this class is for.
        /// </summary>
        private static Type TypeOf(World world, SystemHandle handle)
        {
            var index = (int)world.Unmanaged.GetSystemTypeIndex(handle);
            if (s_typeBySystemTypeIndex == null || !s_typeBySystemTypeIndex.ContainsKey(index))
            {
                s_typeBySystemTypeIndex = BuildTypeMap();
            }

            return s_typeBySystemTypeIndex.TryGetValue(index, out var type) ? type : typeof(object);
        }

        private static Dictionary<int, Type> BuildTypeMap()
        {
            var map = new Dictionary<int, Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    if (!typeof(ComponentSystemBase).IsAssignableFrom(type) && !typeof(ISystem).IsAssignableFrom(type)) continue;

                    try
                    {
                        var index = TypeManager.GetSystemTypeIndex(type);
                        map[(int)index] = type;
                    }
                    catch (Exception)
                    {
                        // Not a registered system type (a test helper, a type in an assembly Entities
                        // never scanned). Nothing in a group can have this index, so skipping is safe.
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Whether <paramref name="system"/> appears anywhere under <paramref name="root"/> — as a
        /// direct member or nested inside a subgroup.
        /// </summary>
        public static bool Contains(World world, ComponentSystemGroup root, Type system)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (root == null || system == null) return false;

            foreach (var member in MembersInUpdateOrder(world, root))
            {
                if (member == system) return true;
                if (typeof(ComponentSystemGroup).IsAssignableFrom(member)
                    && world.GetExistingSystemManaged(member) is ComponentSystemGroup sub
                    && Contains(world, sub, system))
                {
                    return true;
                }
            }

            return false;
        }

        private static void VerifyGroup(World world, ComponentSystemGroup group, List<string> violations)
        {
            if (group == null) return;

            var groupType = group.GetType();
            var members = MembersInUpdateOrder(world, group);
            var indexOf = new Dictionary<Type, int>();
            for (var i = 0; i < members.Count; i++) indexOf[members[i]] = i;

            foreach (var member in members)
            {
                var inGroup = member.GetCustomAttributes(typeof(UpdateInGroupAttribute), true)
                    .Cast<UpdateInGroupAttribute>()
                    .FirstOrDefault();

                // Unity's own systems are not held to this package's rule of declaring a group;
                // only a member that declares one, and declares the wrong one, is a violation.
                if (inGroup != null && inGroup.GroupType != groupType)
                {
                    violations.Add(
                        $"{member.Name} declares [UpdateInGroup({inGroup.GroupType.Name})] but was added to " +
                        $"{groupType.Name}; its ordering relations will never resolve. Add it to the group it names.");
                }

                var memberIsEdge = inGroup != null && (inGroup.OrderFirst || inGroup.OrderLast);
                var memberIndex = indexOf[member];

                foreach (var after in member.GetCustomAttributes(typeof(UpdateAfterAttribute), true).Cast<UpdateAfterAttribute>())
                {
                    if (!indexOf.TryGetValue(after.SystemType, out var otherIndex)) continue; // not a sibling: Entities ignores it
                    if (memberIsEdge || IsEdge(after.SystemType)) continue;                     // relation dropped by Entities
                    if (otherIndex < memberIndex) continue;

                    violations.Add(
                        $"{member.Name} declares [UpdateAfter({after.SystemType.Name})] but runs before it in " +
                        $"{groupType.Name}. Call SortSystems() on the group after adding systems.");
                }

                foreach (var before in member.GetCustomAttributes(typeof(UpdateBeforeAttribute), true).Cast<UpdateBeforeAttribute>())
                {
                    if (!indexOf.TryGetValue(before.SystemType, out var otherIndex)) continue;
                    if (memberIsEdge || IsEdge(before.SystemType)) continue;
                    if (memberIndex < otherIndex) continue;

                    violations.Add(
                        $"{member.Name} declares [UpdateBefore({before.SystemType.Name})] but runs after it in " +
                        $"{groupType.Name}. Call SortSystems() on the group after adding systems.");
                }

                // OrderLast members must come after every non-OrderLast member, OrderFirst before every
                // non-OrderFirst one — that is the whole guarantee the flag makes.
                if (inGroup != null && inGroup.OrderLast)
                {
                    for (var i = memberIndex + 1; i < members.Count; i++)
                    {
                        if (!IsOrderLast(members[i]))
                        {
                            violations.Add($"{member.Name} is OrderLast in {groupType.Name} but {members[i].Name} runs after it.");
                            break;
                        }
                    }
                }

                if (inGroup != null && inGroup.OrderFirst)
                {
                    for (var i = 0; i < memberIndex; i++)
                    {
                        if (!IsOrderFirst(members[i]))
                        {
                            violations.Add($"{member.Name} is OrderFirst in {groupType.Name} but {members[i].Name} runs before it.");
                            break;
                        }
                    }
                }

                if (typeof(ComponentSystemGroup).IsAssignableFrom(member)
                    && world.GetExistingSystemManaged(member) is ComponentSystemGroup sub)
                {
                    VerifyGroup(world, sub, violations);
                }
            }
        }

        private static UpdateInGroupAttribute InGroupOf(Type type) =>
            type.GetCustomAttributes(typeof(UpdateInGroupAttribute), true).Cast<UpdateInGroupAttribute>().FirstOrDefault();

        private static bool IsEdge(Type type)
        {
            var attribute = InGroupOf(type);
            return attribute != null && (attribute.OrderFirst || attribute.OrderLast);
        }

        private static bool IsOrderLast(Type type) => InGroupOf(type)?.OrderLast == true;

        private static bool IsOrderFirst(Type type) => InGroupOf(type)?.OrderFirst == true;
    }
}
