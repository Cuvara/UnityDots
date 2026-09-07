namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Which two world axes a <see cref="MinimapEntry.Position"/> is taken from.
    /// </summary>
    /// <remarks>
    /// The server simulates a 2D plane and <c>SnapshotSpaceMapping</c> decides where that plane lies
    /// in the client's world; the minimap has to project the same way or every marker is on the wrong
    /// axis. Chosen once at <c>MinimapBootstrap.Install</c>, not per entity, for the same reason the
    /// mapping is a constructor argument rather than a per-archetype setting.
    /// </remarks>
    public enum MinimapPlane : byte
    {
        /// <summary>World X and Z — the ground plane a <c>SnapshotSpaceMapping.XZPlane</c> world uses.</summary>
        XZ = 0,

        /// <summary>World X and Y — a side-on or top-down <c>XYPlane</c> world.</summary>
        XY = 1,
    }
}
