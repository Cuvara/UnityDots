using System;
using Unity.Entities;

namespace Cuvara.DOTS.Modules
{
    /// <summary>
    /// One installed module, as recorded on its registration entity by <see cref="DotsModules"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A managed <see cref="IComponentData"/> because it carries a delegate: the uninstaller is what
    /// lets the core assembly tear down a module it cannot name — the physics bridge lives in an
    /// optional assembly the core never references, yet <see cref="DotsModules.UninstallScope"/>
    /// must still be able to remove it.
    /// </para>
    /// <para>
    /// One entity per module, found by <see cref="Name"/>. Two records with the same name in one
    /// world would make "is it installed" ambiguous, so <see cref="DotsModules.Register"/> updates
    /// the existing record instead of adding a second.
    /// </para>
    /// </remarks>
    public sealed class DotsModuleRecord : IComponentData
    {
        /// <summary>Stable module name, e.g. <c>"Views"</c>, <c>"CameraFollow"</c>, <c>"PhysicsMovement"</c>.</summary>
        public string Name;

        /// <summary>Which scope owns the module's lifetime.</summary>
        public DotsModuleScope Scope;

        /// <summary>
        /// Tears the module down in the world the record lives in. Must be idempotent: the record
        /// may be gone by the time a consumer also calls the module's own <c>Uninstall</c>.
        /// </summary>
        public Action<World> Uninstall;

        /// <summary>Number of times <c>Install</c> was called for this module since it was last uninstalled.</summary>
        public int InstallCount;
    }
}
