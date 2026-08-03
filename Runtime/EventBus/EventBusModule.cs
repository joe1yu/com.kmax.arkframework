using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class EventBusModule : IFrameworkModule, IUpdateModule
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.Empty<string>();

        private EventBus _eventBus;
        private bool _started;
        private bool _disposed;

        public string Id => BuiltInModuleIds.EventBus;

        public IReadOnlyCollection<string> Dependencies => NoDependencies;

        public ValueTask InitializeAsync(
            ModuleContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EventBusModule));
            }

            if (_eventBus != null)
            {
                throw new InvalidOperationException(
                    "EventBusModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var eventBus = new EventBus(context.Logger);
            try
            {
                context.ModuleScope.RegisterInstance<IEventBus>(eventBus);
                _eventBus = eventBus;
            }
            catch
            {
                eventBus.Dispose();
                throw;
            }

            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EventBusModule));
            }

            if (_eventBus == null)
            {
                throw new InvalidOperationException(
                    "EventBusModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _started = false;
            _eventBus?.Stop();
            return default;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            _started = false;
            _eventBus = null;
            return default;
        }

        public void Update(float deltaTime)
        {
            if (_started)
            {
                _eventBus.Flush();
            }
        }
    }

    internal sealed class EventBus : IEventBus, IDisposable
    {
        private readonly Dictionary<Type, EventChannel> _channels =
            new Dictionary<Type, EventChannel>();
        private readonly IFrameworkLogger _logger;
        private Queue<Action> _readQueue = new Queue<Action>();
        private Queue<Action> _writeQueue = new Queue<Action>();
        private bool _stopped;

        public EventBus(IFrameworkLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public EventBusDiagnostics Diagnostics
        {
            get
            {
                var entries =
                    new Dictionary<Type, EventTypeDiagnostics>(_channels.Count);
                foreach (var pair in _channels)
                {
                    entries.Add(pair.Key, pair.Value.CreateDiagnostics());
                }

                return new EventBusDiagnostics(entries);
            }
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (_stopped)
            {
                return new EventSubscription(new SubscriptionState());
            }

            var eventType = typeof(TEvent);
            var channel = GetOrCreateChannel(eventType);
            var state = new SubscriptionState(
                channel,
                value => handler((TEvent)value));
            channel.Subscriptions.Add(state);
            return new EventSubscription(state);
        }

        public IDisposable Subscribe<TEvent>(
            ModuleScope ownerScope,
            Action<TEvent> handler)
        {
            if (ownerScope == null)
            {
                throw new ArgumentNullException(nameof(ownerScope));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var subscription = Subscribe(handler);
            try
            {
                return ownerScope.Own(subscription);
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }

        public void Publish<TEvent>(TEvent value)
        {
            if (_stopped)
            {
                return;
            }

            Dispatch(typeof(TEvent), value);
        }

        public void Enqueue<TEvent>(TEvent value)
        {
            if (_stopped)
            {
                return;
            }

            _writeQueue.Enqueue(() => Publish(value));
        }

        public void Dispose()
        {
            Stop();
        }

        internal void Flush()
        {
            if (_stopped || _writeQueue.Count == 0)
            {
                return;
            }

            var previousReadQueue = _readQueue;
            _readQueue = _writeQueue;
            _writeQueue = previousReadQueue;

            while (_readQueue.Count > 0 && !_stopped)
            {
                _readQueue.Dequeue()();
            }

            _readQueue.Clear();
        }

        internal void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _readQueue.Clear();
            _writeQueue.Clear();
            foreach (var channel in _channels.Values)
            {
                channel.DetachAll();
            }

            _channels.Clear();
        }

        private void Dispatch(Type eventType, object value)
        {
            var channel = GetOrCreateChannel(eventType);
            var handlers = new Action<object>[channel.Subscriptions.Count];
            for (var index = 0; index < handlers.Length; index++)
            {
                handlers[index] = channel.Subscriptions[index].Handler;
            }

            channel.DispatchCount++;
            channel.LastDispatchUtc = DateTime.UtcNow;
            for (var index = 0; index < handlers.Length; index++)
            {
                try
                {
                    handlers[index](value);
                }
                catch (Exception exception)
                {
                    channel.ExceptionCount++;
                    TryLogHandlerException(eventType, exception);
                }
            }
        }

        private EventChannel GetOrCreateChannel(Type eventType)
        {
            if (!_channels.TryGetValue(eventType, out var channel))
            {
                channel = new EventChannel();
                _channels.Add(eventType, channel);
            }

            return channel;
        }

        private void TryLogHandlerException(Type eventType, Exception exception)
        {
            try
            {
                _logger.Error(
                    BuiltInModuleIds.EventBus,
                    eventType.FullName,
                    "An event handler threw during dispatch.",
                    exception);
            }
            catch
            {
                // Logger failures must not break event handler isolation.
            }
        }

        private sealed class EventChannel
        {
            public List<SubscriptionState> Subscriptions { get; } =
                new List<SubscriptionState>();

            public long DispatchCount { get; set; }

            public long ExceptionCount { get; set; }

            public DateTime? LastDispatchUtc { get; set; }

            public EventTypeDiagnostics CreateDiagnostics()
            {
                return new EventTypeDiagnostics(
                    Subscriptions.Count,
                    DispatchCount,
                    ExceptionCount,
                    LastDispatchUtc);
            }

            public void Detach(SubscriptionState state)
            {
                Subscriptions.Remove(state);
            }

            public void DetachAll()
            {
                while (Subscriptions.Count > 0)
                {
                    Subscriptions[Subscriptions.Count - 1].Detach();
                }
            }
        }

        private sealed class SubscriptionState : IEventSubscriptionState
        {
            private EventChannel _channel;
            private Action<object> _handler;

            public SubscriptionState()
            {
            }

            public SubscriptionState(
                EventChannel channel,
                Action<object> handler)
            {
                _channel =
                    channel ?? throw new ArgumentNullException(nameof(channel));
                _handler =
                    handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public Action<object> Handler => _handler;

            public void Detach()
            {
                var channel = _channel;
                _channel = null;
                _handler = null;
                channel?.Detach(this);
            }
        }
    }
}
