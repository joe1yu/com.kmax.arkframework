using System;

namespace ArkFramework
{
    internal interface IEventSubscriptionState
    {
        void Detach();
    }

    public sealed class EventSubscription : IDisposable
    {
        private IEventSubscriptionState _state;

        internal EventSubscription(IEventSubscriptionState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public void Dispose()
        {
            var state = _state;
            if (state == null)
            {
                return;
            }

            _state = null;
            state.Detach();
        }
    }
}
