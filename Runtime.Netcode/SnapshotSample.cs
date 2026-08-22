using Cuvara.Netcode.Interpolation;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// One authoritative state the server reported for this entity, retained so the rendered
    /// position can be a point on the path between two of them rather than the newest one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wrapper around netcode's <see cref="InterpolationSample"/>, not a copy of it.</b> The
    /// element type has to implement <see cref="IBufferElementData"/>, which is an ECS interface a
    /// Unity-free package must not implement, so the wrapper exists purely to carry the identical
    /// bytes into chunk memory. The GameObject path stores the very same struct in a pooled array.
    /// Both are read through <c>ISampleBuffer</c> and evaluated by the same
    /// <see cref="SnapshotInterpolation.Evaluate{TBuffer}"/> — which is the point of stage 4: the
    /// interpolation arithmetic is not written a second time here, and there is nothing in this
    /// package for it to disagree with.
    /// </para>
    /// <para>
    /// <b><c>InternalBufferCapacity</c> is 8 because that is what keeps the buffer in the
    /// chunk.</b> Eight samples of 24 bytes is 192 bytes inline; a ninth would move the whole
    /// buffer to a heap allocation owned by the entity, turning a chunk walk into a pointer chase
    /// and adding an allocation per replicated entity that area-of-interest churn would then repeat.
    /// The number is the same 8 that <c>InterpolationConfig.RingCapacity</c> defaults to, and it is
    /// the same 8 for the same reason — that constant's own documentation names this attribute.
    /// A deployment that raises <c>RingCapacity</c> past 8 gets a correct but heap-backed buffer;
    /// raising this attribute to match is a source change, deliberately, because it is a memory
    /// layout decision rather than a tuning one.
    /// </para>
    /// <para>
    /// <b>Positions are stored in server space, verbatim off the wire</b>, exactly as
    /// <see cref="ReconciliationAnchor.ServerPosition"/> is and for the same reason:
    /// <see cref="SnapshotSpaceMapping"/> is applied once, after evaluation, in the job. Mapping on
    /// the way in would interpolate between two values that had each been through a float
    /// projection, which is not the same path the anchor took, and the two would drift apart in the
    /// last places for no benefit at all.
    /// </para>
    /// </remarks>
    [InternalBufferCapacity(8)]
    public struct SnapshotSample : IBufferElementData
    {
        /// <summary>The received state: server tick, receive time, and the wire's own x/y.</summary>
        public InterpolationSample Value;
    }

    /// <summary>
    /// <c>ISampleBuffer</c> over a <see cref="DynamicBuffer{T}"/> of
    /// <see cref="SnapshotSample"/>, so the shared evaluator can read chunk memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A struct, and only ever passed to a <c>where TBuffer : struct, ISampleBuffer</c>
    /// generic.</b> That gives a constrained call which Burst specialises per concrete buffer type;
    /// handing the same value to a parameter typed as the interface would box it on every frame for
    /// every entity and defeat the specialisation. netcode's own <c>ISampleBuffer</c> remarks say
    /// this is the reason the interface has this shape, and this is the ECS half of that contract.
    /// </para>
    /// <para>
    /// Constructing one costs nothing: it is a single <see cref="DynamicBuffer{T}"/> — itself a
    /// pointer and a length — living on the stack for the duration of one <c>Execute</c>.
    /// </para>
    /// </remarks>
    internal readonly struct SnapshotSampleBuffer : ISampleBuffer
    {
        private readonly DynamicBuffer<SnapshotSample> _samples;

        public SnapshotSampleBuffer(DynamicBuffer<SnapshotSample> samples)
        {
            _samples = samples;
        }

        /// <inheritdoc />
        public int Length => _samples.Length;

        /// <inheritdoc />
        public InterpolationSample this[int index] => _samples[index].Value;
    }
}
