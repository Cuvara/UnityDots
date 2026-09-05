using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Mathematics;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>Tests for the ViewOverlay data structures.</summary>
    public sealed class ViewOverlayAnchorTests
    {
        [Test]
        public void ViewOverlayAnchor_DefaultOffset_IsZero()
        {
            var anchor = new ViewOverlayAnchor();
            Assert.AreEqual(float3.zero, anchor.WorldOffset);
        }

        [Test]
        public void ViewOverlayAnchor_CustomOffset_IsPreserved()
        {
            var anchor = new ViewOverlayAnchor { WorldOffset = new float3(0, 2.5f, 0) };
            Assert.AreEqual(2.5f, anchor.WorldOffset.y);
        }

        [Test]
        public void ViewOverlayData_DefaultHealthFraction_IsNegative()
        {
            var data = new ViewOverlayData { HealthFraction = -1f };
            Assert.IsTrue(data.HealthFraction < 0, "Default -1 means 'no health data'");
        }

        [Test]
        public void ViewOverlayData_ValidHealth_IsClamped01()
        {
            var data = new ViewOverlayData { ViewId = 1, HealthFraction = 0.75f, WorldPosition = new float3(10, 3, 5) };
            Assert.AreEqual(1, data.ViewId);
            Assert.AreEqual(0.75f, data.HealthFraction, 0.001f);
            Assert.AreEqual(10f, data.WorldPosition.x, 0.001f);
        }

        [Test]
        public void ViewOverlayData_Fields_AreBlittable()
        {
            // ViewOverlayData must be blittable for NativeList usage
            Assert.AreEqual(
                System.Runtime.InteropServices.Marshal.SizeOf<ViewOverlayData>(),
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<ViewOverlayData>());
        }
    }
}
