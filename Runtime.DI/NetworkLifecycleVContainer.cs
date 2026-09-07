#if CUVARA_DOTS_VCONTAINER && CUVARA_NETCODE
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Netcode;
using VContainer;

namespace Cuvara.DOTS.DI
{
    /// <summary>
    /// Registers the netcode adapter's <see cref="NetworkEntityLifecycle"/> and, with MessagePipe
    /// present, forwards its events into MessagePipe brokers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <b>both</b> VContainer and <c>com.cuvara.netcode</c>, and it is the only file in this
    /// assembly that names a netcode-adapter type. The assembly's reference to
    /// <c>Cuvara.DOTS.Netcode</c> is ignored by Unity when that assembly is compiled out, exactly as
    /// its references to <c>Cuvara.DOTS.GameLogic</c> and <c>MessagePipe</c> already are.
    /// </para>
    /// <para>
    /// <b>The core does not need this.</b> Without DI a consumer constructs a
    /// <see cref="NetworkEntityLifecycle"/> (or lets <see cref="DotsEntityView"/> construct one) and
    /// subscribes to it directly. This registration exists so the container owns the instance, so the
    /// view can be constructed with it, and so a MessagePipe consumer can hear the same events through
    /// <c>ISubscriber&lt;NetworkEntitySpawned&gt;</c> without knowing this package exists.
    /// </para>
    /// <para>
    /// <b>With MessagePipe</b>, call after <c>RegisterMessagePipe()</c> and after
    /// <c>RegisterMessageBroker&lt;NetworkEntitySpawned&gt;</c> /
    /// <c>RegisterMessageBroker&lt;NetworkEntityDespawned&gt;</c> — the same rule
    /// <see cref="MessagePipeVContainer.RegisterDotsMessaging"/> states for the five view messages.
    /// Both delivery routes then fire for every event, in the same order: the hub's direct handlers
    /// first, then the MessagePipe publisher. <b>Without MessagePipe</b>, the hub itself is
    /// registered as <c>IDotsSubscriber&lt;T&gt;</c> for both event types, so consumer code written
    /// against the package's interface resolves either way.
    /// </para>
    /// </remarks>
    public static class NetworkLifecycleVContainer
    {
        /// <summary>
        /// Registers a singleton <see cref="NetworkEntityLifecycle"/>. Hand it to
        /// <see cref="DotsEntityView"/>'s constructor when building the view.
        /// </summary>
        public static IContainerBuilder RegisterDotsNetworkLifecycle(this IContainerBuilder builder)
        {
#if CUVARA_DOTS_MESSAGEPIPE
            // Publisher and subscriber adapters over the MessagePipe brokers, same as the view
            // messages. The hub forwards into the publishers; consumers subscribe through either.
            MessagePipeVContainer.RegisterMessage<NetworkEntitySpawned>(builder);
            MessagePipeVContainer.RegisterMessage<NetworkEntityDespawned>(builder);

            builder.Register(container => new NetworkEntityLifecycle(
                    container.Resolve<IDotsPublisher<NetworkEntitySpawned>>(),
                    container.Resolve<IDotsPublisher<NetworkEntityDespawned>>()),
                Lifetime.Singleton).AsSelf();
#else
            // No transport: the hub is the subscriber surface. Its handler list is the delivery.
            builder.Register(container => new NetworkEntityLifecycle(), Lifetime.Singleton)
                .AsSelf()
                .As<IDotsSubscriber<NetworkEntitySpawned>>()
                .As<IDotsSubscriber<NetworkEntityDespawned>>();
#endif
            return builder;
        }
    }
}
#endif
