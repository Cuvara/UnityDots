#if CUVARA_DOTS_VCONTAINER
using Cuvara.DOTS.Messaging;
using VContainer;

namespace Cuvara.DOTS.DI
{
    /// <summary>
    /// Binds the package's publisher/subscriber interfaces to MessagePipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tension, and how it is resolved.</b> Messaging is to be done with MessagePipe, but
    /// <c>Cuvara.DOTS.Runtime</c> must install with only its four pinned Unity dependencies, so it
    /// cannot reference MessagePipe. It therefore declares
    /// <see cref="IDotsPublisher{TMessage}"/> / <see cref="IDotsSubscriber{TMessage}"/> — two
    /// one-method interfaces, no bus, no dispatch — and the adapters here are what make MessagePipe
    /// satisfy them. This package does not implement a messaging system; it implements two adapters.
    /// </para>
    /// <para>
    /// <b>With MessagePipe absent, publishing is a no-op.</b> That is the documented behaviour, not a
    /// degraded mode: <see cref="NullDotsPublisher{TMessage}"/> is registered and messages are
    /// dropped. Writing a signal bus to fill the gap is precisely what this design refuses — it would
    /// be a second messaging system living inside a package whose consumers already have one.
    /// </para>
    /// <para>
    /// Nothing inside the package subscribes. The messages exist for consumers.
    /// </para>
    /// </remarks>
    public static class MessagePipeVContainer
    {
        /// <summary>
        /// Registers the five view/chunk message types. The netcode adapter's two lifecycle events are registered by <see cref="NetworkLifecycleVContainer.RegisterDotsNetworkLifecycle"/> instead, because they exist only when <c>com.cuvara.netcode</c> does. Call after MessagePipe's own
        /// <c>RegisterMessagePipe()</c> and its <c>RegisterMessageBroker&lt;T&gt;</c> calls for these
        /// types; without them the resolve of <c>IPublisher&lt;T&gt;</c> fails at build time rather
        /// than silently doing nothing.
        /// </summary>
        public static IContainerBuilder RegisterDotsMessaging(this IContainerBuilder builder)
        {
            RegisterMessage<ViewSpawned>(builder);
            RegisterMessage<ViewDespawned>(builder);
            RegisterMessage<ChunkWarmed>(builder);
            RegisterMessage<ChunkReleased>(builder);
            RegisterMessage<ChunkCascadeReleased>(builder);
            return builder;
        }

        internal static void RegisterMessage<TMessage>(IContainerBuilder builder)
        {
#if CUVARA_DOTS_MESSAGEPIPE
            builder.Register<IDotsPublisher<TMessage>>(
                container => new MessagePipeDotsPublisher<TMessage>(container.Resolve<global::MessagePipe.IPublisher<TMessage>>()),
                Lifetime.Singleton);

            builder.Register<IDotsSubscriber<TMessage>>(
                container => new MessagePipeDotsSubscriber<TMessage>(container.Resolve<global::MessagePipe.ISubscriber<TMessage>>()),
                Lifetime.Singleton);
#else
            // No transport installed: the package still resolves a publisher, and it drops messages.
            builder.RegisterInstance<IDotsPublisher<TMessage>>(NullDotsPublisher<TMessage>.Instance);
#endif
        }
    }
}
#endif
