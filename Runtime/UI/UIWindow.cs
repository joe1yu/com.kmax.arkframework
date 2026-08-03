using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework
{
    public abstract class UIWindow : MonoBehaviour
    {
        private readonly List<IDisposable> _subscriptions =
            new List<IDisposable>();
        private IEventBus _eventBus;
        private bool _acceptSubscriptions;

        public CancellationToken LifetimeToken { get; internal set; }

        protected internal virtual ValueTask OnOpenAsync(
            object parameter,
            CancellationToken token)
        {
            return default;
        }

        protected internal virtual ValueTask OnCloseAsync(
            CancellationToken token)
        {
            return default;
        }

        protected IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_acceptSubscriptions || _eventBus == null)
            {
                throw new InvalidOperationException(
                    "Window subscriptions are only available while the window is opening or open.");
            }

            var subscription = _eventBus.Subscribe(handler);
            _subscriptions.Add(subscription);
            return subscription;
        }

        internal void BeginLifetime(
            IEventBus eventBus,
            CancellationToken lifetimeToken)
        {
            EndSubscriptions();
            _eventBus =
                eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            LifetimeToken = lifetimeToken;
            _acceptSubscriptions = true;
        }

        internal Exception EndSubscriptions()
        {
            _acceptSubscriptions = false;
            Exception failure = null;
            for (var index = _subscriptions.Count - 1; index >= 0; index--)
            {
                try
                {
                    _subscriptions[index]?.Dispose();
                }
                catch (Exception exception)
                {
                    failure = failure == null
                        ? exception
                        : new AggregateException(failure, exception);
                }
            }

            _subscriptions.Clear();
            _eventBus = null;
            return failure;
        }
    }
}
