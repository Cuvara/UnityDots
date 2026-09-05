using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// ScriptableObject defining a reusable set of ECS components for an entity type.
    /// Create via <c>Assets/Create/Cuvara/DOTS/Entity Archetype Preset</c>.
    /// </summary>
    /// <remarks>
    /// Instead of manually adding components per entity type in game code, define presets
    /// like "Player", "Mob", "Projectile" with their component sets and create entities
    /// with <see cref="ArchetypeFactory.Create"/>.
    /// </remarks>
    [CreateAssetMenu(menuName = "Cuvara/DOTS/Entity Archetype Preset", fileName = "NewArchetypePreset")]
    public sealed class EntityArchetypePreset : ScriptableObject
    {
        [Tooltip("Identifier matching the server's entity type (e.g. 'player', 'mob', 'npc')")]
        public string entityType = "";

        [Tooltip("View key for EntityViewRequest — matches ViewConfig/ViewArchetypeLibrary")]
        public string viewKey = "";

        [Header("Components")]
        [Tooltip("Include Health component")]
        public bool hasHealth = true;

        [Tooltip("Include MoveData component (velocity + bounds)")]
        public bool hasMoveData = false;

        [Tooltip("Include TimeToLive component")]
        public bool hasTimeToLive = false;

        [Tooltip("Include ViewOverlayAnchor for health bars / name plates")]
        public bool hasOverlayAnchor = true;

        [Header("Defaults")]
        [Tooltip("Initial HP when spawning")]
        public int defaultHp = 100;

        [Tooltip("Initial Max HP")]
        public int defaultMaxHp = 100;

        [Tooltip("Overlay anchor offset above entity (world units)")]
        public Vector3 overlayOffset = new Vector3(0, 2f, 0);

        [Tooltip("Time to live in seconds (0 = infinite, only used when hasTimeToLive is true)")]
        public float timeToLive = 0f;
    }
}
