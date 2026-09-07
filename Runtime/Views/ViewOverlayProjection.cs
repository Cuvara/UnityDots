using Unity.Mathematics;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>Why an overlay anchor is not drawn this frame.</summary>
    public enum ViewOverlayVisibility : byte
    {
        /// <summary>On screen (or off its edge) and in range; draw it at <see cref="ViewOverlayPlacement.Screen"/>.</summary>
        Visible = 0,

        /// <summary>
        /// Behind the camera's near plane. A behind-camera point projected naively lands mirrored on
        /// screen, in front of the player, at a spot nothing stands on; the contract is to hide it.
        /// </summary>
        BehindCamera = 1,

        /// <summary>Farther from the camera than the consumer's <c>maxDistance</c>.</summary>
        TooFar = 2,

        /// <summary>No camera to project with.</summary>
        NoCamera = 3,
    }

    /// <summary>One anchor's screen placement for this frame.</summary>
    public struct ViewOverlayPlacement
    {
        public ViewOverlayVisibility Visibility;

        /// <summary>Screen position in pixels, origin bottom-left (Unity's <c>Camera.WorldToScreenPoint</c> convention). Valid only when <see cref="Visibility"/> is <see cref="ViewOverlayVisibility.Visible"/>.</summary>
        public float2 Screen;

        /// <summary>Distance from the camera to the anchor in world units, for size/alpha falloff. Valid unless <see cref="ViewOverlayVisibility.NoCamera"/>.</summary>
        public float Distance;

        public bool IsVisible => Visibility == ViewOverlayVisibility.Visible;
    }

    /// <summary>
    /// World-to-screen for <see cref="ViewOverlayData.WorldPosition"/>. The one place the projection
    /// rule lives, so every consumer hides behind-camera anchors the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The host owns the camera; the package owns the rule.</b> The overlay buffer carries world
    /// positions because the package does not know which camera draws a given world, whether there
    /// is one, or whether the UI is UI Toolkit, UGUI or IMGUI. What it can own is the projection
    /// semantics — behind-camera is hidden, not mirrored; distance is measured from the camera
    /// position, not along the view axis; a point past the screen edge is still <i>Visible</i>, so a
    /// name plate can slide off rather than pop — and the pixel/UI-unit conversion stays with the
    /// host's UI system.
    /// </para>
    /// <para>
    /// Pure and allocation-free; safe to call per entry per frame.
    /// </para>
    /// </remarks>
    public static class ViewOverlayProjection
    {
        /// <summary>
        /// Projects one anchor. <paramref name="maxDistance"/> ≤ 0 disables distance filtering.
        /// </summary>
        public static ViewOverlayPlacement Project(Camera camera, float3 worldPosition, float maxDistance = 0f)
        {
            if (camera == null)
            {
                return new ViewOverlayPlacement { Visibility = ViewOverlayVisibility.NoCamera };
            }

            var cameraPosition = (float3)camera.transform.position;
            var distance = math.distance(cameraPosition, worldPosition);

            if (maxDistance > 0f && distance > maxDistance)
            {
                return new ViewOverlayPlacement { Visibility = ViewOverlayVisibility.TooFar, Distance = distance };
            }

            var projected = camera.WorldToScreenPoint(worldPosition);

            // z is the depth along the camera's forward axis. At or behind the near plane the
            // projection is mirrored and meaningless, so it is reported rather than returned.
            if (projected.z <= camera.nearClipPlane)
            {
                return new ViewOverlayPlacement { Visibility = ViewOverlayVisibility.BehindCamera, Distance = distance };
            }

            return new ViewOverlayPlacement
            {
                Visibility = ViewOverlayVisibility.Visible,
                Screen = new float2(projected.x, projected.y),
                Distance = distance,
            };
        }
    }
}
